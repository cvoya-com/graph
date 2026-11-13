// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Lowering;

using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Schema;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using BinaryExpression = Cvoya.Graph.Cypher.Ast.Expressions.BinaryExpression;
using Expression = System.Linq.Expressions.Expression;
using ExpressionVisitor = System.Linq.Expressions.ExpressionVisitor;
using MemberExpression = System.Linq.Expressions.MemberExpression;

/// <summary>
/// Recognizes the query shapes whose only agtype work is reading a declared equality property and
/// turns them into a physical SQL plan over the provider's indexed companion table.
/// </summary>
/// <remarks>
/// The builder is deliberately conservative. It accepts only a single-node, relationship-free
/// <c>MATCH</c> per union branch whose grouping key, distinct projection, or equality predicate is a
/// declared <see cref="PropertyIndexKind.Equality"/> property with a string CLR member. Aggregates
/// additionally require a declared <see cref="PropertyAggregateDefinition"/> for the same label,
/// group key, and value property. Every other shape returns <see langword="null"/> and keeps the
/// ordinary AGE path. The aggregate and filter shapes reuse the agtype output forms AGE produces,
/// so the materializer sees an unchanged projection schema.
/// </remarks>
internal static class AgeIndexedAggregatePlanBuilder
{
    private const string GroupAlias = "__group";
    private const string ValueAliasPrefix = "__value";

    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "count",
        "sum",
        "avg",
        "min",
        "max",
    };

    /// <summary>Builds a physical plan for a lowered statement, or returns <see langword="null"/>.</summary>
    internal static AgeSqlPlan? TryBuild(
        CypherStatement statement,
        Expression queryExpression,
        AgeIndexCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(queryExpression);
        ArgumentNullException.ThrowIfNull(catalog);

        if (catalog.IsEmpty)
        {
            return null;
        }

        var branchClauses = FlattenBranches(statement.Clauses);
        if (branchClauses.Count == 0)
        {
            return null;
        }

        var rawBranches = new List<RawBranch>(branchClauses.Count);
        foreach (var clauses in branchClauses)
        {
            if (!TryParseBranch(clauses, out var branch))
            {
                return null;
            }

            // The catalog and every generated table name use the AGE physical label. Normalize
            // here once so a non-native single-label branch and a hierarchy branch agree even if
            // an earlier lowering shape left the logical label in the MATCH pattern.
            rawBranches.Add(branch with { Label = catalog.ResolveLabel(branch.Label) });
        }

        if (rawBranches.Any(branch => branch.Return.Distinct))
        {
            return null;
        }

        var expressionProperties = CollectStringProperties(queryExpression);
        var group = ResolveGroup(rawBranches, expressionProperties, catalog);
        if (group is null)
        {
            return null;
        }

        var branches = new List<BranchShape>(rawBranches.Count);
        foreach (var raw in rawBranches)
        {
            if (!catalog.TryGetIndex(raw.Label, group, PropertyIndexKind.Equality, out var index) ||
                index.ColumnName.Length == 0)
            {
                return null;
            }

            if (raw.Predicate is { } predicate &&
                !string.Equals(predicate.Property, group, StringComparison.Ordinal))
            {
                return null;
            }

            branches.Add(new BranchShape(raw.Label, raw.Alias, raw.Predicate, raw.With, raw.Return, index));
        }

        var postProcessing = statement.PostProcessing;
        try
        {
            if (postProcessing?.Aggregation is { } aggregation)
            {
                var numericPlan = TryBuildNumericAggregate(branches, aggregation, postProcessing, catalog, group);
                return numericPlan ?? TryBuildGrouped(branches, aggregation, postProcessing, catalog, group);
            }

            // A single-label group-by is not expanded into UNION ALL branches, so the aggregation
            // stays in the WITH/RETURN clauses instead of post-processing metadata. Infer the same
            // grouping description and feed the existing companion-table SQL builder.
            if (postProcessing is null &&
                TryInferAggregation(branches, group, out var inferred))
            {
                var synthetic = new CypherPostProcessing(
                    [],
                    Skip: null,
                    Limit: null,
                    branches[0].Return.Items.Count,
                    inferred);
                var numericPlan = TryBuildNumericAggregate(branches, inferred, synthetic, catalog, group);
                return numericPlan ?? TryBuildGrouped(branches, inferred, synthetic, catalog, group);
            }

            if (postProcessing is { Distinct: true } distinct &&
                distinct.Aggregation is null &&
                distinct.Sort.Count == 0 &&
                distinct.Skip is null &&
                distinct.Limit is null)
            {
                return TryBuildDistinct(branches, distinct, group);
            }

            if (postProcessing is null)
            {
                return TryBuildFilteredEntity(branches);
            }
        }
        catch (GraphException)
        {
            throw;
        }
        catch (Exception)
        {
            // A shape the builder does not fully understand must never break the query; the normal
            // AGE path remains the correctness fallback.
            return null;
        }

        return null;
    }

    private static string? ResolveGroup(
        IReadOnlyList<RawBranch> branches,
        HashSet<string> expressionProperties,
        AgeIndexCatalog catalog)
    {
        if (branches.Count == 0 || expressionProperties.Count == 0)
        {
            return null;
        }

        foreach (var property in catalog.Properties.Select(candidate => candidate.Property))
        {
            if (!expressionProperties.Contains(property) ||
                !branches.All(branch => catalog.TryGetIndex(
                    branch.Label,
                    property,
                    PropertyIndexKind.Equality,
                    out var index) &&
                    index.ColumnName.Length > 0))
            {
                continue;
            }

            return property;
        }

        return null;
    }

    /// <summary>
    /// Collects the stored names of every string CLR member the query expression references.
    /// Shared with the paging builder, which must not replace a numeric ordering with text ordering.
    /// </summary>
    internal static HashSet<string> CollectStringProperties(Expression expression)
    {
        var finder = new StringPropertyFinder();
        finder.Visit(expression);
        return finder.Properties;
    }

    private sealed class StringPropertyFinder : ExpressionVisitor
    {
        public HashSet<string> Properties { get; } = new(StringComparer.Ordinal);

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Type == typeof(string) && node.Member is PropertyInfo property)
            {
                Properties.Add(StoredName(property));
            }

            return base.VisitMember(node);
        }
    }

    private static string StoredName(PropertyInfo property) =>
        property.GetCustomAttribute<PropertyAttribute>(inherit: true)?.Label is { Length: > 0 } label
            ? label
            : property.Name.Replace("`", string.Empty, StringComparison.Ordinal);

    private static bool TryInferAggregation(
        IReadOnlyList<BranchShape> branches,
        string group,
        out CypherPostAggregation aggregation)
    {
        aggregation = null!;
        if (branches.Count != 1)
        {
            return false;
        }

        var branch = branches[0];
        var with = branch.With;
        var returnItems = branch.Return.Items;
        if (with is null || with.Wildcard || with.Distinct || with.Items.Count != returnItems.Count)
        {
            return false;
        }

        var byAlias = new Dictionary<string, ReturnItem>(StringComparer.Ordinal);
        foreach (var item in with.Items)
        {
            if (item.Alias is null || !byAlias.TryAdd(item.Alias, item))
            {
                return false;
            }
        }

        var groups = new List<int>();
        var aggregates = new List<CypherPostAggregate>();
        for (var index = 0; index < returnItems.Count; index++)
        {
            if (returnItems[index].Expression is not VariableRef reference ||
                !byAlias.TryGetValue(reference.Alias, out var withItem))
            {
                return false;
            }

            if (IsGroupAccess(withItem.Expression, branch.Alias, group))
            {
                groups.Add(index);
                continue;
            }

            if (!TryReadAggregate(withItem, branch.Alias, out var function, out _))
            {
                return false;
            }

            aggregates.Add(new CypherPostAggregate(index, function));
        }

        if (groups.Count == 0 || aggregates.Count == 0)
        {
            return false;
        }

        aggregation = new CypherPostAggregation(groups, aggregates);
        return true;
    }

    private static AgeSqlPlan? TryBuildNumericAggregate(
        IReadOnlyList<BranchShape> branches,
        CypherPostAggregation aggregation,
        CypherPostProcessing postProcessing,
        AgeIndexCatalog catalog,
        string group)
    {
        if (postProcessing.Sort.Count != 0 ||
            postProcessing.Skip is not null ||
            postProcessing.Limit is not null ||
            aggregation.GroupColumnIndexes.Count != 1 ||
            aggregation.GroupColumnIndexes[0] != 0 ||
            postProcessing.ResultColumnCount < 2 ||
            postProcessing.ResultColumnCount > branches[0].Return.Items.Count)
        {
            return null;
        }

        GroupedBranchDescription? firstDescription = null;
        foreach (var branch in branches)
        {
            if (branch.With is null || branch.With.Wildcard || branch.With.Distinct)
            {
                return null;
            }

            if (!TryDescribeGroupedBranch(
                    branch,
                    postProcessing.ResultColumnCount,
                    aggregation,
                    group,
                    out var branchDescription))
            {
                return null;
            }

            firstDescription ??= branchDescription;
        }

        var signature = GroupedSignature(branches[0], postProcessing.ResultColumnCount);
        if (branches.Skip(1).Any(branch =>
                !string.Equals(signature, GroupedSignature(branch, postProcessing.ResultColumnCount), StringComparison.Ordinal)))
        {
            return null;
        }

        var description = firstDescription!;
        var valueProperty = description.Aggregates[0].ValueProperty;
        if (valueProperty is null ||
            description.Aggregates.Any(aggregate =>
                !string.Equals(aggregate.ValueProperty, valueProperty, StringComparison.Ordinal)))
        {
            return null;
        }

        var aggregateIndexes = new List<AgeAggregateIndex>(branches.Count);
        foreach (var aggregate in description.Aggregates)
        {
            var requiredFunction = RequiredAggregateFunction(aggregate.MergeFunction);
            if (requiredFunction is null)
            {
                return null;
            }

            foreach (var branch in branches)
            {
                if (!catalog.TryGetAggregate(branch.Label, group, valueProperty, out var candidate) ||
                    !candidate.Functions.HasFlag(requiredFunction.Value))
                {
                    return null;
                }
            }
        }

        foreach (var branch in branches)
        {
            if (!catalog.TryGetAggregate(branch.Label, group, valueProperty, out var candidate))
            {
                return null;
            }

            aggregateIndexes.Add(candidate);
        }

        var selectList = new StringBuilder();
        selectList.Append(BuildGroupOutput(GroupAlias, description.ReturnAliases[0]));
        for (var index = 1; index < postProcessing.ResultColumnCount; index++)
        {
            var aggregate = description.Aggregates[index - 1];
            selectList.Append(", ").Append(BuildNumericAggregateExpression(aggregate))
                .Append(" AS ")
                .Append(AgeIndexTable.QuoteIdentifier(description.ReturnAliases[index], "projection alias"));
        }

        var inner = new StringBuilder();
        for (var index = 0; index < branches.Count; index++)
        {
            if (index > 0)
            {
                inner.AppendLine().AppendLine("UNION ALL");
            }

            var branch = branches[index];
            inner.Append("SELECT ")
                .Append(AgeIndexTable.QuoteIdentifier(AgeAggregateTable.GroupTextColumn, "column name"))
                .Append(" AS ").Append(AgeIndexTable.QuoteIdentifier(GroupAlias, "column name"))
                .Append(", ").Append(AgeIndexTable.QuoteIdentifier(AgeAggregateTable.CountColumn, "column name"))
                .Append(", ").Append(AgeIndexTable.QuoteIdentifier(AgeAggregateTable.SumColumn, "column name"))
                .Append(", ").Append(AgeIndexTable.QuoteIdentifier(AgeAggregateTable.MinColumn, "column name"))
                .Append(", ").Append(AgeIndexTable.QuoteIdentifier(AgeAggregateTable.MaxColumn, "column name"))
                .Append(" FROM ").Append(AggregateTable(branch.Label, group, valueProperty));
            AppendPredicate(
                inner,
                branch.Predicate,
                AgeIndexTable.QuoteIdentifier(AgeAggregateTable.GroupTextColumn, "column name"));
        }

        var sql = new StringBuilder();
        sql.Append("SELECT ").Append(selectList).AppendLine();
        sql.AppendLine("FROM (");
        sql.AppendLine(inner.ToString());
        sql.Append(") AS ").Append(AgeIndexTable.QuoteIdentifier("__aggregate_rows", "table alias"))
            .AppendLine();
        sql.Append("GROUP BY ").Append(AgeIndexTable.QuoteIdentifier(GroupAlias, "column name"));

        var required = new List<string>();
        foreach (var branch in branches)
        {
            required.Add(branch.Group.TableName);
            required.Add(AgePhysicalName.AggregateTable(branch.Label, group, valueProperty));
        }

        return new AgeSqlPlan(
            branches.Select(branch => branch.Label).ToArray(),
            sql.ToString(),
            "aggregate",
            required);
    }

    private static AggregateFunction? RequiredAggregateFunction(string mergeFunction) =>
        mergeFunction switch
        {
            "avg" => AggregateFunction.Average,
            "min" => AggregateFunction.Min,
            "max" => AggregateFunction.Max,
            "sum" => AggregateFunction.Sum,
            "count" => AggregateFunction.Count,
            _ => null,
        };

    private static string BuildNumericAggregateExpression(AggregateDescription aggregate)
    {
        var count = AgeIndexTable.QuoteIdentifier(AgeAggregateTable.CountColumn, "column name");
        var sum = AgeIndexTable.QuoteIdentifier(AgeAggregateTable.SumColumn, "column name");
        var min = AgeIndexTable.QuoteIdentifier(AgeAggregateTable.MinColumn, "column name");
        var max = AgeIndexTable.QuoteIdentifier(AgeAggregateTable.MaxColumn, "column name");
        return aggregate.MergeFunction switch
        {
            "avg" => $"(sum({sum}) / NULLIF(sum({count}), 0))::text::ag_catalog.agtype",
            "min" => $"to_jsonb(min({min}))::text::ag_catalog.agtype",
            "max" => $"to_jsonb(max({max}))::text::ag_catalog.agtype",
            "sum" => $"sum({sum})::text::ag_catalog.agtype",
            "count" => $"sum({count})::text::ag_catalog.agtype",
            _ => throw new InvalidOperationException("Unexpected numeric aggregate function."),
        };
    }

    private static string AggregateTable(string label, string group, string value) =>
        AgeIndexTable.GraphToken + "." +
        AgeIndexTable.QuoteIdentifier(AgePhysicalName.AggregateTable(label, group, value), "aggregate table name");

    private static AgeSqlPlan? TryBuildGrouped(
        IReadOnlyList<BranchShape> branches,
        CypherPostAggregation aggregation,
        CypherPostProcessing postProcessing,
        AgeIndexCatalog catalog,
        string group)
    {
        if (postProcessing.Sort.Count != 0 ||
            postProcessing.Skip is not null ||
            postProcessing.Limit is not null ||
            aggregation.GroupColumnIndexes.Count != 1 ||
            aggregation.GroupColumnIndexes[0] != 0 ||
            postProcessing.ResultColumnCount < 1 ||
            postProcessing.ResultColumnCount > branches[0].Return.Items.Count)
        {
            return null;
        }

        GroupedBranchDescription? firstDescription = null;
        foreach (var branch in branches)
        {
            if (branch.With is null || branch.With.Wildcard || branch.With.Distinct)
            {
                return null;
            }

            if (!TryDescribeGroupedBranch(
                    branch,
                    postProcessing.ResultColumnCount,
                    aggregation,
                    group,
                    out var branchDescription))
            {
                return null;
            }

            firstDescription ??= branchDescription;
        }

        var signature = GroupedSignature(branches[0], postProcessing.ResultColumnCount);
        if (branches.Skip(1).Any(branch =>
                !string.Equals(signature, GroupedSignature(branch, postProcessing.ResultColumnCount), StringComparison.Ordinal)))
        {
            return null;
        }

        var description = firstDescription!;
        // Only count(*) is answered from the per-label companion rows. count(property) needs the
        // original label row, which this plan deliberately does not read; returning null keeps the
        // ordinary AGE path (or the numeric aggregate plan) for that shape.
        if (description.Aggregates.Any(aggregate =>
                aggregate.BranchFunction != "count" ||
                aggregate.MergeFunction != "sum" ||
                aggregate.ValueProperty is not null))
        {
            return null;
        }

        foreach (var aggregate in description.Aggregates)
        {
            if (aggregate.ValueProperty is null)
            {
                continue;
            }

            if (!catalog.TryGetAggregate(
                    branches[0].Label,
                    group,
                    aggregate.ValueProperty,
                    out var definition) ||
                !definition.Functions.HasFlag(AggregateFunction.Count))
            {
                return null;
            }
        }

        var valueAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var valueColumns = new List<(string Alias, string Expression)>();
        void RegisterValue(string property)
        {
            if (!valueAliases.ContainsKey(property))
            {
                var alias = ValueAliasPrefix + valueAliases.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                valueAliases[property] = alias;
                valueColumns.Add((alias, ValueSql(property)));
            }
        }

        foreach (var aggregate in description.Aggregates)
        {
            if (aggregate.ValueProperty is not null)
            {
                RegisterValue(aggregate.ValueProperty);
            }
        }

        var selectList = new StringBuilder();
        selectList.Append(BuildGroupOutput(GroupAlias, description.ReturnAliases[0]));
        for (var index = 1; index < postProcessing.ResultColumnCount; index++)
        {
            var aggregate = description.Aggregates[index - 1];
            var expression = BuildAggregateExpression(aggregate, valueAliases);
            if (expression is null)
            {
                return null;
            }

            selectList.Append(", ").Append(expression)
                .Append(" AS ").Append(AgeIndexTable.QuoteIdentifier(description.ReturnAliases[index], "projection alias"));
        }

        var inner = new StringBuilder();
        for (var index = 0; index < branches.Count; index++)
        {
            if (index > 0)
            {
                inner.AppendLine().AppendLine("UNION ALL");
            }

            var branch = branches[index];
            inner.Append("SELECT ").Append(AgeIndexTable.QuoteIdentifier(branch.Group.ColumnName, "column name"))
                .Append(" AS ").Append(AgeIndexTable.QuoteIdentifier(GroupAlias, "projection alias"));
            foreach (var (alias, expression) in valueColumns)
            {
                inner.Append(", ").Append(expression)
                    .Append(" AS ").Append(AgeIndexTable.QuoteIdentifier(alias, "projection alias"));
            }

            inner.Append(" FROM ").Append(StatusTable(branch.Group.TableName));
            AppendPredicate(inner, branch.Predicate, branch.Group.ColumnName);
        }

        var sql = new StringBuilder();
        sql.Append("SELECT ").Append(selectList).AppendLine();
        sql.AppendLine("FROM (");
        sql.AppendLine(inner.ToString());
        sql.Append(") AS ").Append(AgeIndexTable.QuoteIdentifier("__group_rows", "table alias"))
            .AppendLine();
        sql.Append("GROUP BY ").Append(AgeIndexTable.QuoteIdentifier(GroupAlias, "column name"));
        return new AgeSqlPlan(
            branches.Select(branch => branch.Label).ToArray(),
            sql.ToString(),
            "group",
            branches.Select(branch => branch.Group.TableName).ToArray());
    }

    private static bool TryDescribeGroupedBranch(
        BranchShape branch,
        int resultColumnCount,
        CypherPostAggregation aggregation,
        string group,
        out GroupedBranchDescription description)
    {
        description = null!;
        var with = branch.With!;
        var returnItems = branch.Return.Items;
        if (with.Items.Count != returnItems.Count || with.Items.Count < resultColumnCount)
        {
            return false;
        }

        var byAlias = new Dictionary<string, ReturnItem>(StringComparer.Ordinal);
        foreach (var item in with.Items)
        {
            if (item.Alias is null || !byAlias.TryAdd(item.Alias, item))
            {
                return false;
            }
        }

        var returnAliases = new string[resultColumnCount];
        var aggregates = new List<AggregateDescription>(Math.Max(0, resultColumnCount - 1));
        for (var index = 0; index < resultColumnCount; index++)
        {
            var projectionAlias = returnItems[index].Alias;
            if (returnItems[index].Expression is not VariableRef reference ||
                !byAlias.TryGetValue(reference.Alias, out var withItem) ||
                projectionAlias is null)
            {
                return false;
            }

            returnAliases[index] = AgeEntityProjectionPass.NormalizeProjectionColumn(projectionAlias);
            if (index == 0)
            {
                if (!IsGroupAccess(withItem.Expression, branch.Alias, group))
                {
                    return false;
                }

                continue;
            }

            if (!TryReadAggregate(withItem, branch.Alias, out var branchFunction, out var valueProperty))
            {
                return false;
            }

            var merge = aggregation.Aggregates.FirstOrDefault(candidate => candidate.ColumnIndex == index);
            if (merge is null)
            {
                return false;
            }

            var mergeFunction = merge.Function.ToLowerInvariant();
            if (mergeFunction == "avg")
            {
                if (branchFunction == "sum")
                {
                    if (valueProperty is null ||
                        merge.AuxiliaryColumnIndex is not int auxiliary ||
                        auxiliary < 0 ||
                        auxiliary >= returnItems.Count ||
                        returnItems[auxiliary].Expression is not VariableRef auxiliaryReference ||
                        !byAlias.TryGetValue(auxiliaryReference.Alias, out var auxiliaryItem) ||
                        !TryReadAggregate(auxiliaryItem, branch.Alias, out var auxiliaryFunction, out var auxiliaryProperty) ||
                        auxiliaryFunction != "count" ||
                        !string.Equals(auxiliaryProperty, valueProperty, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
                else if (branchFunction != "avg" || valueProperty is null)
                {
                    // A single-label branch is not expanded into partial sum/count columns; the
                    // companion aggregate table already stores both, so the direct avg shape is
                    // answerable without an auxiliary column.
                    return false;
                }
            }
            else if (branchFunction == "count")
            {
                // Hierarchy branches merge count(*) into sum; a single-label branch keeps the
                // original count and the wrapper still receives one row per group.
                if (mergeFunction is not ("sum" or "count"))
                {
                    return false;
                }
            }
            else if (!string.Equals(branchFunction, mergeFunction, StringComparison.Ordinal))
            {
                return false;
            }

            aggregates.Add(new AggregateDescription(branchFunction, mergeFunction, valueProperty));
        }

        description = new GroupedBranchDescription(returnAliases, aggregates);
        return true;
    }

    private static bool TryReadAggregate(
        ReturnItem withItem,
        string alias,
        out string function,
        out string? valueProperty)
    {
        function = string.Empty;
        valueProperty = null;
        if (withItem.Expression is not FunctionCall call ||
            !AggregateFunctions.Contains(call.Name) ||
            call.Arguments.Count != 1)
        {
            return false;
        }

        function = call.Name.ToLowerInvariant();
        if (function == "count" && IsCountAllLiteral(call.Arguments[0]))
        {
            return true;
        }

        if (TryGetPropertyAccess(call.Arguments[0], alias, out var property))
        {
            valueProperty = property;
            return true;
        }

        return false;
    }

    private static string GroupedSignature(BranchShape branch, int resultColumnCount)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < resultColumnCount; index++)
        {
            builder.Append(index == 0 ? "key" : DescribeWithItem(branch, index)).Append('|');
        }

        for (var index = 0; index < branch.Return.Items.Count; index++)
        {
            builder.Append(branch.Return.Items[index].Alias).Append('|');
        }

        return builder.ToString();
    }

    private static string DescribeWithItem(BranchShape branch, int index)
    {
        var item = branch.With!.Items[index];
        if (item.Expression is not FunctionCall function)
        {
            return "?";
        }

        var argument = IsCountAllLiteral(function.Arguments[0])
            ? "1"
            : TryGetPropertyAccess(function.Arguments[0], branch.Alias, out var property)
                ? property
                : "?";
        return function.Name.ToLowerInvariant() + ":" + argument + ":" + item.Alias;
    }

    private static AgeSqlPlan? TryBuildDistinct(
        IReadOnlyList<BranchShape> branches,
        CypherPostProcessing postProcessing,
        string group)
    {
        if (postProcessing.ResultColumnCount != 1)
        {
            return null;
        }

        string? alias = null;
        foreach (var branch in branches)
        {
            if (branch.With is not null ||
                branch.Return.Items.Count != 1 ||
                !IsGroupAccess(branch.Return.Items[0].Expression, branch.Alias, group) ||
                branch.Return.Items[0].Alias is null)
            {
                return null;
            }

            var projectionAlias = AgeEntityProjectionPass.NormalizeProjectionColumn(
                branch.Return.Items[0].Alias!);
            alias ??= projectionAlias;
            if (!string.Equals(alias, projectionAlias, StringComparison.Ordinal))
            {
                return null;
            }
        }

        var inner = BuildInnerGroupUnion(branches);
        var sql =
            "SELECT " +
            BuildGroupOutput(GroupAlias, alias!) +
            Environment.NewLine +
            "FROM (" + Environment.NewLine +
            inner + Environment.NewLine +
            ") AS " + AgeIndexTable.QuoteIdentifier("__group_rows", "table alias") +
            Environment.NewLine +
            "GROUP BY " + AgeIndexTable.QuoteIdentifier(GroupAlias, "column name");
        return new AgeSqlPlan(
            branches.Select(branch => branch.Label).ToArray(),
            sql,
            "distinct",
            branches.Select(branch => branch.Group.TableName).ToArray());
    }

    private static AgeSqlPlan? TryBuildFilteredEntity(IReadOnlyList<BranchShape> branches)
    {
        string? alias = null;
        foreach (var branch in branches)
        {
            if (branch.Predicate is null ||
                branch.With is not null ||
                branch.Return.Items.Count != 1 ||
                branch.Return.Items[0].Alias is null)
            {
                return null;
            }

            if (branch.Return.Items[0].Expression is not MapExpression map)
            {
                return null;
            }

            if (map.Entries.Count != 2)
            {
                return null;
            }

            var node = map.Entries.FirstOrDefault(entry => entry.Key == "Node");
            var complex = map.Entries.FirstOrDefault(entry => entry.Key == "ComplexProperties");
            if (node?.Value is not VariableRef { Alias: var nodeAlias } ||
                !string.Equals(nodeAlias, branch.Alias, StringComparison.Ordinal) ||
                complex?.Value is not ListExpression { Items.Count: 0 })
            {
                return null;
            }

            var projectionAlias = AgeEntityProjectionPass.NormalizeProjectionColumn(
                branch.Return.Items[0].Alias!);
            alias ??= projectionAlias;
            if (!string.Equals(alias, projectionAlias, StringComparison.Ordinal))
            {
                return null;
            }
        }

        var union = new StringBuilder();
        for (var index = 0; index < branches.Count; index++)
        {
            if (index > 0)
            {
                union.AppendLine().AppendLine("UNION ALL");
            }

            var branch = branches[index];
            // agtype_typecast_vertex(map) exists in both AGE 1.7 and 1.8, whereas the internal
            // _agtype_build_vertex signature changed between those releases. Building the map
            // first keeps the projection identical on both supported test/measurement lanes.
            union.Append("SELECT ag_catalog.agtype_build_map(")
                .Append("'Node'::text, ag_catalog.agtype_typecast_vertex(ag_catalog.agtype_build_map(")
                .Append("'id'::text, __src.id, 'label'::text, ")
                .Append(AgeIndexTable.QuoteLiteral(branch.Label, "label name"))
                .Append(", 'properties'::text, __src.properties)), ")
                .Append("'ComplexProperties'::text, ag_catalog.agtype_build_list()) AS ")
                .Append(AgeIndexTable.QuoteIdentifier(alias!, "projection alias"))
                .Append(" FROM ").Append(LabelTable(branch.Label))
                .Append(" AS __src JOIN ").Append(StatusTable(branch.Group.TableName))
                .Append(" AS __index ON __index.id = __src.id");
            AppendPredicate(union, branch.Predicate, "__index." +
                AgeIndexTable.QuoteIdentifier(branch.Group.ColumnName, "column name"));
        }

        return new AgeSqlPlan(
            branches.Select(branch => branch.Label).ToArray(),
            union.ToString(),
            "filter",
            branches.Select(branch => branch.Group.TableName).ToArray());
    }

    private static string BuildInnerGroupUnion(IReadOnlyList<BranchShape> branches)
    {
        var inner = new StringBuilder();
        for (var index = 0; index < branches.Count; index++)
        {
            if (index > 0)
            {
                inner.AppendLine().AppendLine("UNION ALL");
            }

            var branch = branches[index];
            inner.Append("SELECT ").Append(AgeIndexTable.QuoteIdentifier(branch.Group.ColumnName, "column name"))
                .Append(" AS ").Append(AgeIndexTable.QuoteIdentifier(GroupAlias, "column name"))
                .Append(" FROM ").Append(StatusTable(branch.Group.TableName));
            AppendPredicate(inner, branch.Predicate, branch.Group.ColumnName);
        }

        return inner.ToString();
    }

    private static string BuildGroupOutput(string column, string alias) =>
        "to_jsonb(" + AgeIndexTable.QuoteIdentifier(column, "column name") +
        ")::text::ag_catalog.agtype AS " +
        AgeIndexTable.QuoteIdentifier(alias, "projection alias");

    private static string? BuildAggregateExpression(
        AggregateDescription aggregate,
        Dictionary<string, string> valueAliases)
    {
        var value = aggregate.ValueProperty is null ? null : valueAliases[aggregate.ValueProperty];
        if (aggregate.BranchFunction == "count")
        {
            return value is null
                ? "count(*)::text::ag_catalog.agtype"
                : "count(" + value + ")::text::ag_catalog.agtype";
        }

        if (value is null)
        {
            return null;
        }

        return aggregate.MergeFunction switch
        {
            "sum" => "sum((" + value + ")::text::numeric)::text::ag_catalog.agtype",
            "avg" => "(sum((" + value + ")::text::numeric) / NULLIF(count((" + value +
                ")::text::numeric), 0))::text::ag_catalog.agtype",
            "min" => "ag_catalog.age_min(" + value + ")",
            "max" => "ag_catalog.age_max(" + value + ")",
            _ => null,
        };
    }

    internal static void AppendPredicate(
        StringBuilder builder,
        IndexPredicate? predicate,
        string columnName)
    {
        if (predicate is null)
        {
            return;
        }

        builder.Append(" WHERE ");
        var column = columnName;
        if (predicate.ParameterName is not null)
        {
            var parameter = "((" + AgeIndexTable.ParameterToken + "::text::jsonb) ->> " +
                AgeIndexTable.QuoteLiteral(predicate.ParameterName, "parameter name") + ")";
            if (predicate.Negated)
            {
                // Cypher `<>` excludes rows whose property is missing/NULL, so the SQL plan must
                // require a non-NULL value (and a non-NULL parameter) before applying
                // IS DISTINCT FROM. Without the guard, a missing value would be counted.
                builder.Append(column).Append(" IS NOT NULL AND ")
                    .Append(parameter).Append(" IS NOT NULL AND ")
                    .Append(column).Append(" IS DISTINCT FROM ").Append(parameter);
            }
            else
            {
                builder.Append(column).Append(" = ").Append(parameter);
            }

            return;
        }

        var literal = AgeIndexTable.QuoteLiteral(predicate.Constant!, "property literal");
        if (predicate.Negated)
        {
            builder.Append(column).Append(" IS NOT NULL AND ")
                .Append(column).Append(" IS DISTINCT FROM ").Append(literal);
        }
        else
        {
            builder.Append(column).Append(" = ").Append(literal);
        }
    }

    private static string StatusTable(string tableName) =>
        AgeIndexTable.GraphToken + "." +
        AgeIndexTable.QuoteIdentifier(tableName, "index table name");

    private static string LabelTable(string label) =>
        AgeIndexTable.GraphToken + "." +
        AgeIndexTable.QuoteIdentifier(label, "label name");

    private static string ValueSql(string property) =>
        "ag_catalog.agtype_access_operator(VARIADIC ARRAY[properties, " +
        AgeIndexTable.AgtypeStringLiteral(property) + "])";

    private static bool IsGroupAccess(CypherExpression expression, string alias, string group) =>
        TryGetPropertyAccess(expression, alias, out var property) &&
        string.Equals(property, group, StringComparison.Ordinal);

    private static bool TryGetPropertyAccess(
        CypherExpression expression,
        string alias,
        out string property)
    {
        switch (expression)
        {
            case PropertyAccess
            {
                Target: VariableRef { Alias: var targetAlias },
                Property: var accessed,
            } when string.Equals(targetAlias, alias, StringComparison.Ordinal):
                property = accessed;
                return true;

            case EscapedPropertyAccess
            {
                Target: VariableRef { Alias: var targetAlias },
                Property: var accessed,
            } when string.Equals(targetAlias, alias, StringComparison.Ordinal):
                property = accessed;
                return true;

            default:
                property = string.Empty;
                return false;
        }
    }

    private static bool IsCountAllLiteral(CypherExpression expression) =>
        expression is Literal { Value: not null } literal &&
        literal.Value is not bool &&
        TryConvertToInt64(literal.Value) == 1;

    private static long? TryConvertToInt64(object value)
    {
        try
        {
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static bool TryParseBranch(
        IReadOnlyList<ICypherClause> clauses,
        out RawBranch branch)
    {
        branch = null!;
        string? label = null;
        string? alias = null;
        IndexPredicate? predicate = null;
        WithClause? with = null;
        ReturnClause? @return = null;
        foreach (var clause in clauses)
        {
            switch (clause)
            {
                case MatchClause { Optional: false, Patterns.Count: 1 } match when label is null && with is null && @return is null:
                    var pattern = match.Patterns[0];
                    if (pattern.Elements.Count != 1 ||
                        pattern.Elements[0] is not NodePattern { Alias: not null, Labels.Count: 1 } node)
                    {
                        return false;
                    }

                    label = node.Labels[0];
                    alias = node.Alias;
                    break;

                case WhereClause where when predicate is null && with is null && @return is null:
                    if (alias is null || !TryParseIndexPredicate(where.Predicate, alias, out predicate))
                    {
                        return false;
                    }

                    break;

                case WithClause value when with is null && @return is null:
                    with = value;
                    break;

                case ReturnClause value when @return is null:
                    @return = value;
                    break;

                default:
                    return false;
            }
        }

        if (label is null || alias is null || @return is null)
        {
            return false;
        }

        branch = new RawBranch(label, alias, predicate, with, @return);
        return true;
    }

    private static bool TryParseIndexPredicate(
        CypherExpression expression,
        string alias,
        out IndexPredicate? predicate)
    {
        predicate = null;
        if (expression is not BinaryExpression
            { Op: CypherBinaryOperator.Equal or CypherBinaryOperator.NotEqual } binary ||
            !TryGetPropertyAccess(binary.Left, alias, out var property))
        {
            return false;
        }

        var negated = binary.Op == CypherBinaryOperator.NotEqual;
        switch (binary.Right)
        {
            case QueryParameter parameter:
                predicate = new IndexPredicate(property, parameter.Name, null, negated);
                return true;

            case Literal { Value: string literal }:
                predicate = new IndexPredicate(property, null, literal, negated);
                return true;

            default:
                return false;
        }
    }

    private static List<IReadOnlyList<ICypherClause>> FlattenBranches(IReadOnlyList<ICypherClause> clauses)
    {
        var branches = new List<IReadOnlyList<ICypherClause>>();
        var valid = true;
        Visit(clauses);
        return valid ? branches : [];

        void Visit(IReadOnlyList<ICypherClause> current)
        {
            if (!valid)
            {
                return;
            }

            var setOperations = current.OfType<SetOperationClause>().ToArray();
            if (setOperations.Length == 0)
            {
                branches.Add(current);
                return;
            }

            if (setOperations.Length != 1 || current.Count != 1 || !setOperations[0].PreserveDuplicates)
            {
                valid = false;
                return;
            }

            Visit(setOperations[0].First);
            Visit(setOperations[0].Second);
        }
    }

    private sealed record RawBranch(
        string Label,
        string Alias,
        IndexPredicate? Predicate,
        WithClause? With,
        ReturnClause Return);

    private sealed record BranchShape(
        string Label,
        string Alias,
        IndexPredicate? Predicate,
        WithClause? With,
        ReturnClause Return,
        AgeIndexedProperty Group);

    internal sealed record IndexPredicate(string Property, string? ParameterName, string? Constant, bool Negated);

    private sealed record AggregateDescription(
        string BranchFunction,
        string MergeFunction,
        string? ValueProperty);

    private sealed record GroupedBranchDescription(
        string[] ReturnAliases,
        IReadOnlyList<AggregateDescription> Aggregates);
}
