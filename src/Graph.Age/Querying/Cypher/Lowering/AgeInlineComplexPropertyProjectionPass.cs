// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Lowering;

using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using Cvoya.Graph.Cypher.Validation;

/// <summary>
/// Expands inline node projections into structured complex-property hydration clauses.
/// </summary>
internal sealed class AgeInlineComplexPropertyProjectionPass : ICypherPass
{
    /// <inheritdoc />
    public CypherStatement Run(CypherStatement input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var rewriter = new ClauseRewriter();
        var clauses = rewriter.RewriteClauses(input.Clauses);
        return rewriter.Changed
            ? new CypherStatement(clauses, input.Parameters, input.PathTypes)
            : input;
    }

    private static ReturnItem Variable(string alias) => new(new VariableRef(alias), null);

    private static FunctionCall Function(string name, params CypherExpression[] arguments) =>
        new(name, arguments);

    private static MapExpression NodeProjection(
        string alias,
        CypherExpression complexProperties) => new(
    [
        new MapEntry("Node", new VariableRef(alias)),
        new MapEntry("ComplexProperties", complexProperties),
    ]);

    private static IEnumerable<string> CarryCandidates()
    {
        yield return "pathIndex";
        yield return "hopIndex";
        yield return "src";
        yield return "r";
        yield return "tgt";
        for (var suffix = 2; suffix <= GraphDataModel.DefaultDepthAllowed; suffix++)
        {
            yield return $"r_{suffix}";
            yield return $"tgt_{suffix}";
        }
    }

    private sealed class ClauseRewriter
    {
        private readonly HashSet<string> scope = new(StringComparer.Ordinal);

        public ClauseRewriter(IReadOnlyList<string>? initialScope = null)
        {
            foreach (var alias in initialScope ?? [])
            {
                scope.Add(alias);
            }
        }

        public bool Changed { get; private set; }

        public ICypherClause[] RewriteClauses(IReadOnlyList<ICypherClause> clauses)
        {
            var rewritten = new List<ICypherClause>();
            for (var index = 0; index < clauses.Count; index++)
            {
                switch (clauses[index])
                {
                    case WithClause { Wildcard: false } with:
                        RewriteProjectionClause(
                            rewritten,
                            with,
                            with.Items,
                            trailingExpressions: [],
                            items => new WithClause(items, with.Distinct));
                        ProjectScope(with.Items);
                        break;

                    case ReturnClause @return:
                        RewriteProjectionClause(
                            rewritten,
                            @return,
                            @return.Items,
                            TrailingExpressions(clauses, index + 1),
                            items => new ReturnClause(items, @return.Distinct));
                        break;

                    case CallSubqueryClause subquery:
                        rewritten.Add(RewriteSubquery(subquery));
                        BindSubqueryOutputs(subquery);
                        break;

                    case var clause:
                        rewritten.Add(clause);
                        BindClauseAliases(clause);
                        break;
                }
            }

            return rewritten.ToArray();
        }

        private void RewriteProjectionClause(
            List<ICypherClause> clauses,
            ICypherClause original,
            IReadOnlyList<ReturnItem> items,
            IEnumerable<CypherExpression> trailingExpressions,
            Func<IReadOnlyList<ReturnItem>, ICypherClause> createClause)
        {
            var expressionRewriter = new ProjectionExpressionRewriter();
            var rewrittenItems = items
                .Select(item => new ReturnItem(
                    expressionRewriter.Rewrite(item.Expression),
                    item.Alias))
                .ToArray();
            if (!expressionRewriter.Changed)
            {
                clauses.Add(original);
                return;
            }

            Changed = true;
            if (expressionRewriter.HydrationAliases.Count > 0)
            {
                AppendHydrationClauses(
                    clauses,
                    expressionRewriter.HydrationAliases,
                    BuildCarryAliases(items, trailingExpressions));
            }

            clauses.Add(createClause(rewrittenItems));
        }

        private CallSubqueryClause RewriteSubquery(CallSubqueryClause subquery)
        {
            var nested = new ClauseRewriter(subquery.ImportedVariables);
            var body = nested.RewriteClauses(subquery.Body);
            if (!nested.Changed)
            {
                return subquery;
            }

            Changed = true;
            return new CallSubqueryClause(subquery.ImportedVariables, body);
        }

        private List<string> BuildCarryAliases(
            IReadOnlyList<ReturnItem> items,
            IEnumerable<CypherExpression> trailingExpressions)
        {
            var carry = new List<string>();
            foreach (var candidate in CarryCandidates())
            {
                if (scope.Contains(candidate))
                {
                    carry.Add(candidate);
                }
            }

            foreach (var alias in FreeAliasCollector.Collect(
                items.Select(item => item.Expression),
                scope))
            {
                if (!carry.Contains(alias, StringComparer.Ordinal))
                {
                    carry.Add(alias);
                }
            }

            // ORDER BY/SKIP/LIMIT after a RETURN are evaluated against the pre-RETURN scope,
            // so aliases they reference must survive the hydration pipes as well.
            foreach (var alias in FreeAliasCollector.Collect(trailingExpressions, scope))
            {
                if (scope.Contains(alias) && !carry.Contains(alias, StringComparer.Ordinal))
                {
                    carry.Add(alias);
                }
            }

            return carry;
        }

        // Scope bookkeeping mirrors CypherAstValidator so the carried aliases are exactly the
        // variables bound at the hydration insertion point.
        private void BindClauseAliases(ICypherClause clause)
        {
            switch (clause)
            {
                case MatchClause match:
                    foreach (var pattern in match.Patterns)
                    {
                        Bind(pattern.Alias);
                        foreach (var element in pattern.Elements)
                        {
                            Bind(element switch
                            {
                                NodePattern node => node.Alias,
                                RelationshipPattern relationship => relationship.Alias,
                                _ => null,
                            });
                        }
                    }

                    break;

                case UnwindClause unwind:
                    Bind(unwind.Alias);
                    break;

                case CallClause call:
                    foreach (var yield in call.Yields)
                    {
                        Bind(yield.Alias ?? yield.Name);
                    }

                    break;

                case FullTextSearchClause search:
                    Bind(search.Alias);
                    if (search.Target == Cvoya.Graph.Querying.SearchRootTarget.Relationships)
                    {
                        Bind("src");
                        Bind("tgt");
                    }

                    break;
            }
        }

        private void BindSubqueryOutputs(CallSubqueryClause subquery)
        {
            if (subquery.Body[^1] is not ReturnClause @return)
            {
                return;
            }

            foreach (var item in @return.Items)
            {
                Bind(item.Alias ?? (item.Expression as VariableRef)?.Alias);
            }
        }

        private void ProjectScope(IReadOnlyList<ReturnItem> items)
        {
            scope.Clear();
            foreach (var item in items)
            {
                Bind(item.Alias ?? (item.Expression as VariableRef)?.Alias);
            }
        }

        private void Bind(string? alias)
        {
            if (alias is not null)
            {
                scope.Add(alias);
            }
        }

        private static IEnumerable<CypherExpression> TrailingExpressions(
            IReadOnlyList<ICypherClause> clauses,
            int start)
        {
            for (var index = start; index < clauses.Count; index++)
            {
                switch (clauses[index])
                {
                    case OrderByClause orderBy:
                        foreach (var item in orderBy.Items)
                        {
                            yield return item.Expression;
                        }

                        break;

                    case SkipClause skip:
                        yield return skip.Count;
                        break;

                    case LimitClause limit:
                        yield return limit.Count;
                        break;
                }
            }
        }

        private static void AppendHydrationClauses(
            List<ICypherClause> clauses,
            IReadOnlyList<(string Alias, int Depth)> hydrationAliases,
            IReadOnlyList<string> baseCarry)
        {
            var carry = baseCarry.ToList();
            foreach (var (alias, depth) in hydrationAliases)
            {
                var relationshipsAlias = $"{alias}_inline_relationships";
                var propertyAlias = $"{alias}_inline_property";
                var pathAlias = $"{alias}_inline_path";
                var propertiesAlias = $"{alias}_inline_properties";
                var maximumDepth = depth > 0 ? depth : GraphDataModel.DefaultDepthAllowed;
                var singleHop = maximumDepth == 1;

                clauses.Add(new MatchClause(
                [
                    new PathPattern(
                    [
                        new NodePattern(alias, []),
                        new RelationshipPattern(
                            relationshipsAlias,
                            CypherDirection.Outgoing,
                            singleHop ? null : new DepthRange(1, maximumDepth),
                            types: []),
                        new NodePattern(propertyAlias, []),
                    ]),
                ], optional: true));
                var marker = new PropertyAccess(
                    new VariableRef(relationshipsAlias),
                    AgeDialect.Instance.ComplexPropertyRelationshipMarker);
                clauses.Add(new WhereClause(singleHop
                    ? new BinaryExpression(
                        CypherBinaryOperator.Equal,
                        marker,
                        new Literal(true))
                    : new AllExpression(
                        "propertyRelationship",
                        new VariableRef(relationshipsAlias),
                        new BinaryExpression(
                            CypherBinaryOperator.Equal,
                            new PropertyAccess(
                                new VariableRef("propertyRelationship"),
                                AgeDialect.Instance.ComplexPropertyRelationshipMarker),
                            new Literal(true)))));
                clauses.Add(new WithClause(
                    carry.Select(Variable)
                        .Append(new ReturnItem(
                            PropertyPath(alias, relationshipsAlias, singleHop),
                            pathAlias))
                        .ToArray(),
                    distinct: false));
                clauses.Add(new WithClause(
                    carry.Select(Variable)
                        .Append(new ReturnItem(
                            Function("collect", new VariableRef(pathAlias)),
                            propertiesAlias))
                        .ToArray(),
                    distinct: false));
                carry.Add(propertiesAlias);
            }
        }

        private static CaseExpression PropertyPath(
            string ownerAlias,
            string relationshipsAlias,
            bool singleHop)
        {
            var relationships = new VariableRef(relationshipsAlias);
            if (singleHop)
            {
                // A depth-1 type uses a direct relationship instead of a variable-length
                // pattern; AGE plans the direct join against the bound owner rather than a VLE.
                return new CaseExpression(
                    new UnaryExpression(CypherUnaryOperator.IsNull, relationships),
                    new ListExpression([]),
                    new ListExpression(
                    [
                        new MapExpression(
                        [
                            new MapEntry("ParentNode", new VariableRef(ownerAlias)),
                            new MapEntry("Relationship", relationships),
                            new MapEntry(
                                "SequenceNumber",
                                new PropertyAccess(relationships, "SequenceNumber")),
                            new MapEntry("Property", Function("endNode", relationships)),
                        ]),
                    ]));
            }

            var index = new VariableRef("i");
            var relationship = new IndexExpression(relationships, index);
            return new CaseExpression(
                new UnaryExpression(
                    CypherUnaryOperator.IsNull,
                    relationships),
                new ListExpression([]),
                new ListComprehensionExpression(
                    Function(
                        "range",
                        new Literal(0),
                        new BinaryExpression(
                            CypherBinaryOperator.Subtract,
                            Function("size", relationships),
                            new Literal(1))),
                    "i",
                    projection: new MapExpression(
                    [
                        new MapEntry("ParentNode", Function("startNode", relationship)),
                        new MapEntry("Relationship", relationship),
                        new MapEntry(
                            "SequenceNumber",
                            new PropertyAccess(relationship, "SequenceNumber")),
                        new MapEntry("Property", Function("endNode", relationship)),
                    ])));
        }
    }

    private sealed class ProjectionExpressionRewriter
    {
        private readonly List<(string Alias, int Depth)> hydrationAliases = [];

        public bool Changed { get; private set; }

        public List<(string Alias, int Depth)> HydrationAliases => hydrationAliases;

        public CypherExpression Rewrite(CypherExpression expression)
        {
            if (expression is EntityProjectionExpression entity)
            {
                Changed = true;
                if (entity.LoadComplexProperties &&
                    !hydrationAliases.Exists(candidate =>
                        string.Equals(candidate.Alias, entity.Alias, StringComparison.Ordinal)))
                {
                    hydrationAliases.Add((entity.Alias, entity.ComplexPropertyDepth));
                }

                return NodeProjection(
                    entity.Alias,
                    entity.LoadComplexProperties
                        ? new VariableRef($"{entity.Alias}_inline_properties")
                        : new ListExpression([]));
            }

            return expression switch
            {
                BinaryExpression binary => new BinaryExpression(
                    binary.Op,
                    Rewrite(binary.Left),
                    Rewrite(binary.Right)),
                UnaryExpression unary => new UnaryExpression(unary.Op, Rewrite(unary.Operand)),
                PropertyAccess property => new PropertyAccess(Rewrite(property.Target), property.Property),
                EscapedPropertyAccess property => new EscapedPropertyAccess(
                    Rewrite(property.Target),
                    property.Property),
                PhysicalPropertyAccess property => new PhysicalPropertyAccess(Rewrite(property.Target), property.Property),
                CollectionPropertyAccess property => new CollectionPropertyAccess(
                    Rewrite(property.Target),
                    property.Property,
                    property.Escape),
                CollectionContainsExpression contains => new CollectionContainsExpression(
                    Rewrite(contains.Collection),
                    Rewrite(contains.Item)),
                FunctionCall function => new FunctionCall(
                    function.Name,
                    function.Arguments.Select(Rewrite).ToArray()),
                LabelTest label => new LabelTest(Rewrite(label.Target), label.Labels),
                ListExpression list => new ListExpression(list.Items.Select(Rewrite).ToArray()),
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
                MapExpression map => new MapExpression(map.Entries
                    .Select(entry => new MapEntry(entry.Key, Rewrite(entry.Value)))
                    .ToArray()),
                IndexExpression index => new IndexExpression(
                    Rewrite(index.Target),
                    Rewrite(index.Index)),
                CaseExpression @case => new CaseExpression(
                    Rewrite(@case.Condition),
                    Rewrite(@case.WhenTrue),
                    @case.WhenFalse is null ? null : Rewrite(@case.WhenFalse)),
                ConjunctionExpression conjunction => new ConjunctionExpression(
                    conjunction.Predicates.Select(Rewrite).ToArray()),
                PatternSubqueryExpression subquery => new PatternSubqueryExpression(
                    subquery.Kind,
                    subquery.Pattern,
                    subquery.Predicate is null ? null : Rewrite(subquery.Predicate)),
                PatternComprehensionExpression comprehension => new PatternComprehensionExpression(
                    comprehension.Pattern,
                    Rewrite(comprehension.Projection),
                    comprehension.Predicate is null ? null : Rewrite(comprehension.Predicate)),
                _ => expression,
            };
        }
    }

    private sealed class FreeAliasCollector(IReadOnlySet<string> scope)
    {
        private readonly List<string> aliases = [];
        private readonly HashSet<string> bound = new(StringComparer.Ordinal);

        public static List<string> Collect(
            IEnumerable<CypherExpression> expressions,
            IReadOnlySet<string> scope)
        {
            var collector = new FreeAliasCollector(scope);
            foreach (var expression in expressions)
            {
                collector.Visit(expression);
            }

            return collector.aliases;
        }

        private void Visit(CypherExpression expression)
        {
            switch (expression)
            {
                case VariableRef variable:
                    Add(variable.Alias);
                    break;

                case EntityProjectionExpression entity:
                    Add(entity.Alias);
                    break;

                case BinaryExpression binary:
                    Visit(binary.Left);
                    Visit(binary.Right);
                    break;

                case UnaryExpression unary:
                    Visit(unary.Operand);
                    break;

                case PropertyAccess property:
                    Visit(property.Target);
                    break;

                case EscapedPropertyAccess property:
                    Visit(property.Target);
                    break;

                case PhysicalPropertyAccess property:
                    Visit(property.Target);
                    break;

                case CollectionPropertyAccess property:
                    Visit(property.Target);
                    break;

                case CollectionContainsExpression contains:
                    Visit(contains.Collection);
                    Visit(contains.Item);
                    break;

                case FunctionCall function:
                    VisitAll(function.Arguments);
                    break;

                case LabelTest label:
                    Visit(label.Target);
                    break;

                case ListExpression list:
                    VisitAll(list.Items);
                    break;

                case ListComprehensionExpression comprehension:
                    Visit(comprehension.Source);
                    VisitBound(
                        [comprehension.IteratorAlias],
                        comprehension.Predicate,
                        comprehension.Projection);
                    break;

                case ReduceExpression reduce:
                    Visit(reduce.Seed);
                    Visit(reduce.Source);
                    VisitBound(
                        [reduce.AccumulatorAlias, reduce.IteratorAlias],
                        reduce.Reducer);
                    break;

                case AllExpression all:
                    Visit(all.Source);
                    VisitBound([all.IteratorAlias], all.Predicate);
                    break;

                case MapExpression map:
                    VisitAll(map.Entries.Select(entry => entry.Value));
                    break;

                case IndexExpression index:
                    Visit(index.Target);
                    Visit(index.Index);
                    break;

                case CaseExpression @case:
                    Visit(@case.Condition);
                    Visit(@case.WhenTrue);
                    if (@case.WhenFalse is not null)
                    {
                        Visit(@case.WhenFalse);
                    }

                    break;

                case ConjunctionExpression conjunction:
                    VisitAll(conjunction.Predicates);
                    break;

                case PatternSubqueryExpression subquery:
                    VisitPatternBound(subquery.Pattern, subquery.Predicate);
                    break;

                case PatternComprehensionExpression comprehension:
                    VisitPatternBound(
                        comprehension.Pattern,
                        comprehension.Predicate,
                        comprehension.Projection);
                    break;
            }
        }

        private void VisitAll(IEnumerable<CypherExpression> expressions)
        {
            foreach (var expression in expressions)
            {
                Visit(expression);
            }
        }

        private void VisitPatternBound(PathPattern pattern, params CypherExpression?[] expressions)
        {
            var patternAliases = pattern.Elements.Select(element => element switch
            {
                NodePattern node => node.Alias,
                RelationshipPattern relationship => relationship.Alias,
                _ => null,
            }).Append(pattern.Alias).OfType<string>().ToArray();

            // A pattern alias that is already bound in the enclosing scope references that
            // variable; only the remaining aliases introduce local bindings.
            foreach (var alias in patternAliases)
            {
                if (scope.Contains(alias))
                {
                    Add(alias);
                }
            }

            VisitBound(
                patternAliases.Where(alias => !scope.Contains(alias)).ToArray(),
                expressions);
        }

        private void VisitBound(IReadOnlyList<string> aliasesToBind, params CypherExpression?[] expressions)
        {
            var added = aliasesToBind.Where(bound.Add).ToArray();
            foreach (var expression in expressions)
            {
                if (expression is not null)
                {
                    Visit(expression);
                }
            }

            foreach (var alias in added)
            {
                bound.Remove(alias);
            }
        }

        private void Add(string alias)
        {
            if (!bound.Contains(alias) && !aliases.Contains(alias, StringComparer.Ordinal))
            {
                aliases.Add(alias);
            }
        }
    }
}
