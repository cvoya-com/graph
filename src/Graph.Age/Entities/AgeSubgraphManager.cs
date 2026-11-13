// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Entities;

using System.Security.Cryptography;
using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Serialization;
using Cvoya.Graph.Querying;
using Cvoya.Graph.Querying.Commands;
using Cvoya.Graph.Serialization;

/// <summary>
/// Creates new-endpoint and hybrid endpoint–relationship–endpoint subgraphs through one Npgsql
/// batch execution. Later commands bind nodes through operation-local transient correlation rather
/// than through public domain properties.
/// </summary>
internal sealed class AgeSubgraphManager(AgeGraphContext context)
{
    private const string TransientCorrelationPrefix = "__graphModelSubgraphCorrelation";

    private readonly ComplexPropertyManager complexPropertyManager = new(context);

    private sealed record EndpointPlan(
        string Name,
        long? GraphId,
        Graph.INode? Node,
        EntityInfo? Entity,
        string? CorrelationToken,
        string? StorageLabel)
    {
        public bool IsNew => Entity is not null;
    }

    internal async Task CreateSubgraphAsync(
        GraphCommandEndpoint source,
        Graph.IRelationship relationship,
        GraphCommandEndpoint target,
        RelationshipDirection direction,
        GraphRelationshipCreationMode mode,
        AgeGraphTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(relationship);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var relationshipEntity = SerializeRelationship(relationship);
        var relationshipStorageType = SerializationBridge.GetRootStorageName(
            relationshipEntity.Label,
            relationship: true);
        var correlationPropertyName = CreateCorrelationPropertyName();
        var sourcePlan = PlanEndpoint(source, GraphEndpointRole.Source);
        var targetPlan = mode == GraphRelationshipCreationMode.SelfLoop
            ? PlanSelfLoopTarget(source, target, sourcePlan)
            : PlanEndpoint(target, GraphEndpointRole.Target);
        var newEndpoints = mode == GraphRelationshipCreationMode.SelfLoop
            ? new[] { sourcePlan }
            : new[] { sourcePlan, targetPlan };
        newEndpoints = newEndpoints.Where(endpoint => endpoint.IsNew).ToArray();
        if (newEndpoints.Length == 0)
        {
            throw new GraphException("A batched AGE subgraph command requires at least one new endpoint.");
        }

        var sourceChecks = BuildNodeUniquenessChecks(sourcePlan);
        var targetChecks = mode == GraphRelationshipCreationMode.SelfLoop
            ? []
            : BuildNodeUniquenessChecks(targetPlan);
        var relationshipChecks = context.RelationshipManager.BuildRelationshipUniquenessChecks(
            relationship,
            excludeGraphId: null);
        ValidateNewEndpointConstraintCollisions(sourceChecks, targetChecks);

        var valueNodeLevels = complexPropertyManager.PlanSubgraphValueNodes(
            newEndpoints.Select(endpoint => (
                endpoint.CorrelationToken!,
                endpoint.StorageLabel!,
                endpoint.Entity!)).ToArray(),
            CreateCorrelationToken);
        var commands = BuildCommands(
            sourcePlan,
            targetPlan,
            direction,
            mode,
            relationshipEntity,
            relationshipStorageType,
            sourceChecks,
            targetChecks,
            relationshipChecks,
            valueNodeLevels,
            correlationPropertyName,
            newEndpoints);

        // Every write path takes uniqueness locks before label provisioning. Keep the same ordering
        // here so first-use label DDL cannot deadlock against another constrained writer.
        await transaction.Runner.AcquireUniquenessLocksAsync(
            sourceChecks.Concat(targetChecks).Concat(relationshipChecks)
                .Select(check => check.LockKey)
                .ToArray(),
            cancellationToken).ConfigureAwait(false);

        foreach (var nodeLabel in newEndpoints
                     .Select(endpoint => endpoint.StorageLabel!)
                     .Distinct(StringComparer.Ordinal))
        {
            await transaction.Runner
                .EnsureLabelAsync(nodeLabel, relationship: false, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.Runner
            .EnsureLabelAsync(relationshipStorageType, relationship: true, cancellationToken)
            .ConfigureAwait(false);
        if (valueNodeLevels.Count > 0)
        {
            await transaction.Runner
                .EnsureLabelAsync(SerializationBridge.ComplexNodeLabel, relationship: false, cancellationToken)
                .ConfigureAwait(false);
            await transaction.Runner
                .EnsureLabelAsync(SerializationBridge.ComplexRelationshipType, relationship: true, cancellationToken)
                .ConfigureAwait(false);
        }

        var results = await transaction.Runner.RunBatchAsync(commands, cancellationToken).ConfigureAwait(false);
        await SyncNewEndpointIndexesAsync(
            transaction, newEndpoints, results, cancellationToken).ConfigureAwait(false);
        ValidateResults(
            results,
            sourceChecks,
            targetChecks,
            relationshipChecks,
            valueNodeLevels,
            newEndpoints,
            relationship.GetType().Name);
    }

    /// <summary>
    /// Refreshes declared index rows for roots created by the batch. The batch captures each new
    /// root's graphid in its creation command, so no additional token lookup is needed before or
    /// after the cleanup command removes those tokens.
    /// </summary>
    private static async Task SyncNewEndpointIndexesAsync(
        AgeGraphTransaction transaction,
        IReadOnlyList<EndpointPlan> endpoints,
        IReadOnlyList<AgeBatchResult> results,
        CancellationToken cancellationToken)
    {
        var resultMap = results.ToDictionary(result => result.Name, StringComparer.Ordinal);
        var byLabel = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            if (endpoint.StorageLabel is null || endpoint.CorrelationToken is null)
            {
                continue;
            }

            if (!resultMap.TryGetValue($"{endpoint.Name}_root", out var result) ||
                result.Records.Count != 1)
            {
                throw new GraphException(
                    $"AGE batch result '{endpoint.Name}_root' was missing or did not contain exactly one record.");
            }

            if (!byLabel.TryGetValue(endpoint.StorageLabel, out var ids))
            {
                ids = [];
                byLabel[endpoint.StorageLabel] = ids;
            }

            ids.Add(result.Records[0]["id"].As<long>());
        }

        foreach (var (label, ids) in byLabel)
        {
            await transaction.Runner.SyncIndexAsync(ids, [label], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Plans one endpoint operand. The role names the plan, and that name prefixes the endpoint's
    /// batch command, so a plan can never be created under one role and validated as the other.
    /// </summary>
    private EndpointPlan PlanEndpoint(GraphCommandEndpoint endpoint, GraphEndpointRole role)
    {
        var name = role.ToString().ToLowerInvariant();
        return endpoint switch
        {
            SelectedGraphCommandEndpoint
            {
                Element.Kind: GraphElementKind.Node,
                Element.NativeIdentity: long graphId,
            } => new EndpointPlan(name, graphId, Node: null, Entity: null, CorrelationToken: null, StorageLabel: null),
            SelectedGraphCommandEndpoint => throw new GraphException(
                $"The selected {name} endpoint is not an AGE node graphid."),
            NewGraphCommandEndpoint { Node: { } node } => PlanNewEndpoint(name, node),
            _ => throw new GraphException($"The {name} endpoint operand is invalid."),
        };
    }

    private EndpointPlan PlanNewEndpoint(string name, Graph.INode node)
    {
        var entity = SerializeNode(node);
        return new EndpointPlan(
            name,
            GraphId: null,
            node,
            entity,
            CreateCorrelationToken(),
            StorageName(entity));
    }

    private static EndpointPlan PlanSelfLoopTarget(
        GraphCommandEndpoint source,
        GraphCommandEndpoint target,
        EndpointPlan sourcePlan)
    {
        if (source is not NewGraphCommandEndpoint sourceEndpoint ||
            target is not NewGraphCommandEndpoint targetEndpoint ||
            !ReferenceEquals(sourceEndpoint.Node, targetEndpoint.Node) ||
            !sourcePlan.IsNew)
        {
            throw new GraphException(
                "Explicit self-loop creation requires the same new node as both endpoint operands.");
        }

        return sourcePlan;
    }

    private EntityInfo SerializeNode(Graph.INode node)
    {
        GraphDataModel.EnsureNoReferenceCycle(node);
        GraphDataModel.EnsureComplexPropertyDepth(node);
        context.NodeManager.ValidateNodeProperties(node);
        return context.EntityFactory.Serialize(node);
    }

    private EntityInfo SerializeRelationship(Graph.IRelationship relationship)
    {
        GraphDataModel.EnsureNoReferenceCycle(relationship);
        GraphDataModel.EnsureComplexPropertyDepth(relationship);
        context.RelationshipManager.ValidateRelationshipProperties(relationship);

        var entity = context.EntityFactory.Serialize(relationship);
        if (entity.ComplexProperties.Count > 0)
        {
            throw new GraphException(
                $"Relationships cannot have complex properties. Found {entity.ComplexProperties.Count} complex properties on {relationship.GetType().Name}");
        }

        return entity;
    }

    private IReadOnlyList<AgeUniquenessCheck> BuildNodeUniquenessChecks(EndpointPlan endpoint) =>
        endpoint.Node is null
            ? []
            : context.NodeManager.BuildNodeUniquenessChecks(endpoint.Node, excludeGraphId: null);

    private static void ValidateNewEndpointConstraintCollisions(
        IReadOnlyList<AgeUniquenessCheck> sourceChecks,
        IReadOnlyList<AgeUniquenessCheck> targetChecks)
    {
        var conflictingCheck = sourceChecks.FirstOrDefault(sourceCheck =>
            targetChecks.Any(targetCheck => targetCheck.ConstraintKey == sourceCheck.ConstraintKey));
        if (conflictingCheck is not null)
        {
            throw new GraphException(conflictingCheck.ErrorMessage);
        }
    }

    private static List<AgeBatchCommand> BuildCommands(
        EndpointPlan source,
        EndpointPlan target,
        RelationshipDirection direction,
        GraphRelationshipCreationMode mode,
        EntityInfo relationshipEntity,
        string relationshipStorageType,
        IReadOnlyList<AgeUniquenessCheck> sourceChecks,
        IReadOnlyList<AgeUniquenessCheck> targetChecks,
        IReadOnlyList<AgeUniquenessCheck> relationshipChecks,
        IReadOnlyList<IReadOnlyList<ComplexPropertyManager.SubgraphValueNode>> valueNodeLevels,
        string correlationPropertyName,
        IReadOnlyList<EndpointPlan> newEndpoints)
    {
        var commands = new List<AgeBatchCommand>();
        AddUniquenessCommands(commands, "source_unique", sourceChecks);
        AddUniquenessCommands(commands, "target_unique", targetChecks);
        AddUniquenessCommands(commands, "relationship_unique", relationshipChecks);

        foreach (var endpoint in newEndpoints)
        {
            commands.Add(BuildRootCommand(endpoint, correlationPropertyName));
        }

        for (var depth = 0; depth < valueNodeLevels.Count; depth++)
        {
            commands.Add(BuildValueNodeLevelCommand(
                depth,
                valueNodeLevels[depth],
                correlationPropertyName));
        }

        commands.Add(BuildRelationshipCommand(
            source,
            target,
            direction,
            mode,
            relationshipEntity,
            relationshipStorageType,
            correlationPropertyName));

        commands.Add(BuildCleanupCommand(
            correlationPropertyName,
            newEndpoints.Select(endpoint => endpoint.CorrelationToken!)
                .Concat(valueNodeLevels.SelectMany(level => level.Select(node => node.CorrelationToken)))
                .ToArray()));
        return commands;
    }

    private static void AddUniquenessCommands(
        List<AgeBatchCommand> commands,
        string namePrefix,
        IReadOnlyList<AgeUniquenessCheck> checks)
    {
        for (var index = 0; index < checks.Count; index++)
        {
            commands.Add(CountCommand(
                $"{namePrefix}_{index}",
                checks[index].Cypher,
                checks[index].Parameters,
                "duplicateCount"));
        }
    }

    private static AgeBatchCommand BuildRootCommand(
        EndpointPlan endpoint,
        string correlationPropertyName)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["correlationToken"] = endpoint.CorrelationToken,
        };
        var properties = AgeNodeManager.BuildNodeProperties(endpoint.Entity!);
        var setClause = AgeCypherProperties.BuildSetClause(
            "node",
            properties,
            parameters,
            "nodeProperty");
        var storageLabel = CypherIdentifier.Escape(endpoint.StorageLabel, "node label");
        var correlationProperty = CypherIdentifier.Escape(correlationPropertyName, "correlation property name");

        return new AgeBatchCommand(
            $"{endpoint.Name}_root",
            $$"""
            CREATE (node:{{storageLabel}})
            {{setClause}}
            SET node.{{correlationProperty}} = $correlationToken
            RETURN count(node) AS createdCount, id(node) AS id
            """,
            parameters,
            ["createdCount", "id"]);
    }

    private static AgeBatchCommand BuildValueNodeLevelCommand(
        int depth,
        IReadOnlyList<ComplexPropertyManager.SubgraphValueNode> level,
        string correlationPropertyName)
    {
        var nodePropertyNames = level
            .SelectMany(node => node.NodeProperties.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var relationshipPropertyNames = level
            .SelectMany(node => node.RelationshipProperties.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var rows = level.Select(node => new Dictionary<string, object?>
        {
            ["parentToken"] = node.ParentCorrelationToken,
            ["parentStorageLabel"] = node.ParentStorageLabel,
            ["token"] = node.CorrelationToken,
            ["nodeProperties"] = ComplexPropertyManager.ExpandProperties(
                node.NodeProperties,
                nodePropertyNames),
            ["relationshipProperties"] = ComplexPropertyManager.ExpandProperties(
                node.RelationshipProperties,
                relationshipPropertyNames),
        }).ToList();
        var nodeSet = ComplexPropertyManager.BuildSetClauseFromRowMap(
            "complex",
            "row.nodeProperties",
            nodePropertyNames);
        var relationshipSet = ComplexPropertyManager.BuildSetClauseFromRowMap(
            "propertyRelationship",
            "row.relationshipProperties",
            relationshipPropertyNames);
        var correlationProperty = CypherIdentifier.Escape(correlationPropertyName, "correlation property name");
        var complexLabel = CypherIdentifier.Escape(SerializationBridge.ComplexNodeLabel, "complex value label");
        var complexRelationship = CypherIdentifier.Escape(
            SerializationBridge.ComplexRelationshipType,
            "complex property relationship type");

        return CountCommand(
            $"complex_level_{depth}",
            $$"""
            UNWIND $rows AS row
            MATCH (parent)
            WHERE parent.{{correlationProperty}} = row.parentToken
              AND size([parentLabel IN labels(parent) WHERE parentLabel = row.parentStorageLabel]) > 0
            CREATE (parent)-[propertyRelationship:{{complexRelationship}}]->(complex:{{complexLabel}})
            {{relationshipSet}}
            {{nodeSet}}
            SET complex.{{correlationProperty}} = row.token
            RETURN count(complex) AS createdCount
            """,
            new Dictionary<string, object?> { ["rows"] = rows },
            "createdCount");
    }

    private static AgeBatchCommand BuildRelationshipCommand(
        EndpointPlan source,
        EndpointPlan target,
        RelationshipDirection direction,
        GraphRelationshipCreationMode mode,
        EntityInfo entity,
        string storageType,
        string correlationPropertyName)
    {
        var effectiveDirection = mode == GraphRelationshipCreationMode.SelfLoop
            ? RelationshipDirection.Outgoing
            : direction;
        var (physicalSource, physicalTarget) = effectiveDirection switch
        {
            RelationshipDirection.Outgoing => (source, target),
            RelationshipDirection.Incoming => (target, source),
            _ => throw new GraphException($"Unsupported relationship direction '{effectiveDirection}'."),
        };
        var parameters = new Dictionary<string, object?>();
        var sourceMatch = BuildEndpointMatch(
            "source",
            physicalSource,
            correlationPropertyName,
            parameters);
        var targetAlias = mode == GraphRelationshipCreationMode.SelfLoop ? "source" : "target";
        var targetMatch = mode == GraphRelationshipCreationMode.SelfLoop
            ? string.Empty
            : BuildEndpointMatch(
                targetAlias,
                physicalTarget,
                correlationPropertyName,
                parameters);
        var properties = AgeRelationshipManager.BuildRelationshipProperties(entity);

        var setClause = AgeCypherProperties.BuildSetClause(
            "relationship",
            properties,
            parameters,
            "relationshipProperty");
        var physicalType = CypherIdentifier.Escape(storageType, "relationship type");

        return CountCommand(
            "relationship",
            $$"""
            {{sourceMatch}}
            {{targetMatch}}
            CREATE (source)-[relationship:{{physicalType}}]->({{targetAlias}})
            {{setClause}}
            RETURN count(relationship) AS createdCount
            """,
            parameters,
            "createdCount");
    }

    private static string BuildEndpointMatch(
        string alias,
        EndpointPlan endpoint,
        string correlationPropertyName,
        Dictionary<string, object?> parameters)
    {
        if (endpoint.GraphId is { } graphId)
        {
            var parameterName = $"{alias}GraphId";
            parameters.Add(parameterName, graphId);
            return $"MATCH ({alias}) WHERE id({alias}) = ${parameterName}";
        }

        var tokenParameterName = $"{alias}CorrelationToken";
        parameters.Add(tokenParameterName, endpoint.CorrelationToken);
        var storageLabel = CypherIdentifier.Escape(endpoint.StorageLabel, $"{alias} node label");
        var correlationProperty = CypherIdentifier.Escape(correlationPropertyName, "correlation property name");
        return $"MATCH ({alias}:{storageLabel}) WHERE {alias}.{correlationProperty} = ${tokenParameterName}";
    }

    private static AgeBatchCommand BuildCleanupCommand(
        string correlationPropertyName,
        IReadOnlyList<string> correlationTokens)
    {
        var correlationProperty = CypherIdentifier.Escape(correlationPropertyName, "correlation property name");
        return CountCommand(
            "cleanup",
            $$"""
            UNWIND $correlationTokens AS correlationToken
            MATCH (node)
            WHERE node.{{correlationProperty}} = correlationToken
            REMOVE node.{{correlationProperty}}
            RETURN count(node) AS cleanedCount
            """,
            new Dictionary<string, object?>
            {
                // Deduplicate so the count stays exact after the list is unwound. AGE 1.8 cannot
                // evaluate an IN with an agtype list parameter.
                ["correlationTokens"] = correlationTokens.Distinct(StringComparer.Ordinal).ToArray(),
            },
            "cleanedCount");
    }

    private static string StorageName(EntityInfo entity) =>
        SerializationBridge.GetRootStorageName(entity.Label, relationship: false);

    private static AgeBatchCommand CountCommand(
        string name,
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        string column) => new(name, cypher, parameters, [column]);

    private static void ValidateResults(
        IReadOnlyList<AgeBatchResult> results,
        IReadOnlyList<AgeUniquenessCheck> sourceChecks,
        IReadOnlyList<AgeUniquenessCheck> targetChecks,
        IReadOnlyList<AgeUniquenessCheck> relationshipChecks,
        IReadOnlyList<IReadOnlyList<ComplexPropertyManager.SubgraphValueNode>> valueNodeLevels,
        IReadOnlyList<EndpointPlan> newEndpoints,
        string relationshipTypeName)
    {
        var resultMap = results.ToDictionary(result => result.Name, StringComparer.Ordinal);
        ValidateUniqueness(resultMap, "source_unique", sourceChecks);
        ValidateUniqueness(resultMap, "target_unique", targetChecks);
        ValidateUniqueness(resultMap, "relationship_unique", relationshipChecks);

        foreach (var endpoint in newEndpoints)
        {
            if (GetCount(resultMap, $"{endpoint.Name}_root", "createdCount") != 1)
            {
                throw new GraphException("Failed to create the expected subgraph endpoint nodes.");
            }
        }

        for (var depth = 0; depth < valueNodeLevels.Count; depth++)
        {
            if (GetCount(resultMap, $"complex_level_{depth}", "createdCount") != valueNodeLevels[depth].Count)
            {
                throw new GraphException("Failed to create the expected complex-property subtree.");
            }
        }

        if (GetCount(resultMap, "relationship", "createdCount") != 1)
        {
            throw new GraphException(
                $"Failed to create relationship of type {relationshipTypeName}; one or both frozen endpoints may no longer exist.");
        }

        // Every root and every value node carries exactly one token, so the cleanup must clear as
        // many nodes as the plan created. A shortfall means a token survives the commit.
        var expectedCleanupCount = newEndpoints.Count + valueNodeLevels.Sum(level => level.Count);
        if (GetCount(resultMap, "cleanup", "cleanedCount") != expectedCleanupCount)
        {
            throw new GraphException("Failed to remove every transient subgraph correlation token.");
        }
    }

    private static void ValidateUniqueness(
        IReadOnlyDictionary<string, AgeBatchResult> results,
        string namePrefix,
        IReadOnlyList<AgeUniquenessCheck> checks)
    {
        for (var index = 0; index < checks.Count; index++)
        {
            if (GetCount(results, $"{namePrefix}_{index}", "duplicateCount") > 0)
            {
                throw new GraphException(checks[index].ErrorMessage);
            }
        }
    }

    private static long GetCount(
        IReadOnlyDictionary<string, AgeBatchResult> results,
        string resultName,
        string column)
    {
        if (!results.TryGetValue(resultName, out var result) || result.Records.Count != 1)
        {
            throw new GraphException($"AGE batch result '{resultName}' was missing or did not contain exactly one record.");
        }

        return result.Records[0][column].As<long>();
    }

    private static string CreateCorrelationPropertyName() =>
        $"{TransientCorrelationPrefix}_{CreateCorrelationToken()}";

    private static string CreateCorrelationToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
