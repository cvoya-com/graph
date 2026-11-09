// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Execution;

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Entities;
using Cvoya.Graph.Age.Querying.Cypher.Builders;
using Cvoya.Graph.Age.Querying.Cypher.Lowering;
using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.Cypher;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Planning;
using Cvoya.Graph.Querying;
using Cvoya.Graph.Querying.Commands;
using Cvoya.Graph.Serialization;
using Cvoya.Graph.Serialization.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


internal sealed class CypherEngine
{
    private enum ElementTerminal
    {
        None,
        First,
        FirstOrDefault,
        Last,
        Single,
        SingleOrDefault,
    }

    private static readonly Dictionary<MethodInfo, ElementTerminal> ElementTerminalMethods = CreateElementTerminalMethods();

    private static readonly HashSet<MethodInfo> NonEmptyAggregateTerminalMethods = CreateNonEmptyAggregateTerminalMethods();

    private readonly EntityFactory _entityFactory;
    private readonly ILogger<CypherEngine> _logger;
    private readonly CypherExecutor _executor;
    private readonly GraphResultMaterializer _materializer;
    private readonly AgeRecordAdapter _recordAdapter = new();
    private readonly ILoggerFactory? _loggerFactory;
    private readonly AgeFullTextSearchRewriter _fullTextSearchRewriter;
    private readonly SchemaRegistry _schemaRegistry;
    private readonly string _graphName;
    private readonly ComplexPropertyManager _complexPropertyManager;

    public CypherEngine(
        EntityFactory entityFactory,
        SchemaRegistry schemaRegistry,
        string graphName,
        ComplexPropertyManager complexPropertyManager,
        ILoggerFactory? loggerFactory)
    {
        _entityFactory = entityFactory ?? throw new ArgumentNullException(nameof(entityFactory));
        _logger = loggerFactory?.CreateLogger<CypherEngine>() ?? NullLogger<CypherEngine>.Instance;

        // Create our internal components
        _executor = new CypherExecutor(loggerFactory);
        _materializer = new GraphResultMaterializer(entityFactory, loggerFactory);
        _loggerFactory = loggerFactory;
        _schemaRegistry = schemaRegistry ?? throw new ArgumentNullException(nameof(schemaRegistry));
        _graphName = graphName ?? throw new ArgumentNullException(nameof(graphName));
        _complexPropertyManager = complexPropertyManager ?? throw new ArgumentNullException(nameof(complexPropertyManager));
        _fullTextSearchRewriter = new AgeFullTextSearchRewriter(schemaRegistry);
    }

    internal static void ValidateMutation(GraphMutationModel mutation)
    {
        _ = new CypherQueryPlanner(AgeDialect.CommandPlanningInstance).Plan(mutation.Selection);
        if (mutation.Kind == GraphMutationKind.Update ||
            mutation.Selection.ElementKind == GraphElementKind.Relationship)
        {
            _ = new CypherMutationPlanner(AgeDialect.PlanningInstance).Plan(mutation, []);
        }
    }

    internal async Task<IReadOnlyList<SelectedGraphElement>> SelectNativeAsync(
        GraphElementSelectionModel selection,
        Expression sourceExpression,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken)
    {
        var rewritten = await _fullTextSearchRewriter
            .RewriteAsync(sourceExpression, transaction.Runner, cancellationToken)
            .ConfigureAwait(false);
        if (!ReferenceEquals(rewritten, sourceExpression))
        {
            selection = new GraphElementSelectionModel(
                GraphQueryModelBuilder.Build(rewritten),
                selection.Mode);
            GraphElementSelectionModelValidator.Validate(selection);
        }

        return await SelectPlannedAsync(selection, transaction, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<int> ApplyMutationAsync(
        GraphMutationModel mutation,
        Expression mutationExpression,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken)
    {
        var complexPlan = mutation.Kind == GraphMutationKind.Update
            ? ComplexPropertyMutationPlan.Create(_entityFactory, mutation)
            : null;
        var rewritten = await _fullTextSearchRewriter
            .RewriteAsync(mutationExpression, transaction.Runner, cancellationToken)
            .ConfigureAwait(false);
        if (!ReferenceEquals(rewritten, mutationExpression))
        {
            mutation = GraphMutationModelBuilder.Build(rewritten);
        }

        var selected = await SelectPlannedAsync(mutation.Selection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (selected.Count == 0)
        {
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var nativeIdentities = selected.Select(item => item.NativeIdentity).ToArray();
        var constraintPlan = GraphMutationConstraintPlan.Create(mutation, _schemaRegistry);
        if (mutation.Kind == GraphMutationKind.Delete &&
            mutation.Selection.ElementKind == GraphElementKind.Node)
        {
            await DeleteNodesAsync(
                nativeIdentities.Cast<long>().ToArray(),
                mutation.CascadeDelete,
                transaction,
                cancellationToken).ConfigureAwait(false);
        }
        else if (mutation.Kind == GraphMutationKind.Update)
        {
            var updateComplexPlan = complexPlan ?? throw new InvalidOperationException(
                "An update mutation must have a complex-property plan.");
            if (!constraintPlan.IsEmpty)
            {
                var rows = await ReadConstraintRowsAsync(
                    mutation,
                    nativeIdentities,
                    constraintPlan,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                var proposed = constraintPlan.ValidateFinalValues(rows);
                await ValidateUnselectedConstraintsAsync(
                    nativeIdentities.Cast<long>().ToArray(),
                    constraintPlan,
                    proposed,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (updateComplexPlan.HasWork)
            {
                // Complex replacement spans multiple AGE statements. Lock every frozen parent in
                // the same global order as constrained and entity-manager updates before any scalar
                // cleanup, subtree deletion, or value-node creation can interleave.
                await transaction.Runner.AcquireElementLocksAsync(
                    nativeIdentities.Cast<long>().ToArray(),
                    relationship: false,
                    cancellationToken).ConfigureAwait(false);
            }

            var scalarMutation = BuildScalarMutation(mutation);
            if (scalarMutation.Assignments.Count > 0)
            {
                var statement = new CypherMutationPlanner(AgeDialect.PlanningInstance)
                    .Plan(scalarMutation, nativeIdentities);
                _ = await ExecuteStatementAsync(statement, transaction, cancellationToken).ConfigureAwait(false);
            }

            if (updateComplexPlan.HasWork)
            {
                await _complexPropertyManager.ReplaceGraphIdBoundComplexPropertiesAsync(
                    transaction.Runner,
                    nativeIdentities.Cast<long>().ToArray(),
                    updateComplexPlan.RelationshipTypesToClear,
                    updateComplexPlan.PropertyNamesToClear,
                    updateComplexPlan.ReplacementEntity,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            var statement = new CypherMutationPlanner(AgeDialect.PlanningInstance)
                .Plan(mutation, nativeIdentities);
            _ = await ExecuteStatementAsync(statement, transaction, cancellationToken).ConfigureAwait(false);
        }

        return selected.Count;
    }

    private static GraphMutationModel BuildScalarMutation(GraphMutationModel mutation)
    {
        var assignments = mutation.Assignments.SelectMany<GraphPropertyAssignment, GraphPropertyAssignment>(assignment =>
            assignment switch
            {
                GraphConstantPropertyAssignment { IsComplex: true, Dynamic: true } complex =>
                [new GraphConstantPropertyAssignment(
                    complex.PropertySelector,
                    complex.Property,
                    complex.StorageName,
                    dynamic: true,
                    value: null)],
                GraphConstantPropertyAssignment { IsComplex: true } => [],
                _ => [assignment],
            }).ToArray();
        return new GraphMutationModel(
            mutation.Kind,
            mutation.Selection,
            assignments,
            mutation.CascadeDelete);
    }

    private async Task<IReadOnlyList<GraphMutationConstraintRow>> ReadConstraintRowsAsync(
        GraphMutationModel mutation,
        IReadOnlyList<object> nativeIdentities,
        GraphMutationConstraintPlan constraintPlan,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken)
    {
        await transaction.Runner.AcquireElementLocksAsync(
            nativeIdentities.Cast<long>().ToArray(),
            mutation.Selection.ElementKind == GraphElementKind.Relationship,
            cancellationToken).ConfigureAwait(false);
        var statement = new CypherMutationPlanner(AgeDialect.PlanningInstance).PlanConstraintValues(
            mutation,
            nativeIdentities,
            constraintPlan.Properties.Select(property => property.StorageName).ToArray(),
            acquireWriteLock: false);
        var records = await ExecuteStatementAsync(statement, transaction, cancellationToken).ConfigureAwait(false);
        var rows = records.Select(record =>
        {
            var values = constraintPlan.Properties.Select((property, index) =>
            {
                object? value = record[CypherMutationPlanner.ConstraintValueColumn(index)].As<object>();
                return new KeyValuePair<string, object?>(property.StorageName, value);
            }).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            return new GraphMutationConstraintRow(record["__nativeId"].As<long>(), values);
        })
            .ToArray();
        GraphMutationConstraintPlan.ValidateTargetRows(nativeIdentities, rows);
        return rows;
    }

    private async Task ValidateUnselectedConstraintsAsync(
        IReadOnlyList<long> selectedIdentities,
        GraphMutationConstraintPlan constraintPlan,
        IReadOnlyList<GraphMutationConstraintValue> proposed,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!CypherIdentifier.IsNativeLabelName(constraintPlan.LabelOrType))
        {
            throw new GraphQueryTranslationException(
                $"AGE atomic key and unique updates require a native label or relationship type; " +
                $"'{constraintPlan.LabelOrType}' uses non-native fallback storage.");
        }

        var entityKind = constraintPlan.ElementKind == GraphElementKind.Node
            ? AgeUniquenessLockKey.NodeEntityKind
            : AgeUniquenessLockKey.RelationshipEntityKind;
        await transaction.Runner.AcquireUniquenessLocksAsync(
            proposed.Select(candidate => AgeUniquenessLockKey.Compute(
                _graphName,
                entityKind,
                constraintPlan.LabelOrType,
                candidate.Constraint.Description,
                candidate.Values)).ToArray(),
            cancellationToken).ConfigureAwait(false);

        var escapedName = CypherIdentifier.Escape(
            constraintPlan.LabelOrType,
            constraintPlan.ElementKind == GraphElementKind.Node ? "node label" : "relationship type");
        var match = constraintPlan.ElementKind == GraphElementKind.Node
            ? $"MATCH (candidate:{escapedName})"
            : $"MATCH ()-[candidate:{escapedName}]->()";
        foreach (var candidate in proposed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = new Dictionary<string, object?>
            {
                ["selectedIds"] = selectedIdentities,
            };
            var predicates = new List<string>
            {
                "size([selectedId IN $selectedIds WHERE id(candidate) = selectedId]) = 0",
            };
            for (var index = 0; index < candidate.Constraint.Properties.Count; index++)
            {
                var parameterName = $"constraintValue{index}";
                parameters[parameterName] = candidate.Values[index];
                var storageName = SimpleCollectionStorageCodec.GetPayloadPropertyName(
                    candidate.Constraint.Properties[index].StorageName);
                predicates.Add(
                    $"candidate.{CypherIdentifier.Escape(storageName, "property name")} = ${parameterName}");
            }

            var records = await _executor.ExecuteAsync(
                $"{match} WHERE {string.Join(" AND ", predicates)} RETURN count(candidate) AS duplicateCount",
                parameters,
                ["duplicateCount"],
                transaction,
                cancellationToken).ConfigureAwait(false);
            if (records.Single()["duplicateCount"].As<long>() > 0)
            {
                throw constraintPlan.CreateViolation(candidate.Constraint);
            }
        }
    }

    private async Task<IReadOnlyList<SelectedGraphElement>> SelectPlannedAsync(
        GraphElementSelectionModel selection,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken)
    {
        var statement = new CypherQueryPlanner(AgeDialect.CommandPlanningInstance).Plan(selection);
        var records = await ExecuteStatementAsync(statement, transaction, cancellationToken).ConfigureAwait(false);
        return records.Select(record => new SelectedGraphElement(
            selection.ElementKind,
            record["__nativeId"].As<long>())).ToArray();
    }

    private async Task DeleteNodesAsync(
        IReadOnlyList<long> nativeIdentities,
        bool cascadeDelete,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["targetIds"] = nativeIdentities,
        };
        if (!cascadeDelete)
        {
            // AGE 1.8 crashes on a list parameter inside IN ($targetIds), so the preflight
            // unwinds the list instead. Two directed matches are required because a deleted
            // node can be either endpoint. Collecting DISTINCT relationship ids before the
            // final count keeps self-relationships (and relationships between two selected
            // targets) from being counted twice. Complex-property marker edges stay excluded.
            var preflight = $"""
                UNWIND $targetIds AS targetId
                MATCH (target)
                WHERE id(target) = targetId
                OPTIONAL MATCH (target)-[outgoing]->()
                WHERE coalesce(outgoing.{ComplexPropertyStorage.RelationshipMarkerProperty}, false) = false
                WITH target, collect(DISTINCT id(outgoing)) AS outgoingIds
                OPTIONAL MATCH (target)<-[incoming]-()
                WHERE coalesce(incoming.{ComplexPropertyStorage.RelationshipMarkerProperty}, false) = false
                WITH target, outgoingIds, collect(DISTINCT id(incoming)) AS incomingIds
                WITH target, outgoingIds + incomingIds AS relationshipIds
                UNWIND relationshipIds AS relationshipId
                RETURN count(DISTINCT relationshipId) AS relationshipCount
                """;
            var records = await _executor.ExecuteAsync(
                preflight,
                parameters,
                ["relationshipCount"],
                transaction,
                cancellationToken).ConfigureAwait(false);
            var relationshipCount = records.Single()["relationshipCount"].As<long>();
            if (relationshipCount > 0)
            {
                throw new GraphException(
                    $"Cannot delete the selected nodes because they have {relationshipCount} incident user relationship(s). " +
                    "Delete those relationships first or use cascade delete.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var propertyQuery = $"""
            UNWIND $targetIds AS targetId
            MATCH (target)
            WHERE id(target) = targetId
            MATCH (target)-[propertyRelationships*1..{GraphDataModel.DefaultDepthAllowed}]->(propertyNode)
            WHERE size([age_hop IN range(0, size(propertyRelationships) - 1) WHERE propertyRelationships[toInteger(age_hop)].{ComplexPropertyStorage.RelationshipMarkerProperty} = true]) = size(propertyRelationships)
            RETURN DISTINCT id(propertyNode) AS propertyNodeId
            """;
        var propertyNodes = await _executor.ExecuteAsync(
            propertyQuery,
            parameters,
            ["propertyNodeId"],
            transaction,
            cancellationToken).ConfigureAwait(false);
        foreach (var propertyNode in propertyNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await _executor.ExecuteAsync(
                "MATCH (propertyNode) WHERE id(propertyNode) = $propertyNodeId DETACH DELETE propertyNode RETURN true AS deleted",
                new Dictionary<string, object?>
                {
                    ["propertyNodeId"] = propertyNode["propertyNodeId"].As<long>(),
                },
                ["deleted"],
                transaction,
                cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _ = await _executor.ExecuteAsync(
            "UNWIND $targetIds AS targetId MATCH (target) WHERE id(target) = targetId DETACH DELETE target RETURN count(*) AS affectedCount",
            parameters,
            ["affectedCount"],
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<AgeRecord>> ExecuteStatementAsync(
        CypherStatement statement,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken)
    {
        statement = CypherQueryVisitor.LowerStatement(statement, _schemaRegistry.HasComplexProperties);
        var rendered = new CypherRenderer(AgeDialect.Instance).Render(statement);
        var projectionColumns = rendered.ProjectionColumns
            .Select(AgeEntityProjectionPass.NormalizeProjectionColumn)
            .ToArray();
        var parameterBuilder = new CypherParameterBuilder(_entityFactory, _loggerFactory);
        var converted = rendered.Parameters.ToDictionary(
            pair => pair.Key,
            pair => parameterBuilder.BuildParameterValue(pair.Value),
            StringComparer.Ordinal);
        return await _executor.ExecuteAsync(
            rendered.Text,
            converted,
            projectionColumns,
            transaction,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> ExecuteAsync<T>(
            Expression expression,
            AgeGraphTransaction transaction,
            CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebugCypherEngine60(typeof(T).Name);

            if (AgeMixedSearchRootExpression.TryFind(expression, out var mixedRoot))
            {
                ValidateMixedSearchExpression(expression);
                var entities = await LoadMixedSearchEntitiesAsync(
                    mixedRoot!, transaction, cancellationToken).ConfigureAwait(false);
                return AgeMixedSearchEvaluator.Evaluate<T>(expression, entities);
            }

            // Lower any full-text Search operator out of the expression tree (phase 1 + rewrite to a
            // Where over the matched ids) before the shared pipeline runs. Unchanged only when there
            // is no Search; typed search traversal continues over the rewritten node source.
            expression = await _fullTextSearchRewriter
                .RewriteAsync(expression, transaction.Runner, cancellationToken).ConfigureAwait(false);

            // Build the Cypher query from the expression
            var cypherQuery = BuildCypherQuery(typeof(T), expression, _loggerFactory);

            LogCypherQuery(cypherQuery);

            // Execute the query
            var records = await _executor.ExecuteAsync(
                cypherQuery.Text,
                cypherQuery.Parameters,
                cypherQuery.ProjectionColumns ?? [],
                cypherQuery.PostProcessing,
                transaction,
                cancellationToken).ConfigureAwait(false);

            ValidateElementTerminalRecordCount(GetElementTerminal(expression), records.Count);
            ValidateNonEmptyAggregateValue<T>(expression, records);

            // Let the materializer handle everything - no need to duplicate logic here
            var adapted = _recordAdapter.Adapt(records);
            if (cypherQuery.SumUnionCountBranches)
            {
                adapted = CollapseUnionCountBranches(adapted);
            }

            var result = await _materializer.MaterializeAsync<T>(
                adapted,
                cypherQuery.GraphPathTypes,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogErrorCypherEngine91(ex, typeof(T).Name);
            throw;
        }
    }

    public async IAsyncEnumerable<T> StreamAsync<T>(
        Expression expression,
        AgeGraphTransaction transaction,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebugCypherEngine103(typeof(T).Name);

        if (AgeMixedSearchRootExpression.TryFind(expression, out var mixedRoot))
        {
            ValidateMixedSearchExpression(expression);
            var entities = await LoadMixedSearchEntitiesAsync(
                mixedRoot!, transaction, cancellationToken).ConfigureAwait(false);
            foreach (var item in AgeMixedSearchEvaluator.EvaluateSequence<T>(expression, entities))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            yield break;
        }

        // See ExecuteAsync: lower full-text search before building the query.
        expression = await _fullTextSearchRewriter
            .RewriteAsync(expression, transaction.Runner, cancellationToken).ConfigureAwait(false);

        var cypherQuery = BuildCypherQuery(typeof(T), expression, _loggerFactory);
        LogCypherQuery(cypherQuery);

        var records = CypherExecutor.StreamAsync(
            cypherQuery.Text,
            cypherQuery.Parameters,
            cypherQuery.ProjectionColumns ?? [],
            cypherQuery.PostProcessing,
            transaction,
            cancellationToken);

        if (cypherQuery.GraphPathTypes is { } graphPathTypes && typeof(T) == typeof(IGraphPath))
        {
            await foreach (var path in StreamGraphPathsAsync(records, graphPathTypes, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return (T)path;
            }

            yield break;
        }

        await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var adaptedRecord = _recordAdapter.Adapt(record);
            var item = await _materializer
                .MaterializeRecordAsync<T>(adaptedRecord, cancellationToken).ConfigureAwait(false);

            if (item is not null)
            {
                yield return item;
            }
        }
    }

    private async IAsyncEnumerable<IGraphPath> StreamGraphPathsAsync(
        IAsyncEnumerable<AgeRecord> records,
        (Type Source, Type Relationship, Type Target) graphPathTypes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<GraphResultProcessor.GraphPathHop>? currentHops = null;
        int? currentPathIndex = null;

        await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // TraversePaths orders rows by pathIndex, then hopIndex, so a path can be emitted once its index changes.
            var hop = _materializer.ProcessGraphPathHop(
                _recordAdapter.Adapt(record),
                graphPathTypes,
                cancellationToken);
            if (currentPathIndex is int pathIndex && hop.PathIndex != pathIndex)
            {
                yield return _materializer.MaterializeGraphPath(currentHops!, graphPathTypes);
                currentHops = [];
            }

            currentPathIndex = hop.PathIndex;
            currentHops ??= [];
            currentHops.Add(hop);
        }

        if (currentHops is { Count: > 0 })
        {
            yield return _materializer.MaterializeGraphPath(currentHops, graphPathTypes);
        }
    }

    private static Dictionary<MethodInfo, ElementTerminal> CreateElementTerminalMethods()
    {
        var methods = new Dictionary<MethodInfo, ElementTerminal>();

        AddAll(nameof(QueryTerminals.FirstAsyncMarker), ElementTerminal.First);
        AddAll(nameof(QueryTerminals.FirstOrDefaultAsyncMarker), ElementTerminal.FirstOrDefault);
        AddAll(nameof(QueryTerminals.LastAsyncMarker), ElementTerminal.Last);
        AddAll(nameof(QueryTerminals.SingleAsyncMarker), ElementTerminal.Single);
        AddAll(nameof(QueryTerminals.SingleOrDefaultAsyncMarker), ElementTerminal.SingleOrDefault);

        return methods;

        void AddAll(string name, ElementTerminal terminal)
        {
            foreach (var method in typeof(QueryTerminals).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == name))
            {
                methods[method] = terminal;
            }
        }
    }

    private static HashSet<MethodInfo> CreateNonEmptyAggregateTerminalMethods()
    {
        // Min, Max, and non-nullable Average require a non-empty sequence: each returns a single
        // record whose aggregate value is null over zero rows, which must surface as an empty-
        // sequence error rather than default(T). Sum and Count are excluded — they return 0.
        return typeof(QueryTerminals)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name is nameof(QueryTerminals.MinAsyncMarker) or nameof(QueryTerminals.MaxAsyncMarker)
                or nameof(QueryTerminals.AverageAsyncMarker))
            .ToHashSet();
    }

    private static ElementTerminal GetElementTerminal(Expression expression)
    {
        if (expression is not MethodCallExpression methodCall)
            return ElementTerminal.None;

        var key = methodCall.Method.IsGenericMethod
            ? methodCall.Method.GetGenericMethodDefinition()
            : methodCall.Method;

        return ElementTerminalMethods.TryGetValue(key, out var terminal)
            ? terminal
            : ElementTerminal.None;
    }

    /// <summary>
    /// Sums the per-physical-label <c>count(...)</c> rows produced by the AGE UNION ALL count
    /// expansion into the one scalar a LINQ count terminal expects. Apache AGE cannot aggregate
    /// over a union at the Cypher level and does not support <c>CALL { }</c> on every supported
    /// version, so the provider performs this last addition over the disjoint branch counts.
    /// </summary>
    private static List<GraphRecord> CollapseUnionCountBranches(List<GraphRecord> records)
    {
        if (records.Count <= 1 || records[0].Values.Count != 1)
        {
            return records;
        }

        var column = records[0].Keys.Single();
        long total = 0;
        foreach (var record in records)
        {
            if (record.Values.Count != 1 ||
                record[column] is not { Kind: GraphValueKind.Scalar } value)
            {
                return records;
            }

            var increment = value.ScalarValue switch
            {
                long longValue => longValue,
                int intValue => intValue,
                short shortValue => shortValue,
                sbyte sbyteValue => sbyteValue,
                byte byteValue => byteValue,
                ushort ushortValue => ushortValue,
                uint uintValue => uintValue,
                ulong ulongValue when ulongValue <= long.MaxValue => (long)ulongValue,
                _ => (long?)null,
            };

            if (increment is null)
            {
                return records;
            }

            total = checked(total + increment.Value);
        }

        return
        [
            new GraphRecord(new Dictionary<string, GraphValue>(StringComparer.Ordinal)
            {
                [column] = GraphValue.Scalar(total),
            }),
        ];
    }

    private static void ValidateNonEmptyAggregateValue<T>(Expression expression, List<AgeRecord> records)
    {
        if (!typeof(T).IsValueType || Nullable.GetUnderlyingType(typeof(T)) is not null)
            return;

        if (!IsNonEmptyAggregateTerminal(expression))
            return;

        // min()/max()/avg() over zero rows yields a single record holding a null aggregate value,
        // not zero records, so the empty-sequence signal cannot come from the record count. A
        // nullable result type (handled above) legitimately materializes that null; a non-nullable
        // one must raise the standard empty-sequence error instead of returning default(T).
        if (records is [var record] && record.Values.Count == 1 && record.Values.Values.First() is null)
            throw new InvalidOperationException("Sequence contains no elements");
    }

    private static bool IsNonEmptyAggregateTerminal(Expression expression)
    {
        if (expression is not MethodCallExpression methodCall)
            return false;

        var key = methodCall.Method.IsGenericMethod
            ? methodCall.Method.GetGenericMethodDefinition()
            : methodCall.Method;

        return NonEmptyAggregateTerminalMethods.Contains(key);
    }

    private static void ValidateElementTerminalRecordCount(ElementTerminal terminal, int recordCount)
    {
        switch (terminal)
        {
            case ElementTerminal.First when recordCount == 0:
            case ElementTerminal.Last when recordCount == 0:
            case ElementTerminal.Single when recordCount == 0:
                throw new InvalidOperationException("Sequence contains no elements");

            case ElementTerminal.Single when recordCount > 1:
            case ElementTerminal.SingleOrDefault when recordCount > 1:
                throw new InvalidOperationException("Sequence contains more than one element");

            case ElementTerminal.None:
            case ElementTerminal.First:
            case ElementTerminal.FirstOrDefault:
            case ElementTerminal.Last:
            case ElementTerminal.Single:
            case ElementTerminal.SingleOrDefault:
                return;
        }
    }

    private void LogCypherQuery(CypherQuery cypherQuery)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var parameterNames = cypherQuery.Parameters.Keys.ToArray();
            _logger.LogDebugCypherEngine274(cypherQuery.Text);
            _logger.LogDebugCypherEngine275(parameterNames, parameterNames.Length);
        }

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTraceCypherEngine283(cypherQuery.Parameters);
        }
    }

    private CypherQuery BuildCypherQuery(Type type, Expression expression, ILoggerFactory? loggerFactory = null)
    {
        // Extract the element type from the expression if the type is a collection
        var elementType = ExtractElementType(type, expression);

        var visitor = new CypherQueryVisitor(elementType, loggerFactory, _schemaRegistry.HasComplexProperties);
        visitor.Visit(expression);

        var query = visitor.Query;

        var paramBuilder = new CypherParameterBuilder(_entityFactory);
        var convertedParams = new Dictionary<string, object?>();

        foreach (var (key, value) in query.Parameters)
        {
            convertedParams[key] = paramBuilder.BuildParameterValue(value);
        }

        return new CypherQuery(
            query.Text,
            convertedParams,
            query.GraphPathTypes,
            query.ProjectionColumns,
            query.PostProcessing,
            query.SumUnionCountBranches);
    }

    private async Task<IReadOnlyList<IEntity>> LoadMixedSearchEntitiesAsync(
        AgeMixedSearchRootExpression root,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken)
    {
        var nodeMatches = await AgeFullTextSearch.FindMatchesAsync(
            typeof(INode), root.SearchQuery, _schemaRegistry, transaction.Runner, cancellationToken)
            .ConfigureAwait(false);
        var relationshipMatches = await AgeFullTextSearch.FindMatchesAsync(
            typeof(IRelationship), root.SearchQuery, _schemaRegistry, transaction.Runner, cancellationToken)
            .ConfigureAwait(false);
        var combinedMatchCount = nodeMatches
            .Concat(relationshipMatches)
            .Select(match => (match.Target, match.GraphId))
            .Distinct()
            .Count();
        AgeFullTextSearch.EnforceIdSetLimit(combinedMatchCount);

        var nodeGraphIds = nodeMatches.Select(match => match.GraphId).Distinct().ToArray();
        var relationshipGraphIds = relationshipMatches.Select(match => match.GraphId).Distinct().ToArray();

        var nodes = nodeGraphIds.Length == 0
            ? []
            : await ExecuteAsync<List<INode>>(
                AgeFullTextSearchRewriter.BuildGraphIdFilter(root.NodeSource, typeof(INode), nodeGraphIds),
                transaction,
                cancellationToken).ConfigureAwait(false) ?? [];
        var relationships = relationshipGraphIds.Length == 0
            ? []
            : await ExecuteAsync<List<IRelationship>>(
                AgeFullTextSearchRewriter.BuildGraphIdFilter(
                    root.RelationshipSource, typeof(IRelationship), relationshipGraphIds),
                transaction,
                cancellationToken).ConfigureAwait(false) ?? [];

        return [.. nodes.Cast<IEntity>(), .. relationships.Cast<IEntity>()];
    }

    private static void ValidateMixedSearchExpression(Expression expression)
    {
        var model = GraphQueryModelBuilder.Build(expression);
        GraphQueryModelValidator.Validate(model);
    }

    private static Type ExtractElementType(Type resultType, Expression expression)
    {
        // If the result type is a collection (List<T>, IEnumerable<T>, etc.),
        // extract the element type
        if (resultType.IsGenericType)
        {
            var genericArgs = resultType.GetGenericArguments();
            if (genericArgs.Length == 1)
            {
                return genericArgs[0];
            }
        }

        // Try to extract from the expression tree by finding the first queryable constant
        return ExtractElementTypeFromExpression(expression) ?? resultType;
    }

    private static Type? ExtractElementTypeFromExpression(Expression expression)
    {
        if (expression is ConstantExpression constant &&
            constant.Value is IQueryable queryable)
        {
            return queryable.ElementType;
        }

        if (expression is MethodCallExpression methodCall)
        {
            // Check the object of the method call
            if (methodCall.Object != null)
            {
                var objType = ExtractElementTypeFromExpression(methodCall.Object);
                if (objType != null) return objType;
            }

            // Check the arguments
            var argumentType = methodCall.Arguments
                .Select(ExtractElementTypeFromExpression)
                .FirstOrDefault(type => type != null);
            if (argumentType != null) return argumentType;
        }

        if (expression is UnaryExpression unary)
        {
            return ExtractElementTypeFromExpression(unary.Operand);
        }

        return null;
    }
}
