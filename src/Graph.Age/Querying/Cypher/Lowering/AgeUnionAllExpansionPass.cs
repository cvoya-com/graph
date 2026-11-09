// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Lowering;

using Cvoya.Graph.Age.Serialization;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using Cvoya.Graph.Cypher.Validation;

/// <summary>
/// Rewrites a node pattern whose labels are alternatives into one query branch per physical
/// label, joined with <c>UNION ALL</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NodePattern.Labels"/> is a label <em>alternative</em> list in the shared AST: both
/// the AGE and Neo4j dialects render it as a label disjunction (<c>:A|B</c>). Apache AGE does not
/// accept that syntax, so the AGE provider must lower the list itself. Doing so as a
/// <c>UNION ALL</c> of single-label patterns is what lets AGE open exactly the label tables of the
/// type hierarchy instead of scanning every vertex table behind a <c>labels(v)</c> predicate.
/// </para>
/// <para>
/// A single label that has registered subtypes is expanded the same way, using
/// <see cref="AgeTypeHierarchyRegistry.GetSubLabels"/>.
/// </para>
/// <para>
/// This pass runs at the very end of lowering, after label predicates and entity projections have
/// been produced. The statement is duplicated per branch, so every branch renders a complete
/// query. AGE attaches a trailing <c>ORDER BY</c>/<c>SKIP</c>/<c>LIMIT</c> to the final branch of
/// a union instead of the combined result and cannot execute a <c>CALL { }</c> wrapper, so those
/// clauses are lifted into <see cref="CypherStatement.PostProcessing"/> and re-applied by the
/// execution wrapper. <c>RETURN DISTINCT</c> becomes a distinct <c>UNION</c>, which deduplicates
/// the combined result on the server. Partial aggregate rows are combined the same way.
/// </para>
/// <para>
/// Expansion is skipped when per-branch evaluation would change the result in a way the wrapper
/// cannot repair: optional matches, scoped CALL subqueries, multiple expandable nodes, grouping
/// stages other than the final aggregate, and aggregates that cannot be merged additively. A lone
/// global <c>count(...)</c> is the additive special case: per-label counts are summed by the
/// wrapper, and the provider also collapses raw union-count rows for compatibility.
/// </para>
/// </remarks>
internal sealed class AgeUnionAllExpansionPass : ICypherPass
{
    internal const string SortColumnPrefix = "__age_union_sort";

    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "avg",
        "collect",
        "count",
        "max",
        "min",
        "percentileCont",
        "percentileDisc",
        "stDev",
        "stDevP",
        "sum",
    };

    /// <summary>Aggregate functions whose partial results combine by summation.</summary>
    private static readonly HashSet<string> AdditiveAggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "count",
        "sum",
    };

    /// <inheritdoc />
    public CypherStatement Run(CypherStatement input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var clauses = ExpandBranch(
            input.Clauses,
            allowPostProcessing: true,
            out var changed,
            out var postProcessing);
        return changed
            ? new CypherStatement(clauses, input.Parameters, input.PathTypes)
            {
                PostProcessing = postProcessing ?? input.PostProcessing,
            }
            : input;
    }

    /// <summary>
    /// Chooses the single node pattern that this statement may expand into <c>UNION ALL</c>, or
    /// <see langword="null"/> when the statement must keep the containment fallback.
    /// </summary>
    /// <remarks>
    /// <see cref="AgeLabelPatternPass"/> consults the same selection before it decides whether to
    /// keep a pattern's labels in the MATCH clause. Both callers must therefore share this method.
    /// </remarks>
    public static ExpansionTarget? SelectExpansion(CypherStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return SelectExpansion(statement.Clauses, allowPostProcessing: true);
    }

    /// <summary>
    /// Chooses the single node pattern that one linear query branch may expand, or
    /// <see langword="null"/> when the branch must keep the containment fallback.
    /// </summary>
    /// <param name="clauses">The clauses of one query branch, without a set operation.</param>
    /// <param name="allowPostProcessing">
    /// Whether this branch is the outermost statement, so lifted ordering, paging, and aggregate
    /// combination can be applied by the execution wrapper. Branches of an existing set operation
    /// must pass <see langword="false"/>.
    /// </param>
    public static ExpansionTarget? SelectExpansion(
        IReadOnlyList<ICypherClause> clauses,
        bool allowPostProcessing)
    {
        ArgumentNullException.ThrowIfNull(clauses);

        if (clauses.Any(clause => clause is SetOperationClause or FullTextSearchClause))
        {
            return null;
        }

        if (!TryFindPostProcessing(clauses, out var bodyCount, out var orderBy, out var skip, out var limit))
        {
            return null;
        }

        var hasPostProcessing = orderBy is not null || skip is not null || limit is not null;
        if (hasPostProcessing && !allowPostProcessing)
        {
            return null;
        }

        // An entity projection with captured ordering is left in place by AgeClauseOrderPass, so
        // AgeClauseOrderPass cannot move a separate paging tail after it. An entity projection
        // that loads complex properties introduces a grouped `collect` stage; a following tail
        // would page the collected groups instead of the underlying rows. Both shapes keep the
        // containment fallback.
        if (clauses.OfType<EntityProjectionClause>().LastOrDefault() is { } entityProjection)
        {
            var projectsComplexProperties =
                entityProjection.LoadSourceProperties || entityProjection.LoadTargetProperties;
            if (entityProjection.Ordering.Count > 0 ||
                (projectsComplexProperties && bodyCount >= 0 && bodyCount != clauses.Count))
            {
                return null;
            }
        }

        // Lifted ordering and paging are only supported when every trailing clause follows the
        // final RETURN (the shape AgeClauseOrderPass produces).
        if (bodyCount >= 0 && bodyCount != clauses.Count && clauses.Skip(bodyCount).Any(clause =>
            clause is not OrderByClause and not SkipClause and not LimitClause))
        {
            return null;
        }

        ExpansionTarget? target = null;

        foreach (var clause in clauses)
        {
            if (clause is not MatchClause match)
            {
                continue;
            }

            if (match.Optional && ContainsLabeledNode([match]))
            {
                return null;
            }

            if (match.Optional)
            {
                continue;
            }

            foreach (var node in match.Patterns
                .SelectMany(pattern => pattern.Elements)
                .OfType<NodePattern>())
            {
                if (node.Alias is null || !TryGetAlternatives(node, out _))
                {
                    continue;
                }

                if (target is not null)
                {
                    // Two expandable nodes cannot be represented by a single union without a
                    // branch cross product; keep the containment fallback.
                    return null;
                }

                target = new ExpansionTarget(node.Alias);
            }
        }

        if (target is null)
        {
            return null;
        }

        foreach (var clause in clauses)
        {
            switch (clause)
            {
                case CallSubqueryClause subquery when ContainsLabeledNode(subquery.Body):
                    return null;

                case WithClause with when with.Wildcard is false && with.Distinct:
                    // DISTINCT inside a grouping stage is part of the row pipeline; deduplicating
                    // the union cannot reproduce it.
                    return null;

                case WithClause:
                case ReturnClause:
                    break;
            }
        }

        if (!HasSpanningAggregation(clauses, target.Alias))
        {
            return target;
        }

        // A spanning aggregate only expands when the wrapper can merge the per-branch partial
        // rows: that needs a top-level statement and a projection without ordering or paging.
        return allowPostProcessing &&
            !hasPostProcessing &&
            TryBuildAggregation(clauses, target.Alias, out _)
            ? target
            : null;
    }

    /// <summary>
    /// Gets whether <paramref name="node"/> carries labels that this pass expands.
    /// </summary>
    /// <param name="node">The node pattern.</param>
    /// <param name="alternatives">The physical labels to visit, including the pattern label itself.</param>
    public static bool TryGetAlternatives(NodePattern node, out IReadOnlyList<string> alternatives)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.Labels.Count > 1)
        {
            alternatives = node.Labels;
            return true;
        }

        if (node.Labels.Count == 1)
        {
            var label = node.Labels[0];
            var subLabels = AgeTypeHierarchyRegistry.Instance.GetSubLabels(label);
            if (subLabels.Count > 1)
            {
                alternatives = subLabels;
                return true;
            }
        }

        alternatives = [];
        return false;
    }

    /// <summary>
    /// Locates the ordering and paging clauses that must be lifted to the execution wrapper.
    /// </summary>
    /// <remarks>
    /// The clauses can be on either side of the projection at this point in lowering:
    /// <c>AgeClauseOrderPass</c> moves them after the projection, but the label pass runs before
    /// that pass and sees the planner's pre-projection order. Only the post-projection shape is
    /// executable by <see cref="Expand"/>; the label pass uses the same analysis to keep the
    /// expanded pattern's labels so the final pass has something to expand.
    /// </remarks>
    private static bool TryFindPostProcessing(
        IReadOnlyList<ICypherClause> clauses,
        out int bodyCount,
        out OrderByClause? orderBy,
        out int? skip,
        out int? limit)
    {
        bodyCount = clauses.Count;
        orderBy = null;
        skip = null;
        limit = null;

        var lastProjection = -1;
        for (var index = 0; index < clauses.Count; index++)
        {
            if (clauses[index] is ReturnClause or EntityProjectionClause)
            {
                lastProjection = index;
            }
        }

        if (lastProjection < 0)
        {
            return false;
        }

        var pagingIndexes = new List<int>();
        for (var index = 0; index < clauses.Count; index++)
        {
            if (clauses[index] is OrderByClause or SkipClause or LimitClause)
            {
                pagingIndexes.Add(index);
            }
        }

        if (pagingIndexes.Count == 0)
        {
            return true;
        }

        bool afterProjection;
        if (pagingIndexes[0] > lastProjection)
        {
            // AgeClauseOrderPass shape: Match/Where, projection, ORDER BY, SKIP, LIMIT.
            bodyCount = lastProjection + 1;
            afterProjection = true;
            if (pagingIndexes[^1] != clauses.Count - 1)
            {
                return false;
            }
        }
        else if (pagingIndexes[^1] < lastProjection)
        {
            // Planner shape: Match/Where, ORDER BY, SKIP, LIMIT, projection. Kept labels are safe
            // because AgeClauseOrderPass moves the clauses after the projection before expansion
            // runs.
            afterProjection = false;
            bodyCount = -1;
            if (pagingIndexes[^1] != lastProjection - 1)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        foreach (var index in pagingIndexes)
        {
            switch (clauses[index])
            {
                case OrderByClause clause when orderBy is null:
                    orderBy = clause;
                    break;
                case SkipClause clause when skip is null && TryGetNonNegativeInt(clause.Count, out var skipValue):
                    skip = skipValue;
                    break;
                case LimitClause clause when limit is null && TryGetNonNegativeInt(clause.Count, out var limitValue):
                    limit = limitValue;
                    break;
                default:
                    return false;
            }

            if (!afterProjection)
            {
                // In the planner shape the block must be contiguous right before the projection.
                if (index < lastProjection - pagingIndexes.Count ||
                    index >= lastProjection)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryGetNonNegativeInt(CypherExpression expression, out int value)
    {
        if (expression is Literal { Value: int intValue } && intValue >= 0)
        {
            value = intValue;
            return true;
        }

        if (expression is Literal { Value: long longValue } && longValue is >= 0 and <= int.MaxValue)
        {
            value = (int)longValue;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool ContainsLabeledNode(IReadOnlyList<ICypherClause> clauses)
    {
        foreach (var clause in clauses)
        {
            switch (clause)
            {
                case MatchClause match:
                    if (match.Patterns
                        .SelectMany(pattern => pattern.Elements)
                        .OfType<NodePattern>()
                        .Any(node => node.Labels.Count > 0))
                    {
                        return true;
                    }

                    break;

                case CallSubqueryClause subquery:
                    if (ContainsLabeledNode(subquery.Body))
                    {
                        return true;
                    }

                    break;

                case SetOperationClause setOperation:
                    if (ContainsLabeledNode(setOperation.First) ||
                        ContainsLabeledNode(setOperation.Second))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static bool ContainsAggregate(CypherExpression expression) => expression switch
    {
        FunctionCall function => AggregateFunctions.Contains(function.Name) ||
            function.Arguments.Any(ContainsAggregate),
        BinaryExpression binary => ContainsAggregate(binary.Left) || ContainsAggregate(binary.Right),
        UnaryExpression unary => ContainsAggregate(unary.Operand),
        PropertyAccess property => ContainsAggregate(property.Target),
        EscapedPropertyAccess property => ContainsAggregate(property.Target),
        PhysicalPropertyAccess property => ContainsAggregate(property.Target),
        CollectionPropertyAccess property => ContainsAggregate(property.Target),
        CollectionContainsExpression contains =>
            ContainsAggregate(contains.Collection) || ContainsAggregate(contains.Item),
        ListExpression list => list.Items.Any(ContainsAggregate),
        ListComprehensionExpression comprehension =>
            ContainsAggregate(comprehension.Source) ||
            (comprehension.Predicate is not null && ContainsAggregate(comprehension.Predicate)) ||
            (comprehension.Projection is not null && ContainsAggregate(comprehension.Projection)),
        ReduceExpression reduce =>
            ContainsAggregate(reduce.Seed) ||
            ContainsAggregate(reduce.Source) ||
            ContainsAggregate(reduce.Reducer),
        AllExpression all => ContainsAggregate(all.Source) || ContainsAggregate(all.Predicate),
        MapExpression map => map.Entries.Any(entry => ContainsAggregate(entry.Value)),
        IndexExpression index => ContainsAggregate(index.Target) || ContainsAggregate(index.Index),
        CaseExpression @case =>
            ContainsAggregate(@case.Condition) ||
            ContainsAggregate(@case.WhenTrue) ||
            (@case.WhenFalse is not null && ContainsAggregate(@case.WhenFalse)),
        ConjunctionExpression conjunction => conjunction.Predicates.Any(ContainsAggregate),
        PatternSubqueryExpression subquery =>
            subquery.Predicate is not null && ContainsAggregate(subquery.Predicate),
        PatternComprehensionExpression comprehension =>
            ContainsAggregate(comprehension.Projection) ||
            (comprehension.Predicate is not null && ContainsAggregate(comprehension.Predicate)),
        _ => false,
    };

    /// <summary>
    /// Expands each side of a set operation independently, then expands one linear branch.
    /// </summary>
    private static ICypherClause[] ExpandBranch(
        IReadOnlyList<ICypherClause> clauses,
        bool allowPostProcessing,
        out bool changed,
        out CypherPostProcessing? postProcessing)
    {
        changed = false;
        postProcessing = null;
        var output = new List<ICypherClause>(clauses.Count);

        foreach (var clause in clauses)
        {
            if (clause is not SetOperationClause setOperation)
            {
                output.Add(clause);
                continue;
            }

            // A nested set-operation branch cannot have its own lifted ordering or paging: the
            // wrapper sees one combined result. Keep those branches on the containment fallback.
            var first = ExpandBranch(
                setOperation.First,
                allowPostProcessing: false,
                out var firstChanged,
                out _);
            var second = ExpandBranch(
                setOperation.Second,
                allowPostProcessing: false,
                out var secondChanged,
                out _);
            output.Add(firstChanged || secondChanged
                ? new SetOperationClause(first, second, setOperation.PreserveDuplicates)
                : setOperation);
            changed |= firstChanged || secondChanged;
        }

        if (changed || SelectExpansion(clauses, allowPostProcessing) is not { } target)
        {
            return output.ToArray();
        }

        changed = true;
        return Expand(clauses, target, allowPostProcessing, out postProcessing);
    }

    private static ICypherClause[] Expand(
        IReadOnlyList<ICypherClause> clauses,
        ExpansionTarget target,
        bool allowPostProcessing,
        out CypherPostProcessing? postProcessing)
    {
        postProcessing = null;

        IReadOnlyList<string> alternatives = [];
        foreach (var clause in clauses)
        {
            if (clause is not MatchClause match)
            {
                continue;
            }

            foreach (var node in match.Patterns
                .SelectMany(pattern => pattern.Elements)
                .OfType<NodePattern>())
            {
                if (string.Equals(node.Alias, target.Alias, StringComparison.Ordinal) &&
                    TryGetAlternatives(node, out var nodeAlternatives))
                {
                    alternatives = nodeAlternatives;
                }
            }
        }

        if (alternatives.Count < 2 ||
            !TryFindPostProcessing(clauses, out var bodyCount, out var orderBy, out var skip, out var limit) ||
            bodyCount < 0)
        {
            return clauses.ToArray();
        }

        if (!allowPostProcessing && (orderBy is not null || skip is not null || limit is not null))
        {
            return clauses.ToArray();
        }

        var body = clauses.Take(bodyCount).ToArray();
        if (body.Length == 0 || body[^1] is not ReturnClause finalReturn)
        {
            return clauses.ToArray();
        }

        var spanningAggregation = HasSpanningAggregation(body, target.Alias);
        var aggregation = TryBuildAggregation(body, target.Alias, out var aggregationPlan)
            ? aggregationPlan
            : null;
        if (spanningAggregation && aggregation is null)
        {
            return clauses.ToArray();
        }

        if (aggregation is not null && (orderBy is not null || skip is not null || limit is not null))
        {
            return clauses.ToArray();
        }

        var distinct = finalReturn.Distinct;
        if (distinct && (aggregation is not null || spanningAggregation))
        {
            return clauses.ToArray();
        }

        var resultColumnCount = finalReturn.Items.Count;
        var branches = alternatives
            .Select(label => RewriteBranch(body, target.Alias, label))
            .ToArray();

        var sortColumns = new List<CypherPostSort>();
        if (orderBy is not null && aggregation is null)
        {
            for (var index = 0; index < orderBy.Items.Count; index++)
            {
                var sortAlias = $"{SortColumnPrefix}{index}";
                sortColumns.Add(new CypherPostSort(resultColumnCount + index, orderBy.Items[index].Descending));
                for (var branchIndex = 0; branchIndex < branches.Length; branchIndex++)
                {
                    branches[branchIndex] = AppendReturnItem(
                        branches[branchIndex],
                        new ReturnItem(orderBy.Items[index].Expression, sortAlias),
                        clearDistinct: distinct);
                }
            }
        }
        else if (distinct)
        {
            for (var branchIndex = 0; branchIndex < branches.Length; branchIndex++)
            {
                branches[branchIndex] = AppendReturnItem(
                    branches[branchIndex],
                    item: null,
                    clearDistinct: true);
            }
        }

        if (orderBy is not null && aggregation is null && limit is not null)
        {
            // Per-branch top-N: the wrapper still merges and re-orders the combined rows, but
            // bounding each branch lets an index on the ordering property satisfy the sort and
            // keeps the SQL sort over at most branches * (skip + limit) rows.
            var branchLimit = (long)(skip ?? 0) + limit.Value;
            for (var branchIndex = 0; branchIndex < branches.Length; branchIndex++)
            {
                branches[branchIndex] =
                [
                    .. branches[branchIndex],
                    new OrderByClause(orderBy.Items),
                    new LimitClause(new Literal((int)Math.Min(branchLimit, int.MaxValue))),
                ];
            }
        }

        // A DISTINCT projection without ordering deduplicates after the union, which lets
        // PostgreSQL use a hash aggregate over the combined rows instead of sorting every branch.
        // With ordering the distinct union is retained so the dedupe and ordering share one sort.
        var distinctInWrapper = distinct && orderBy is null && allowPostProcessing;
        var preserveDuplicates = !distinct || distinctInWrapper;
        ICypherClause[] combined = branches[0];
        for (var index = 1; index < branches.Length; index++)
        {
            combined = [new SetOperationClause(combined, branches[index], preserveDuplicates)];
        }

        if (sortColumns.Count > 0 || skip is not null || limit is not null ||
            aggregation is not null || distinctInWrapper)
        {
            postProcessing = new CypherPostProcessing(
                sortColumns,
                skip,
                limit,
                resultColumnCount,
                aggregation,
                distinctInWrapper);
        }

        return combined;
    }

    private static ICypherClause[] AppendReturnItem(
        ICypherClause[] branch,
        ReturnItem? item,
        bool clearDistinct)
    {
        for (var index = branch.Length - 1; index >= 0; index--)
        {
            if (branch[index] is not ReturnClause projection)
            {
                continue;
            }

            var items = item is null
                ? projection.Items
                : [.. projection.Items, item];
            var rewritten = branch.ToArray();
            rewritten[index] = new ReturnClause(items, clearDistinct ? false : projection.Distinct);
            return rewritten;
        }

        return branch;
    }

    /// <summary>
    /// Gets whether an aggregate in a <c>WITH</c> stage would combine rows that the union keeps in
    /// separate branches. Grouping by the expanded node alias is safe because a vertex stores
    /// exactly one physical label and therefore belongs to exactly one branch.
    /// </summary>
    private static bool AggregationSpansBranches(IReadOnlyList<ReturnItem> items, string alias)
    {
        if (!items.Any(item => ContainsAggregate(item.Expression)))
        {
            return false;
        }

        var grouping = items
            .Where(item => !ContainsAggregate(item.Expression))
            .ToArray();

        // A group key that contains the expanded alias cannot span branches: a vertex stores
        // exactly one physical label and therefore belongs to exactly one branch. Other keys in
        // the same grouping tuple narrow the groups but cannot merge rows across branches.
        if (grouping.Any(item =>
            item.Expression is VariableRef variable &&
            string.Equals(variable.Alias, alias, StringComparison.Ordinal)))
        {
            return false;
        }

        return true;
    }

    private static bool HasSpanningAggregation(IReadOnlyList<ICypherClause> clauses, string alias) =>
        clauses.OfType<WithClause>().Any(with =>
            !with.Wildcard && AggregationSpansBranches(with.Items, alias)) ||
        clauses.OfType<ReturnClause>().Any(@return =>
            AggregationSpansBranches(@return.Items, alias));

    /// <summary>
    /// Builds the plan that merges per-branch partial aggregate rows. Only additive aggregates
    /// (<c>count</c> and <c>sum</c>) have a lossless merge without shipping every row back.
    /// </summary>
    private static bool TryBuildAggregation(
        IReadOnlyList<ICypherClause> body,
        string alias,
        out CypherPostAggregation? aggregation)
    {
        aggregation = null;

        var projection = body.OfType<ReturnClause>().LastOrDefault();
        if (projection is null)
        {
            return false;
        }

        var groupingStage = body
            .OfType<WithClause>()
            .LastOrDefault(with => !with.Wildcard && with.Items.Any(item => ContainsAggregate(item.Expression)));

        if (groupingStage is not null)
        {
            if (groupingStage.Distinct ||
                !AggregationSpansBranches(groupingStage.Items, alias))
            {
                // Either the stage is not a mergeable aggregate, or its groups are disjoint per
                // branch and the plain union is already exact.
                return !AggregationSpansBranches(groupingStage.Items, alias);
            }

            var groups = new List<int>();
            var aggregates = new List<CypherPostAggregate>();
            for (var index = 0; index < projection.Items.Count; index++)
            {
                if (projection.Items[index].Expression is not VariableRef reference)
                {
                    return false;
                }

                var sourceIndex = IndexOfAlias(groupingStage.Items, reference.Alias);
                if (sourceIndex < 0)
                {
                    return false;
                }

                var source = groupingStage.Items[sourceIndex];
                if (ContainsAggregate(source.Expression))
                {
                    if (!TryCombineAggregate(source.Expression, out var function))
                    {
                        return false;
                    }

                    aggregates.Add(new CypherPostAggregate(index, function));
                }
                else
                {
                    groups.Add(index);
                }
            }

            if (aggregates.Count == 0)
            {
                return false;
            }

            aggregation = new CypherPostAggregation(groups, aggregates);
            return true;
        }

        if (!AggregationSpansBranches(projection.Items, alias))
        {
            // No spanning aggregate. This is either an ordinary projection or an aggregate whose
            // groups are disjoint per branch.
            return true;
        }

        var directGroups = new List<int>();
        var directAggregates = new List<CypherPostAggregate>();
        for (var index = 0; index < projection.Items.Count; index++)
        {
            var expression = projection.Items[index].Expression;
            if (ContainsAggregate(expression))
            {
                if (!TryCombineAggregate(expression, out var function))
                {
                    return false;
                }

                directAggregates.Add(new CypherPostAggregate(index, function));
            }
            else
            {
                directGroups.Add(index);
            }
        }

        if (directAggregates.Count == 0)
        {
            return false;
        }

        aggregation = new CypherPostAggregation(directGroups, directAggregates);
        return true;
    }

    private static int IndexOfAlias(IReadOnlyList<ReturnItem> items, string alias)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (string.Equals(items[index].Alias, alias, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryCombineAggregate(CypherExpression expression, out string function)
    {
        if (expression is FunctionCall call && AdditiveAggregateFunctions.Contains(call.Name))
        {
            function = "sum";
            return true;
        }

        function = string.Empty;
        return false;
    }

    private static ICypherClause[] RewriteBranch(
        IReadOnlyList<ICypherClause> clauses,
        string alias,
        string physicalLabel)
    {
        var rewritten = new ICypherClause[clauses.Count];
        for (var index = 0; index < clauses.Count; index++)
        {
            rewritten[index] = clauses[index] is MatchClause match
                ? RewriteMatch(match, alias, physicalLabel)
                : clauses[index];
        }

        return rewritten;
    }

    private static MatchClause RewriteMatch(MatchClause match, string alias, string physicalLabel)
    {
        var changed = false;
        var patterns = new PathPattern[match.Patterns.Count];
        for (var patternIndex = 0; patternIndex < match.Patterns.Count; patternIndex++)
        {
            var pattern = match.Patterns[patternIndex];
            var elements = pattern.Elements.ToArray();
            for (var elementIndex = 0; elementIndex < elements.Length; elementIndex++)
            {
                if (elements[elementIndex] is NodePattern node &&
                    string.Equals(node.Alias, alias, StringComparison.Ordinal))
                {
                    // NodePattern labels are logical labels; AGE tables use the encoded storage
                    // name for labels that are not plain symbolic identifiers.
                    elements[elementIndex] = new NodePattern(
                        node.Alias,
                        [SerializationBridge.GetRootStorageName(physicalLabel, relationship: false)]);
                    changed = true;
                }
            }

            patterns[patternIndex] = changed
                ? new PathPattern(elements, pattern.Alias, pattern.Selection)
                : pattern;
        }

        return changed ? new MatchClause(patterns, match.Optional) : match;
    }

    /// <summary>Identifies the node pattern that a statement expands.</summary>
    /// <param name="Alias">The alias of the expandable node.</param>
    public sealed record ExpansionTarget(string Alias);
}
