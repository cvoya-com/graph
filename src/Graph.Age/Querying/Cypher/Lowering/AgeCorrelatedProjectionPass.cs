// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Lowering;

using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using Cvoya.Graph.Cypher.Validation;

/// <summary>
/// Lowers correlated collection and pattern-subquery projections to AGE-compatible grouped matches.
/// </summary>
/// <remarks>
/// AGE 1.7 executes neither pattern comprehensions, nor <c>CALL { }</c> subqueries, nor
/// <c>EXISTS { }</c>/<c>COUNT { }</c> pattern subqueries (the latter two parse but silently match
/// nothing). Correlated collection projections share one traversal pattern, so the pass matches that
/// traversal once and groups its rows with <c>collect</c>/<c>count</c>; per-projection filters become
/// conditional aggregation (<c>CASE</c> producing <c>NULL</c>, which every aggregate skips) so one
/// filtered projection cannot narrow the rows its siblings aggregate over, and the anchoring
/// existence filter becomes a grouped row-count guard. Remaining pattern subqueries — relationship
/// degrees, complex-collection sizes, and existence filters — become sequential optional matches so
/// zero-degree roots survive and multiple counts cannot multiply one another through a Cartesian
/// product.
/// </remarks>
internal sealed class AgeCorrelatedProjectionPass : ICypherPass
{
    private const string ProjectionAliasPrefix = "__age_projection";
    private const string AnchorRowsAlias = "__age_anchor_rows";
    private readonly string countAliasPrefix;

    public AgeCorrelatedProjectionPass(string countAliasPrefix = "__age_count")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(countAliasPrefix);
        this.countAliasPrefix = countAliasPrefix;
    }

    /// <inheritdoc />
    public CypherStatement Run(CypherStatement input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var statement = TryFindCorrelatedAnchor(input.Clauses, out var whereIndex, out var anchor, out var remainder)
            ? LowerCorrelatedCollection(input, whereIndex, anchor, remainder)
            : input;
        return LowerPatternSubqueryProjections(statement, countAliasPrefix);
    }

    private static CypherStatement LowerCorrelatedCollection(
        CypherStatement input,
        int whereIndex,
        PatternSubqueryExpression anchor,
        CypherExpression? remainder)
    {
        // The anchored plan is [root match, filter, subqueries/paging…, return]: no subquery may
        // precede the filter, and between the filter and RETURN only correlated subqueries and
        // final-row ordering/paging clauses may appear. The latter page the final rows, which the
        // lowering computes only after its grouping stage and row guard — so they move behind the
        // lowered RETURN, where the clause-order pass places them for rendering.
        var returnIndex = FindReturnIndex(input.Clauses);
        var carriedPaging = new List<ICypherClause>();
        for (var index = 0; index < returnIndex; index++)
        {
            if (index <= whereIndex
                ? input.Clauses[index] is CallSubqueryClause
                : !ClassifyIntermediateClause(input.Clauses[index], carriedPaging))
            {
                throw Unsupported("a correlated projection with an unexpected clause layout");
            }
        }

        var outerReturn = (ReturnClause)input.Clauses[returnIndex];
        var calls = input.Clauses
            .Take(returnIndex)
            .OfType<CallSubqueryClause>()
            .Select(ParseCall)
            .ToArray();
        if (calls.Any(call => call.Kind == CorrelatedCallKind.NestedGrouping))
        {
            return LowerNestedGrouping(
                input,
                whereIndex,
                anchor,
                remainder,
                returnIndex,
                outerReturn,
                calls,
                carriedPaging);
        }

        var orderedCalls = calls.Where(call => call.Kind == CorrelatedCallKind.OrderedCollection).ToArray();
        if (orderedCalls.Length > 1)
        {
            throw Unsupported("multiple ordered correlated collections");
        }

        var clauses = BuildCorrelatedPrefix(input.Clauses, whereIndex, remainder, anchor, applyAnchorPredicate: false);
        if (orderedCalls is [var ordered])
        {
            clauses.Add(ordered.Carry!);
            clauses.Add(ordered.OrderBy!);
        }

        // The shared match is unfiltered, so each per-call filter (the segment predicates plus any
        // Where inside the correlated operation) becomes conditional aggregation: filtered rows
        // contribute NULL, which collect/count/avg/sum/min/max all skip. This keeps one filtered
        // projection from narrowing the rows its siblings aggregate over.
        var callExpressions = calls.ToDictionary(
            call => call.Alias,
            ApplyCallPredicate,
            StringComparer.Ordinal);
        var aggregationItems = new List<ReturnItem>
        {
            new(new VariableRef(SourceAlias(anchor.Pattern)), null),
        };
        var rewrittenItems = new ReturnItem[outerReturn.Items.Count];
        for (var index = 0; index < outerReturn.Items.Count; index++)
        {
            var expression = outerReturn.Items[index].Expression;
            if (TryLowerCorrelatedExpression(expression, callExpressions, anchor, out var aggregate))
            {
                var alias = $"{ProjectionAliasPrefix}{index}";
                aggregationItems.Add(new ReturnItem(aggregate, alias));
                expression = new VariableRef(alias);
            }

            rewrittenItems[index] = new ReturnItem(expression, outerReturn.Items[index].Alias);
        }

        // The consumed EXISTS anchored the row set: sources whose traversal rows all fail the anchor
        // predicate must produce no result row, not empty aggregates. Count the qualifying rows in
        // the same grouping pass and filter on that count afterwards.
        if (anchor.Predicate is not null)
        {
            aggregationItems.Add(new ReturnItem(
                Function("count", Conditional(anchor.Predicate, CountTarget(anchor.Pattern))),
                AnchorRowsAlias));
        }

        clauses.Add(new WithClause(aggregationItems, distinct: false));
        if (anchor.Predicate is not null)
        {
            clauses.Add(new WhereClause(new BinaryExpression(
                CypherBinaryOperator.GreaterThan,
                new VariableRef(AnchorRowsAlias),
                new Literal(0))));
        }

        clauses.Add(new ReturnClause(rewrittenItems, outerReturn.Distinct));
        clauses.AddRange(carriedPaging);
        AppendTrailingClauses(clauses, input.Clauses, returnIndex);
        return new CypherStatement(clauses, input.Parameters, input.PathTypes);
    }

    private static bool ClassifyIntermediateClause(ICypherClause clause, List<ICypherClause> carriedPaging)
    {
        switch (clause)
        {
            case CallSubqueryClause:
                return true;
            case OrderByClause or SkipClause or LimitClause:
                carriedPaging.Add(clause);
                return true;
            default:
                return false;
        }
    }

    private static CypherStatement LowerNestedGrouping(
        CypherStatement input,
        int whereIndex,
        PatternSubqueryExpression anchor,
        CypherExpression? remainder,
        int returnIndex,
        ReturnClause outerReturn,
        IReadOnlyList<CorrelatedCall> calls,
        IReadOnlyList<ICypherClause> carriedPaging)
    {
        if (calls is not [{ Kind: CorrelatedCallKind.NestedGrouping } nested] ||
            ContainsCorrelatedExpression(outerReturn.Items.Select(item => item.Expression), nested.Alias))
        {
            throw Unsupported("a nested correlated grouping combined with another correlated projection");
        }

        // The traversal rows are re-grouped by the inner key, so a filter must remove rows before
        // that grouping rather than becoming conditional aggregation — otherwise fully-filtered keys
        // would still surface as empty groups. Only the shared traversal predicate (mirrored by the
        // anchor) is expressible as a row filter without dropping owners the anchor keeps.
        if (Conjuncts(nested.Predicate).Count != Conjuncts(anchor.Predicate).Count)
        {
            throw Unsupported("a filtered nested correlated grouping");
        }

        var clauses = BuildCorrelatedPrefix(input.Clauses, whereIndex, remainder, anchor, applyAnchorPredicate: true);
        var sourceAlias = SourceAlias(anchor.Pattern);
        clauses.Add(nested.NestedKey!);

        var groupedItems = new List<ReturnItem>
        {
            new(new VariableRef(sourceAlias), null),
        };
        groupedItems.AddRange(nested.NestedAggregates!.Items);
        clauses.Add(new WithClause(groupedItems, nested.NestedAggregates.Distinct));
        clauses.Add(new WithClause(
        [
            new ReturnItem(new VariableRef(sourceAlias), null),
            new ReturnItem(nested.Expression, nested.Alias),
        ],
        distinct: false));
        clauses.Add(outerReturn);
        clauses.AddRange(carriedPaging);
        AppendTrailingClauses(clauses, input.Clauses, returnIndex);
        return new CypherStatement(clauses, input.Parameters, input.PathTypes);
    }

    private static List<ICypherClause> BuildCorrelatedPrefix(
        IReadOnlyList<ICypherClause> clauses,
        int whereIndex,
        CypherExpression? remainder,
        PatternSubqueryExpression anchor,
        bool applyAnchorPredicate)
    {
        var output = new List<ICypherClause>();
        for (var index = 0; index < whereIndex; index++)
        {
            output.Add(clauses[index]);
        }

        if (remainder is not null)
        {
            output.Add(new WhereClause(remainder));
        }

        output.Add(new MatchClause([anchor.Pattern], optional: false));
        if (applyAnchorPredicate && anchor.Predicate is not null)
        {
            output.Add(new WhereClause(anchor.Predicate));
        }

        return output;
    }

    private static CorrelatedCall ParseCall(CallSubqueryClause call)
    {
        if (call.Body[^1] is not ReturnClause { Items: [var result] } || result.Alias is null)
        {
            throw Unsupported("a correlated subquery without one named result");
        }

        var whereClauses = call.Body.OfType<WhereClause>().ToArray();
        if (whereClauses.Length > 1)
        {
            throw Unsupported("a correlated subquery with more than one filter");
        }

        var predicate = whereClauses.FirstOrDefault()?.Predicate;
        var withClauses = call.Body.OfType<WithClause>().ToArray();
        var orderBy = call.Body.OfType<OrderByClause>().SingleOrDefault();
        return withClauses.Length switch
        {
            0 when orderBy is null => new CorrelatedCall(
                CorrelatedCallKind.Aggregate,
                result.Alias,
                result.Expression,
                predicate),
            1 when orderBy is not null => new CorrelatedCall(
                CorrelatedCallKind.OrderedCollection,
                result.Alias,
                result.Expression,
                predicate,
                Carry: withClauses[0],
                OrderBy: orderBy),
            2 when orderBy is null => new CorrelatedCall(
                CorrelatedCallKind.NestedGrouping,
                result.Alias,
                result.Expression,
                predicate,
                NestedKey: withClauses[0],
                NestedAggregates: withClauses[1]),
            _ => throw Unsupported("an unrecognized correlated subquery body"),
        };
    }

    private static CypherExpression ApplyCallPredicate(CorrelatedCall call)
    {
        if (call.Predicate is null)
        {
            return call.Expression;
        }

        if (call.Expression is FunctionCall { Arguments: [var argument] } aggregate)
        {
            return Function(aggregate.Name, Conditional(call.Predicate, argument));
        }

        throw Unsupported("a filtered correlated subquery with an unrecognized aggregate shape");
    }

    private static bool TryLowerCorrelatedExpression(
        CypherExpression expression,
        Dictionary<string, CypherExpression> callExpressions,
        PatternSubqueryExpression anchor,
        out CypherExpression aggregate)
    {
        switch (expression)
        {
            case PatternComprehensionExpression comprehension
                when PatternsEquivalent(comprehension.Pattern, anchor.Pattern):
                aggregate = Function(
                    "collect",
                    Conditional(comprehension.Predicate, comprehension.Projection));
                return true;

            case PatternSubqueryExpression { Kind: PatternSubqueryKind.Count } count
                when PatternsEquivalent(count.Pattern, anchor.Pattern):
                aggregate = Function(
                    "count",
                    Conditional(count.Predicate, CountTarget(count.Pattern)));
                return true;

            case VariableRef variable when callExpressions.TryGetValue(variable.Alias, out var callExpression):
                aggregate = callExpression;
                return true;

            default:
                // Pattern subqueries over other traversals (relationship degrees, complex-collection
                // sizes) stay behind for the sequential optional-match lowering that follows.
                aggregate = null!;
                return false;
        }
    }

    private static CypherStatement LowerPatternSubqueryProjections(
        CypherStatement input,
        string countAliasPrefix)
    {
        var terminalIndex = FindProjectionTerminalIndex(input.Clauses);
        if (terminalIndex < 0)
        {
            return input;
        }

        var extractor = new PatternSubqueryExtractor(countAliasPrefix);
        var output = new List<ICypherClause>();
        var scope = new List<string>();
        OrderByClause? entityResultOrdering = null;
        for (var index = 0; index < input.Clauses.Count; index++)
        {
            var clause = input.Clauses[index];
            switch (clause)
            {
                case WhereClause where:
                    {
                        var predicate = extractor.Rewrite(where.Predicate);
                        var drained = extractor.Drain();
                        if (drained.Length > 0)
                        {
                            if (index > 0 && input.Clauses[index - 1] is MatchClause { Optional: true })
                            {
                                throw Unsupported("a pattern subquery filter attached to an optional match");
                            }

                            EmitCountStages(output, scope, drained);
                            output.Add(new WhereClause(predicate));
                        }
                        else
                        {
                            output.Add(where);
                        }

                        break;
                    }

                case OrderByClause orderBy when index < terminalIndex:
                    {
                        // The explicit ordering still controls paging before entity hydration, while
                        // entity projections must also carry the ordering through their property-load
                        // pipeline. Keep the original expression so it can be lowered again at the
                        // projection boundary if no explicit result ordering is already attached.
                        var rewritten = RewriteOrdering(orderBy, extractor);
                        if (input.Clauses[terminalIndex] is EntityProjectionClause)
                        {
                            var counts = extractor.Drain();
                            if (counts.Length > 0)
                            {
                                entityResultOrdering = orderBy;
                                EmitCountStages(output, scope, counts);
                            }
                        }

                        output.Add(rewritten);
                        break;
                    }

                case ReturnClause @return when index == terminalIndex:
                    {
                        var items = @return.Items
                            .Select(item => new ReturnItem(extractor.Rewrite(item.Expression), item.Alias))
                            .ToArray();

                        // Trailing ORDER BY may reference the same counts; rewrite it now so the
                        // stages land before RETURN, where the aliases are still in scope.
                        var trailing = new ICypherClause[input.Clauses.Count - terminalIndex - 1];
                        for (var offset = 0; offset < trailing.Length; offset++)
                        {
                            trailing[offset] = input.Clauses[terminalIndex + 1 + offset] is OrderByClause trailingOrder
                                ? RewriteOrdering(trailingOrder, extractor)
                                : input.Clauses[terminalIndex + 1 + offset];
                        }

                        EmitCountStages(output, scope, extractor.Drain());
                        output.Add(new ReturnClause(items, @return.Distinct));
                        output.AddRange(trailing);
                        index = input.Clauses.Count - 1;
                        break;
                    }

                case EntityProjectionClause projection when index == terminalIndex:
                    {
                        var sourceOrdering = projection.Ordering.Count > 0
                            ? projection.Ordering
                            : entityResultOrdering?.Items ?? [];
                        var ordering = sourceOrdering
                            .Select(item => new OrderByItem(
                                extractor.Rewrite(item.Expression),
                                item.Descending))
                            .ToArray();
                        EmitCountStages(output, scope, extractor.Drain());
                        output.Add(new EntityProjectionClause(
                            projection.Shape,
                            projection.SourceAlias,
                            projection.RelationshipAlias,
                            projection.TargetAlias,
                            projection.LoadSourceProperties,
                            projection.LoadTargetProperties,
                            projection.IncludePathCoordinates,
                            ordering,
                            projection.RowIdentityAliases)
                        {
                            SourceComplexPropertyDepth = projection.SourceComplexPropertyDepth,
                            TargetComplexPropertyDepth = projection.TargetComplexPropertyDepth,
                        });
                        break;
                    }

                default:
                    output.Add(clause);
                    break;
            }

            UpdateScope(scope, clause);
        }

        return extractor.Created == 0
            ? input
            : new CypherStatement(output, input.Parameters, input.PathTypes);
    }

    private static int FindProjectionTerminalIndex(IReadOnlyList<ICypherClause> clauses)
    {
        for (var index = clauses.Count - 1; index >= 0; index--)
        {
            if (clauses[index] is ReturnClause or EntityProjectionClause)
            {
                return index;
            }
        }

        return -1;
    }

    private static OrderByClause RewriteOrdering(OrderByClause orderBy, PatternSubqueryExtractor extractor) =>
        new(orderBy.Items
            .Select(item => new OrderByItem(extractor.Rewrite(item.Expression), item.Descending))
            .ToArray());

    private static void EmitCountStages(
        List<ICypherClause> output,
        List<string> scope,
        IReadOnlyList<CountProjection> counts)
    {
        foreach (var count in counts)
        {
            var prepared = PrepareCountPattern(count.Pattern, count.Alias);
            output.Add(new MatchClause([prepared.Pattern], optional: true));

            var items = scope.Select(alias => new ReturnItem(new VariableRef(alias), null)).ToList();
            var countTarget = new VariableRef(prepared.RelationshipAlias);
            // A WHERE following AGE's OPTIONAL MATCH eliminates the preserved null row when the
            // optional pattern has no match. Put the subquery predicate inside the aggregate so a
            // failed predicate contributes NULL while the outer row and its zero count survive.
            items.Add(new ReturnItem(
                Function("count", Conditional(count.Predicate, countTarget)),
                count.Alias));
            output.Add(new WithClause(items, distinct: false));
            scope.Add(count.Alias);
        }
    }

    private static PreparedCountPattern PrepareCountPattern(PathPattern pattern, string countAlias)
    {
        var elements = new PatternElement[pattern.Elements.Count];
        string? relationshipAlias = null;
        var nodeIndex = 0;
        var relationshipIndex = 0;
        for (var index = 0; index < pattern.Elements.Count; index++)
        {
            elements[index] = pattern.Elements[index] switch
            {
                NodePattern node => new NodePattern(
                    node.Alias ?? $"{countAlias}_node{nodeIndex++}",
                    node.Labels),
                RelationshipPattern relationship => PrepareRelationship(relationship),
                _ => throw Unsupported("an unknown pattern element"),
            };
        }

        return new PreparedCountPattern(
            new PathPattern(elements, pattern.Alias),
            relationshipAlias ?? throw Unsupported("a pattern count without a relationship"));

        RelationshipPattern PrepareRelationship(RelationshipPattern relationship)
        {
            var alias = relationship.Alias ?? $"{countAlias}_relationship{relationshipIndex++}";
            relationshipAlias ??= alias;
            return new RelationshipPattern(
                alias,
                relationship.Direction,
                relationship.Depth,
                relationship.Types,
                relationship.IsComplexProperty);
        }
    }

    private static void UpdateScope(List<string> scope, ICypherClause clause)
    {
        switch (clause)
        {
            case MatchClause match:
                foreach (var pattern in match.Patterns)
                {
                    Add(pattern.Alias);
                    foreach (var element in pattern.Elements)
                    {
                        Add(element switch
                        {
                            NodePattern node => node.Alias,
                            RelationshipPattern relationship => relationship.Alias,
                            _ => null,
                        });
                    }
                }

                break;

            case WithClause { Wildcard: false } with:
                scope.Clear();
                foreach (var item in with.Items)
                {
                    Add(item.Alias ?? (item.Expression as VariableRef)?.Alias);
                }

                break;

            case UnwindClause unwind:
                Add(unwind.Alias);
                break;

            case CallClause call:
                foreach (var yield in call.Yields)
                {
                    Add(yield.Alias ?? yield.Name);
                }

                break;

            case FullTextSearchClause search:
                Add(search.Alias);
                break;
        }

        void Add(string? alias)
        {
            if (alias is not null && !scope.Contains(alias, StringComparer.Ordinal))
            {
                scope.Add(alias);
            }
        }
    }

    private static bool TryFindCorrelatedAnchor(
        IReadOnlyList<ICypherClause> clauses,
        out int whereIndex,
        out PatternSubqueryExpression anchor,
        out CypherExpression? remainder)
    {
        // The synthetic grouped-plan anchor is the last EXISTS conjunct of the first filter that has
        // one. A statement with CALL subqueries always carries that anchor; without CALLs an EXISTS
        // is an anchor only when a projected comprehension or count re-traverses the same pattern —
        // any other EXISTS is an ordinary existence filter, lowered to an optional-match count.
        var hasCalls = clauses.Any(clause => clause is CallSubqueryClause);
        Func<PatternSubqueryExpression, bool> isAnchor = hasCalls
            ? _ => true
            : candidate => HasCorrelatedProjection(clauses, candidate);
        for (var index = 0; index < clauses.Count; index++)
        {
            if (clauses[index] is WhereClause where &&
                TryRemoveExists(where.Predicate, isAnchor, out anchor, out remainder))
            {
                whereIndex = index;
                return true;
            }
        }

        whereIndex = -1;
        anchor = null!;
        remainder = null;
        return false;
    }

    private static bool HasCorrelatedProjection(
        IReadOnlyList<ICypherClause> clauses,
        PatternSubqueryExpression anchor) =>
        clauses.OfType<ReturnClause>().Any(@return => @return.Items.Any(item =>
            ContainsExpression(item.Expression, expression =>
                (expression is PatternComprehensionExpression comprehension &&
                    PatternsEquivalent(comprehension.Pattern, anchor.Pattern)) ||
                (expression is PatternSubqueryExpression
                {
                    Kind: PatternSubqueryKind.Count,
                    Pattern: var pattern,
                } && PatternsEquivalent(pattern, anchor.Pattern)))));

    private static bool ContainsExpression(
        CypherExpression expression,
        Func<CypherExpression, bool> predicate)
    {
        if (predicate(expression))
        {
            return true;
        }

        return expression switch
        {
            PropertyAccess property => ContainsExpression(property.Target, predicate),
            EscapedPropertyAccess property => ContainsExpression(property.Target, predicate),
            PhysicalPropertyAccess property => ContainsExpression(property.Target, predicate),
            CollectionPropertyAccess property => ContainsExpression(property.Target, predicate),
            CollectionContainsExpression contains =>
                ContainsExpression(contains.Collection, predicate) || ContainsExpression(contains.Item, predicate),
            FunctionCall function => function.Arguments.Any(item => ContainsExpression(item, predicate)),
            BinaryExpression binary =>
                ContainsExpression(binary.Left, predicate) || ContainsExpression(binary.Right, predicate),
            UnaryExpression unary => ContainsExpression(unary.Operand, predicate),
            LabelTest label => ContainsExpression(label.Target, predicate),
            ListExpression list => list.Items.Any(item => ContainsExpression(item, predicate)),
            MapExpression map => map.Entries.Any(entry => ContainsExpression(entry.Value, predicate)),
            IndexExpression index =>
                ContainsExpression(index.Target, predicate) || ContainsExpression(index.Index, predicate),
            CaseExpression @case =>
                ContainsExpression(@case.Condition, predicate) ||
                ContainsExpression(@case.WhenTrue, predicate) ||
                (@case.WhenFalse is not null && ContainsExpression(@case.WhenFalse, predicate)),
            ConjunctionExpression conjunction =>
                conjunction.Predicates.Any(item => ContainsExpression(item, predicate)),
            PatternSubqueryExpression subquery =>
                subquery.Predicate is not null && ContainsExpression(subquery.Predicate, predicate),
            PatternComprehensionExpression comprehension =>
                ContainsExpression(comprehension.Projection, predicate) ||
                (comprehension.Predicate is not null && ContainsExpression(comprehension.Predicate, predicate)),
            ListComprehensionExpression comprehension =>
                ContainsExpression(comprehension.Source, predicate) ||
                (comprehension.Predicate is not null && ContainsExpression(comprehension.Predicate, predicate)) ||
                (comprehension.Projection is not null && ContainsExpression(comprehension.Projection, predicate)),
            ReduceExpression reduce =>
                ContainsExpression(reduce.Seed, predicate) ||
                ContainsExpression(reduce.Source, predicate) ||
                ContainsExpression(reduce.Reducer, predicate),
            AllExpression all =>
                ContainsExpression(all.Source, predicate) || ContainsExpression(all.Predicate, predicate),
            _ => false,
        };
    }

    private static bool TryRemoveExists(
        CypherExpression expression,
        Func<PatternSubqueryExpression, bool> isAnchor,
        out PatternSubqueryExpression anchor,
        out CypherExpression? remainder)
    {
        if (expression is PatternSubqueryExpression { Kind: PatternSubqueryKind.Exists } exists &&
            isAnchor(exists))
        {
            anchor = exists;
            remainder = null;
            return true;
        }

        if (expression is ConjunctionExpression conjunction)
        {
            for (var index = conjunction.Predicates.Count - 1; index >= 0; index--)
            {
                if (!TryRemoveExists(conjunction.Predicates[index], isAnchor, out anchor, out var nestedRemainder))
                {
                    continue;
                }

                var predicates = conjunction.Predicates.Where((_, itemIndex) => itemIndex != index).ToList();
                if (nestedRemainder is not null)
                {
                    predicates.Insert(index, nestedRemainder);
                }

                remainder = predicates.Count switch
                {
                    0 => null,
                    1 => predicates[0],
                    _ => new ConjunctionExpression(predicates),
                };
                return true;
            }
        }

        anchor = null!;
        remainder = null;
        return false;
    }

    private static int FindReturnIndex(IReadOnlyList<ICypherClause> clauses, bool required = true)
    {
        for (var index = clauses.Count - 1; index >= 0; index--)
        {
            if (clauses[index] is ReturnClause)
            {
                return index;
            }
        }

        return required ? throw Unsupported("a statement without RETURN") : -1;
    }

    private static string SourceAlias(PathPattern pattern) =>
        (pattern.Elements[0] as NodePattern)?.Alias ?? throw Unsupported("a correlated pattern without a source alias");

    private static VariableRef CountTarget(PathPattern pattern) =>
        pattern.Elements.OfType<RelationshipPattern>().FirstOrDefault()?.Alias is { } alias
            ? new VariableRef(alias)
            : (pattern.Elements[^1] as NodePattern)?.Alias is { } nodeAlias
                ? new VariableRef(nodeAlias)
                : throw Unsupported("a correlated pattern without a countable alias");

    private static CypherExpression Conditional(CypherExpression? predicate, CypherExpression value) =>
        predicate is null ? value : new CaseExpression(predicate, value, new Literal(null));

    private static IReadOnlyList<CypherExpression> Conjuncts(CypherExpression? predicate) => predicate switch
    {
        null => [],
        ConjunctionExpression conjunction => conjunction.Predicates,
        _ => [predicate],
    };

    private static bool PatternsEquivalent(PathPattern left, PathPattern right)
    {
        if (!string.Equals(left.Alias, right.Alias, StringComparison.Ordinal) ||
            left.Elements.Count != right.Elements.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Elements.Count; index++)
        {
            var equivalent = (left.Elements[index], right.Elements[index]) switch
            {
                (NodePattern leftNode, NodePattern rightNode) =>
                    string.Equals(leftNode.Alias, rightNode.Alias, StringComparison.Ordinal) &&
                    leftNode.Labels.SequenceEqual(rightNode.Labels, StringComparer.Ordinal),
                (RelationshipPattern leftRelationship, RelationshipPattern rightRelationship) =>
                    string.Equals(leftRelationship.Alias, rightRelationship.Alias, StringComparison.Ordinal) &&
                    leftRelationship.Direction == rightRelationship.Direction &&
                    leftRelationship.Depth == rightRelationship.Depth &&
                    leftRelationship.IsComplexProperty == rightRelationship.IsComplexProperty &&
                    leftRelationship.Types.SequenceEqual(rightRelationship.Types, StringComparer.Ordinal),
                _ => false,
            };
            if (!equivalent)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsCorrelatedExpression(IEnumerable<CypherExpression> expressions, string allowedAlias) =>
        expressions.Any(expression => expression switch
        {
            PatternComprehensionExpression or PatternSubqueryExpression { Kind: PatternSubqueryKind.Count } => true,
            VariableRef variable => !string.Equals(variable.Alias, allowedAlias, StringComparison.Ordinal) &&
                variable.Alias.StartsWith("__group", StringComparison.Ordinal),
            _ => false,
        });

    private static void AppendTrailingClauses(
        List<ICypherClause> output,
        IReadOnlyList<ICypherClause> input,
        int returnIndex)
    {
        for (var index = returnIndex + 1; index < input.Count; index++)
        {
            output.Add(input[index]);
        }
    }

    private static FunctionCall Function(string name, params CypherExpression[] arguments) => new(name, arguments);

    private static GraphQueryTranslationException Unsupported(string shape) => new(
        $"Apache AGE cannot structurally lower {shape}.");

    private enum CorrelatedCallKind
    {
        Aggregate,
        OrderedCollection,
        NestedGrouping,
    }

    private sealed record CorrelatedCall(
        CorrelatedCallKind Kind,
        string Alias,
        CypherExpression Expression,
        CypherExpression? Predicate,
        WithClause? Carry = null,
        OrderByClause? OrderBy = null,
        WithClause? NestedKey = null,
        WithClause? NestedAggregates = null);

    private sealed record PreparedCountPattern(PathPattern Pattern, string RelationshipAlias);

    private sealed record CountProjection(int Ordinal, string Alias, PathPattern Pattern, CypherExpression? Predicate);

    /// <summary>
    /// Rewrites <c>COUNT { }</c> subqueries to count-stage variable references and <c>EXISTS { }</c>
    /// subqueries to <c>&gt; 0</c> comparisons over the same counts, recording each extracted count
    /// for optional-match stage emission.
    /// </summary>
    private sealed class PatternSubqueryExtractor
    {
        private readonly string countAliasPrefix;
        private readonly List<CountProjection> counts = [];
        private int drained;

        public PatternSubqueryExtractor(string countAliasPrefix)
        {
            this.countAliasPrefix = countAliasPrefix;
        }

        public int Created => counts.Count;

        /// <summary>Returns the counts extracted since the previous drain.</summary>
        /// <remarks>
        /// Deduplication is per drain segment: a count referenced again after its stage was emitted
        /// gets a fresh stage, because an earlier alias may have left the carried scope in between.
        /// </remarks>
        public CountProjection[] Drain()
        {
            var slice = counts.Skip(drained).ToArray();
            drained = counts.Count;
            return slice;
        }

        public CypherExpression Rewrite(CypherExpression expression)
        {
            switch (expression)
            {
                case PatternSubqueryExpression { Kind: PatternSubqueryKind.Count } count:
                    return new VariableRef(Projection(count.Pattern, count.Predicate).Alias);

                case PatternSubqueryExpression { Kind: PatternSubqueryKind.Exists } exists:
                    return new BinaryExpression(
                        CypherBinaryOperator.GreaterThan,
                        new VariableRef(Projection(exists.Pattern, exists.Predicate).Alias),
                        new Literal(0));
            }

            return expression switch
            {
                PropertyAccess property => new PropertyAccess(Rewrite(property.Target), property.Property),
                EscapedPropertyAccess property => new EscapedPropertyAccess(Rewrite(property.Target), property.Property),
                PhysicalPropertyAccess property => new PhysicalPropertyAccess(Rewrite(property.Target), property.Property),
                CollectionPropertyAccess property => new CollectionPropertyAccess(
                    Rewrite(property.Target),
                    property.Property,
                    property.Escape),
                CollectionContainsExpression contains => new CollectionContainsExpression(
                    Rewrite(contains.Collection),
                    Rewrite(contains.Item)),
                FunctionCall function => new FunctionCall(function.Name, function.Arguments.Select(Rewrite).ToArray()),
                BinaryExpression binary => new BinaryExpression(binary.Op, Rewrite(binary.Left), Rewrite(binary.Right)),
                UnaryExpression unary => new UnaryExpression(unary.Op, Rewrite(unary.Operand)),
                LabelTest label => new LabelTest(Rewrite(label.Target), label.Labels),
                ListExpression list => new ListExpression(list.Items.Select(Rewrite).ToArray()),
                MapExpression map => new MapExpression(map.Entries
                    .Select(entry => new MapEntry(entry.Key, Rewrite(entry.Value))).ToArray()),
                IndexExpression index => new IndexExpression(Rewrite(index.Target), Rewrite(index.Index)),
                CaseExpression @case => new CaseExpression(
                    Rewrite(@case.Condition),
                    Rewrite(@case.WhenTrue),
                    @case.WhenFalse is null ? null : Rewrite(@case.WhenFalse)),
                ConjunctionExpression conjunction => new ConjunctionExpression(
                    conjunction.Predicates.Select(Rewrite).ToArray()),
                ListComprehensionExpression comprehension => new ListComprehensionExpression(
                    Rewrite(comprehension.Source),
                    comprehension.IteratorAlias,
                    comprehension.Predicate is null ? null : Rewrite(comprehension.Predicate),
                    comprehension.Projection is null ? null : Rewrite(comprehension.Projection)),
                ReduceExpression reduce => new ReduceExpression(
                    reduce.AccumulatorAlias,
                    Rewrite(reduce.Seed),
                    reduce.IteratorAlias,
                    Rewrite(reduce.Source),
                    Rewrite(reduce.Reducer)),
                AllExpression all => new AllExpression(
                    all.IteratorAlias,
                    Rewrite(all.Source),
                    Rewrite(all.Predicate)),
                _ => expression,
            };
        }

        private CountProjection Projection(PathPattern pattern, CypherExpression? predicate)
        {
            for (var index = drained; index < counts.Count; index++)
            {
                if (counts[index].Predicate == predicate && PatternsEquivalent(counts[index].Pattern, pattern))
                {
                    return counts[index];
                }
            }

            var created = new CountProjection(counts.Count, $"{countAliasPrefix}{counts.Count}", pattern, predicate);
            counts.Add(created);
            return created;
        }
    }
}
