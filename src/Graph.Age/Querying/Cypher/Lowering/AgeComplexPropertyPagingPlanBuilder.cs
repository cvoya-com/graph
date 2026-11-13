// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Lowering;

using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Schema;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using LinqExpression = System.Linq.Expressions.Expression;

/// <summary>
/// Recognizes a complex-property query whose final page can be selected before the
/// variable-length complex-property load.
/// </summary>
/// <remarks>
/// The shape is deliberately narrow: one or more plain root <c>MATCH (alias:Label)</c> branches,
/// a complex-property marker, a single scalar sort key, and a positive offset. The existing
/// lowering bounds each branch to <c>skip + limit</c> rows, but the complex-property collect still
/// runs for all of them. Selecting the page first bounds the variable-length traversal to the
/// final <c>limit</c> rows, which is the dominant cost under concurrent load.
/// <para>
/// The detector reads the typed AST, so escaped and non-ASCII labels and properties are ordinary
/// names instead of pattern-match misses. A branch that is not a single root node pattern, a
/// non-marker <c>WHERE</c>, or a sort column that is not a property of the root alias keeps the
/// ordinary path.
/// </para>
/// </remarks>
internal static class AgeComplexPropertyPagingPlanBuilder
{
    private const string ComplexPropertyMarker = "__graphModelComplexProperty";

    /// <summary>Builds a page plan for a lowered statement, or returns <see langword="null"/>.</summary>
    internal static AgePageSelectionPlan? TryBuild(
        CypherStatement statement,
        LinqExpression queryExpression,
        AgeIndexCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(queryExpression);
        ArgumentNullException.ThrowIfNull(catalog);

        var postProcessing = statement.PostProcessing;
        int sortColumnIndex;
        bool descending;
        long skip;
        long limit;
        var clauseLevelPaging = postProcessing is null;
        if (postProcessing is not null)
        {
            if (postProcessing.Aggregation is not null ||
                postProcessing.Distinct ||
                postProcessing.Sort.Count != 1 ||
                postProcessing.Skip is not long postSkip ||
                postSkip <= 0 ||
                postProcessing.Limit is not long postLimit ||
                postLimit <= 0)
            {
                return null;
            }

            sortColumnIndex = postProcessing.Sort[0].ColumnIndex;
            descending = postProcessing.Sort[0].Descending;
            skip = postSkip;
            limit = postLimit;
        }
        else if (!TryGetClauseLevelPaging(statement.Clauses, out sortColumnIndex, out descending, out skip, out limit))
        {
            return null;
        }

        var branches = FlattenBranches(statement.Clauses);
        if (branches.Count == 0)
        {
            return null;
        }

        string? alias = null;
        var markerSeen = false;
        var labels = new List<string>(branches.Count);
        foreach (var branch in branches)
        {
            if (!TryParseBranch(branch, ref alias, out var label, ref markerSeen))
            {
                return null;
            }

            labels.Add(catalog.ResolveLabel(label));
        }

        if (alias is null || !markerSeen)
        {
            return null;
        }

        string? sortProperty = null;
        foreach (var branch in branches)
        {
            if (!TryGetSortProperty(branch, sortColumnIndex, alias, out var branchProperty))
            {
                return null;
            }

            sortProperty ??= branchProperty;
            if (!string.Equals(sortProperty, branchProperty, StringComparison.Ordinal))
            {
                return null;
            }
        }

        if (sortProperty is null)
        {
            return null;
        }

        var distinctLabels = labels.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var label in distinctLabels)
        {
            if (!catalog.TryGetIndex(label, sortProperty, PropertyIndexKind.Ordering, out _))
            {
                return null;
            }
        }

        return new AgePageSelectionPlan(
            labels,
            alias,
            sortProperty,
            descending,
            skip,
            limit,
            BuildSources(distinctLabels, sortProperty, queryExpression, catalog),
            clauseLevelPaging);
    }

    /// <summary>
    /// Reads the paging and single sort key from the clause pipeline of a single-label query. A
    /// union lifts those clauses into <see cref="CypherStatement.PostProcessing"/> instead.
    /// </summary>
    private static bool TryGetClauseLevelPaging(
        IReadOnlyList<ICypherClause> clauses,
        out int sortColumnIndex,
        out bool descending,
        out long skip,
        out long limit)
    {
        sortColumnIndex = -1;
        descending = false;
        skip = 0;
        limit = 0;

        var orderBy = clauses.OfType<OrderByClause>().LastOrDefault();
        var skipClause = clauses.OfType<SkipClause>().LastOrDefault();
        var limitClause = clauses.OfType<LimitClause>().LastOrDefault();
        if (orderBy is not { Items.Count: 1 } ||
            skipClause is null ||
            limitClause is null ||
            !TryGetNonNegativeLong(skipClause.Count, out skip) ||
            !TryGetNonNegativeLong(limitClause.Count, out limit) ||
            skip <= 0 ||
            limit <= 0)
        {
            return false;
        }

        descending = orderBy.Items[0].Descending;
        return true;
    }

    private static bool TryGetNonNegativeLong(CypherExpression expression, out long value)
    {
        switch (expression)
        {
            case Literal { Value: int intValue } when intValue >= 0:
                value = intValue;
                return true;
            case Literal { Value: long longValue } when longValue >= 0:
                value = longValue;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>
    /// Resolves one equality companion source per label. The optimization is all-or-nothing: a label
    /// without a declared equality property, or a non-string sort property, keeps the AGE accessor
    /// ordering for every branch so results never mix two ordering rules.
    /// </summary>
    private static List<AgePageSource> BuildSources(
        string[] labels,
        string sortProperty,
        LinqExpression queryExpression,
        AgeIndexCatalog catalog)
    {
        // Equality companion columns are text. Replacing a numeric ordering with text ordering
        // would be correct only for values that happen to sort identically, so require the CLR
        // member to be a string.
        if (!AgeIndexedAggregatePlanBuilder.CollectStringProperties(queryExpression).Contains(sortProperty))
        {
            return [];
        }

        var sources = new List<AgePageSource>(labels.Length);
        foreach (var label in labels)
        {
            if (!catalog.TryGetIndex(label, sortProperty, PropertyIndexKind.Equality, out var equality) ||
                equality.ColumnName.Length == 0)
            {
                return [];
            }

            sources.Add(new AgePageSource(label, equality.TableName, equality.ColumnName));
        }

        return sources;
    }

    private static bool TryParseBranch(
        IReadOnlyList<ICypherClause> clauses,
        ref string? alias,
        out string label,
        ref bool markerSeen)
    {
        string? branchAlias = null;
        string? branchLabel = null;
        for (var index = 0; index < clauses.Count; index++)
        {
            switch (clauses[index])
            {
                case MatchClause { Optional: true }:
                    // The complex-property load is an optional variable-length traversal; it must
                    // not be mistaken for a second root pattern.
                    break;

                case MatchClause { Patterns.Count: 1 } match when branchAlias is null:
                    if (match.Patterns[0].Elements.Count != 1 ||
                        match.Patterns[0].Elements[0] is not NodePattern
                        {
                            Alias: not null,
                            Labels.Count: 1,
                        } node)
                    {
                        label = string.Empty;
                        return false;
                    }

                    branchAlias = node.Alias;
                    branchLabel = node.Labels[0];

                    // A filter attached directly to the root MATCH would have to be repeated by the
                    // page-id pre-query, which this plan deliberately does not attempt.
                    if (index + 1 < clauses.Count && clauses[index + 1] is WhereClause)
                    {
                        label = string.Empty;
                        return false;
                    }

                    break;

                case MatchClause:
                    label = string.Empty;
                    return false;

                case WhereClause where:
                    markerSeen |= ContainsPropertyName(where.Predicate, ComplexPropertyMarker);
                    break;

                case SetOperationClause:
                    label = string.Empty;
                    return false;
            }
        }

        if (branchAlias is null || branchLabel is null)
        {
            label = string.Empty;
            return false;
        }

        if (alias is null)
        {
            alias = branchAlias;
        }
        else if (!string.Equals(alias, branchAlias, StringComparison.Ordinal))
        {
            label = string.Empty;
            return false;
        }

        label = branchLabel;
        return true;
    }

    private static bool TryGetSortProperty(
        IReadOnlyList<ICypherClause> clauses,
        int columnIndex,
        string alias,
        out string property)
    {
        property = string.Empty;
        var projection = clauses.OfType<ReturnClause>().LastOrDefault();
        if (projection is not null &&
            columnIndex >= 0 &&
            columnIndex < projection.Items.Count &&
            TryGetPropertyAccess(projection.Items[columnIndex].Expression, alias, out property))
        {
            return true;
        }

        // An entity projection returns the entity map for its single column, so the sort
        // expression never appears in the RETURN. Single-label queries keep the original
        // property access in the trailing ORDER BY; hierarchy branches also expose it there
        // (and additionally repeat it in the union sort column). postProcessing already
        // requires exactly one sort key, so a single-item ORDER BY is the same key.
        var orderBy = clauses.OfType<OrderByClause>().LastOrDefault();
        if (orderBy is not { Items.Count: 1 })
        {
            return false;
        }

        if (TryGetPropertyAccess(orderBy.Items[0].Expression, alias, out property))
        {
            return true;
        }

        // Some lowering shapes capture the sort expression in a preceding WITH item and order
        // by that alias. Resolve one alias indirection rather than giving up on the page plan.
        if (orderBy.Items[0].Expression is not VariableRef { Alias: var sortAlias })
        {
            property = string.Empty;
            return false;
        }

        for (var index = clauses.Count - 1; index >= 0; index--)
        {
            if (clauses[index] is not WithClause { Wildcard: false } with)
            {
                continue;
            }

            var item = with.Items.LastOrDefault(candidate =>
                string.Equals(candidate.Alias, sortAlias, StringComparison.Ordinal));
            if (item is not null && TryGetPropertyAccess(item.Expression, alias, out property))
            {
                return true;
            }
        }

        property = string.Empty;
        return false;
    }

    private static bool TryGetPropertyAccess(
        CypherExpression expression,
        string alias,
        out string property)
    {
        var (target, accessed) = expression switch
        {
            PropertyAccess access => (access.Target, access.Property),
            EscapedPropertyAccess access => (access.Target, access.Property),
            PhysicalPropertyAccess access => (access.Target, access.Property),
            _ => (null, null),
        };
        if (target is VariableRef { Alias: var targetAlias } &&
            accessed is not null &&
            string.Equals(targetAlias, alias, StringComparison.Ordinal))
        {
            property = accessed;
            return true;
        }

        property = string.Empty;
        return false;
    }

    private static bool ContainsPropertyName(CypherExpression expression, string property) =>
        expression switch
        {
            PropertyAccess access =>
                string.Equals(access.Property, property, StringComparison.Ordinal) ||
                ContainsPropertyName(access.Target, property),
            EscapedPropertyAccess access =>
                string.Equals(access.Property, property, StringComparison.Ordinal) ||
                ContainsPropertyName(access.Target, property),
            PhysicalPropertyAccess access =>
                string.Equals(access.Property, property, StringComparison.Ordinal) ||
                ContainsPropertyName(access.Target, property),
            CollectionPropertyAccess access =>
                string.Equals(access.Property, property, StringComparison.Ordinal) ||
                ContainsPropertyName(access.Target, property),
            BinaryExpression binary =>
                ContainsPropertyName(binary.Left, property) ||
                ContainsPropertyName(binary.Right, property),
            UnaryExpression unary => ContainsPropertyName(unary.Operand, property),
            ConjunctionExpression conjunction =>
                conjunction.Predicates.Any(predicate => ContainsPropertyName(predicate, property)),
            FunctionCall function =>
                function.Arguments.Any(argument => ContainsPropertyName(argument, property)),
            ListExpression list => list.Items.Any(item => ContainsPropertyName(item, property)),
            MapExpression map => map.Entries.Any(entry => ContainsPropertyName(entry.Value, property)),
            IndexExpression index =>
                ContainsPropertyName(index.Target, property) ||
                ContainsPropertyName(index.Index, property),
            CaseExpression @case =>
                ContainsPropertyName(@case.Condition, property) ||
                ContainsPropertyName(@case.WhenTrue, property) ||
                (@case.WhenFalse is not null && ContainsPropertyName(@case.WhenFalse, property)),
            CollectionContainsExpression contains =>
                ContainsPropertyName(contains.Collection, property) ||
                ContainsPropertyName(contains.Item, property),
            LabelTest label => ContainsPropertyName(label.Target, property),
            AllExpression all =>
                ContainsPropertyName(all.Source, property) ||
                ContainsPropertyName(all.Predicate, property),
            ReduceExpression reduce =>
                ContainsPropertyName(reduce.Seed, property) ||
                ContainsPropertyName(reduce.Source, property) ||
                ContainsPropertyName(reduce.Reducer, property),
            ListComprehensionExpression comprehension =>
                ContainsPropertyName(comprehension.Source, property) ||
                (comprehension.Predicate is not null &&
                 ContainsPropertyName(comprehension.Predicate, property)) ||
                (comprehension.Projection is not null &&
                 ContainsPropertyName(comprehension.Projection, property)),
            _ => false,
        };

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

            if (setOperations.Length != 1 ||
                current.Count != 1 ||
                !setOperations[0].PreserveDuplicates)
            {
                valid = false;
                return;
            }

            Visit(setOperations[0].First);
            Visit(setOperations[0].Second);
        }
    }
}
