// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Lowering;

using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using Cvoya.Graph.Cypher.Validation;

/// <summary>
/// Expands entity projections and lowers their expressions into AGE-compatible structured Cypher.
/// </summary>
internal sealed class AgeEntityProjectionPass : ICypherPass
{
    private const string ProjectionOrderPrefix = "__projectionOrder";

    /// <inheritdoc />
    public CypherStatement Run(CypherStatement input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var expander = new ProjectionExpander();
        var expandedClauses = expander.ExpandClauses(input.Clauses);
        var rewriter = new EntityProjectionRewriter();
        var rewrittenClauses = rewriter.RewriteClauses(expandedClauses);
        return expander.Changed || rewriter.Changed
            ? new CypherStatement(rewrittenClauses, input.Parameters, input.PathTypes)
            : input;
    }

    internal static string NormalizeProjectionColumn(string column)
    {
        if (column.Length > 2 && column[0] == '`' && column[^1] == '`' &&
            IsReservedAlias(column[1..^1]))
        {
            return column[1..^1];
        }

        return column;
    }

    private static bool IsReservedAlias(string alias) =>
        string.Equals(alias, "exists", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(alias, "contains", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(alias, "count", StringComparison.OrdinalIgnoreCase);

    private static string EscapeReservedAlias(string alias) =>
        IsReservedAlias(alias) ? $"`{alias}`" : alias;

    private static ReturnItem Variable(string alias) => new(new VariableRef(alias), null);

    private static FunctionCall Function(string name, params CypherExpression[] arguments) =>
        new(name, arguments);

    private sealed class ProjectionExpander
    {
        public bool Changed { get; private set; }

        public ICypherClause[] ExpandClauses(IReadOnlyList<ICypherClause> clauses)
        {
            var expanded = new List<ICypherClause>();
            foreach (var clause in clauses)
            {
                switch (clause)
                {
                    case EntityProjectionClause projection:
                        expanded.AddRange(ExpandProjection(projection));
                        Changed = true;
                        break;

                    case CallSubqueryClause subquery:
                        expanded.Add(new CallSubqueryClause(
                            subquery.ImportedVariables,
                            ExpandClauses(subquery.Body)));
                        break;

                    default:
                        expanded.Add(clause);
                        break;
                }
            }

            return expanded.ToArray();
        }

        private static List<ICypherClause> ExpandProjection(EntityProjectionClause projection)
        {
            var clauses = new List<ICypherClause>();
            AppendOrderingCapture(clauses, projection);

            if (projection.Shape == EntityProjectionShape.Node)
            {
                if (projection.LoadSourceProperties)
                {
                    AppendNodePropertyLoad(clauses, projection);
                }

                AppendProjectionResult(
                    clauses,
                    projection.Ordering,
                    [new ReturnItem(NodeProjection(projection.SourceAlias, "src_properties", projection.LoadSourceProperties), "Node")],
                    ["Node"]);
                return clauses;
            }

            if (projection.Shape == EntityProjectionShape.Relationship)
            {
                AppendProjectionResult(
                    clauses,
                    projection.Ordering,
                    [new ReturnItem(new VariableRef(projection.SourceAlias), "Relationship")],
                    ["Relationship"]);
                return clauses;
            }

            if (projection.IncludePathCoordinates)
            {
                AppendProjectionResult(
                    clauses,
                    projection.Ordering,
                    [
                        Variable("pathIndex"),
                        Variable("hopIndex"),
                        new ReturnItem(
                            PathSegmentProjection(
                                projection.SourceAlias,
                                projection.RelationshipAlias!,
                                projection.TargetAlias!,
                                new ListExpression([]),
                                new ListExpression([])),
                            "PathSegment"),
                    ],
                    ["pathIndex", "hopIndex", "PathSegment"]);
                return clauses;
            }

            if (projection.LoadSourceProperties || projection.LoadTargetProperties)
            {
                AppendPathPropertyLoads(clauses, projection);
            }

            AppendProjectionResult(
                clauses,
                projection.Ordering,
                [new ReturnItem(
                    PathSegmentProjection(
                        projection.SourceAlias,
                        projection.RelationshipAlias!,
                        projection.TargetAlias!,
                        projection.LoadSourceProperties
                            ? new VariableRef("src_properties")
                            : new ListExpression([]),
                        projection.LoadTargetProperties
                            ? new VariableRef("tgt_properties")
                            : new ListExpression([])),
                    "PathSegment")],
                ["PathSegment"]);
            return clauses;
        }

        private static void AppendOrderingCapture(
            List<ICypherClause> clauses,
            EntityProjectionClause projection)
        {
            if (projection.Ordering.Count == 0)
            {
                return;
            }

            var items = ProjectionInputAliases(projection).Select(Variable).ToList();
            for (var index = 0; index < projection.Ordering.Count; index++)
            {
                items.Add(new ReturnItem(
                    projection.Ordering[index].Expression,
                    ProjectionOrderAlias(index)));
            }

            clauses.Add(new WithClause(items, distinct: false));
        }

        private static IEnumerable<string> ProjectionInputAliases(EntityProjectionClause projection)
        {
            yield return projection.SourceAlias;
            if (projection.Shape == EntityProjectionShape.PathSegment)
            {
                yield return projection.RelationshipAlias!;
                yield return projection.TargetAlias!;
            }

            foreach (var rowIdentityAlias in projection.RowIdentityAliases)
            {
                yield return rowIdentityAlias;
            }

            if (projection.IncludePathCoordinates)
            {
                yield return "pathIndex";
                yield return "hopIndex";
            }
        }

        private static void AppendNodePropertyLoad(
            List<ICypherClause> clauses,
            EntityProjectionClause projection)
        {
            IReadOnlyList<string> aliases = [projection.SourceAlias, .. projection.RowIdentityAliases];
            var sourceSingleHop = AppendPropertyMatch(
                clauses,
                projection.SourceAlias,
                "src",
                "rels",
                "prop",
                projection.SourceComplexPropertyDepth);
            clauses.Add(new WithClause(
                CarryItems(aliases, projection.Ordering)
                    .Append(new ReturnItem(
                        PropertyPath("src_path", projection.SourceAlias, "rels", sourceSingleHop),
                        "src_property_path"))
                    .ToArray(),
                distinct: false));
            clauses.Add(new WithClause(
                CarryItems(aliases, projection.Ordering)
                    .Append(new ReturnItem(CollectedPropertyPaths("src_property_path"), "src_properties"))
                    .ToArray(),
                distinct: false));
        }

        private static void AppendPathPropertyLoads(
            List<ICypherClause> clauses,
            EntityProjectionClause projection)
        {
            var aliases = new[]
            {
                projection.SourceAlias,
                projection.RelationshipAlias!,
                projection.TargetAlias!,
            };

            if (projection.LoadSourceProperties)
            {
                var sourceSingleHop = AppendPropertyMatch(
                    clauses,
                    projection.SourceAlias,
                    "src",
                    "rels",
                    "prop",
                    projection.SourceComplexPropertyDepth);
                clauses.Add(new WithClause(
                    CarryItems(aliases, projection.Ordering)
                        .Append(new ReturnItem(
                            PropertyPath("src_path", projection.SourceAlias, "rels", sourceSingleHop),
                            "src_property_path"))
                        .ToArray(),
                    distinct: false));
                clauses.Add(new WithClause(
                    CarryItems(aliases, projection.Ordering)
                        .Append(new ReturnItem(CollectedPropertyPaths("src_property_path"), "src_properties"))
                        .ToArray(),
                    distinct: false));
            }
            else
            {
                clauses.Add(new WithClause(
                    CarryItems(aliases, projection.Ordering)
                        .Append(new ReturnItem(new ListExpression([]), "src_properties"))
                        .ToArray(),
                    distinct: false));
            }

            if (projection.LoadTargetProperties)
            {
                var targetSingleHop = AppendPropertyMatch(
                    clauses,
                    projection.TargetAlias!,
                    "tgt",
                    "trels",
                    "tprop",
                    projection.TargetComplexPropertyDepth);
                clauses.Add(new WithClause(
                    CarryItems(aliases, projection.Ordering)
                        .Append(Variable("src_properties"))
                        .Append(new ReturnItem(
                            PropertyPath("tgt_path", projection.TargetAlias!, "trels", targetSingleHop),
                            "tgt_property_path"))
                        .ToArray(),
                    distinct: false));
                clauses.Add(new WithClause(
                    CarryItems(aliases, projection.Ordering)
                        .Append(Variable("src_properties"))
                        .Append(new ReturnItem(CollectedPropertyPaths("tgt_property_path"), "tgt_properties"))
                        .ToArray(),
                    distinct: false));
            }
            else
            {
                clauses.Add(new WithClause(
                    CarryItems(aliases, projection.Ordering)
                        .Append(Variable("src_properties"))
                        .Append(new ReturnItem(new ListExpression([]), "tgt_properties"))
                        .ToArray(),
                    distinct: false));
            }
        }

        private static bool AppendPropertyMatch(
            List<ICypherClause> clauses,
            string ownerAlias,
            string prefix,
            string relationshipsAlias,
            string propertyAlias,
            int complexPropertyDepth)
        {
            var maximumDepth = complexPropertyDepth > 0
                ? complexPropertyDepth
                : GraphDataModel.DefaultDepthAllowed;
            var singleHop = maximumDepth == 1;
            clauses.Add(new MatchClause(
            [
                new PathPattern(
                [
                    new NodePattern(ownerAlias, []),
                    new RelationshipPattern(
                        relationshipsAlias,
                        CypherDirection.Outgoing,
                        singleHop ? null : new DepthRange(1, maximumDepth),
                        types: []),
                    new NodePattern(propertyAlias, []),
                ], singleHop ? null : $"{prefix}_path"),
            ], optional: true));
            var marker = new PropertyAccess(
                new VariableRef(relationshipsAlias),
                AgeDialect.Instance.ComplexPropertyRelationshipMarker);
            clauses.Add(new WhereClause(singleHop
                ? new BinaryExpression(CypherBinaryOperator.Equal, marker, new Literal(true))
                : new AllExpression(
                    "rel",
                    new VariableRef(relationshipsAlias),
                    new BinaryExpression(
                        CypherBinaryOperator.Equal,
                        new PropertyAccess(
                            new VariableRef("rel"),
                            AgeDialect.Instance.ComplexPropertyRelationshipMarker),
                        new Literal(true)))));
            return singleHop;
        }

        private static CaseExpression PropertyPath(
            string pathAlias,
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
            var pathNodes = Function("nodes", new VariableRef(pathAlias));
            return new CaseExpression(
                new UnaryExpression(CypherUnaryOperator.IsNull, new VariableRef(pathAlias)),
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
                        new MapEntry(
                            "ParentNode",
                            new CaseExpression(
                                new BinaryExpression(
                                    CypherBinaryOperator.Equal,
                                    index,
                                    new Literal(0)),
                                new VariableRef(ownerAlias),
                                new IndexExpression(pathNodes, index))),
                        new MapEntry("Relationship", relationship),
                        new MapEntry("SequenceNumber", new PropertyAccess(relationship, "SequenceNumber")),
                        new MapEntry(
                            "Property",
                            new IndexExpression(
                                pathNodes,
                                new BinaryExpression(
                                    CypherBinaryOperator.Add,
                                    index,
                                    new Literal(1)))),
                    ])));
        }

        private static ReduceExpression CollectedPropertyPaths(string propertyPathAlias) => new(
            "flat",
            new ListExpression([]),
            "path",
            Function("collect", new VariableRef(propertyPathAlias)),
            new BinaryExpression(
                CypherBinaryOperator.Add,
                new VariableRef("flat"),
                new VariableRef("path")));

        private static IEnumerable<ReturnItem> CarryItems(
            IReadOnlyList<string> aliases,
            IReadOnlyList<OrderByItem> ordering) =>
            aliases.Select(Variable).Concat(ProjectionOrderItems(ordering));

        private static IEnumerable<ReturnItem> ProjectionOrderItems(IReadOnlyList<OrderByItem> ordering) =>
            Enumerable.Range(0, ordering.Count).Select(index => Variable(ProjectionOrderAlias(index)));

        private static void AppendProjectionResult(
            List<ICypherClause> clauses,
            IReadOnlyList<OrderByItem> ordering,
            IReadOnlyList<ReturnItem> resultItems,
            IReadOnlyList<string> resultColumns)
        {
            if (ordering.Count == 0)
            {
                clauses.Add(new ReturnClause(resultItems, distinct: false));
                return;
            }

            clauses.Add(new WithClause(
                resultItems.Concat(ProjectionOrderItems(ordering)).ToArray(),
                distinct: false));
            clauses.Add(new OrderByClause(ordering
                .Select((item, index) => new OrderByItem(
                    new VariableRef(ProjectionOrderAlias(index)),
                    item.Descending))
                .ToArray()));
            clauses.Add(new ReturnClause(resultColumns.Select(Variable).ToArray(), distinct: false));
        }

        private static MapExpression NodeProjection(
            string nodeAlias,
            string propertiesAlias,
            bool loadProperties) => new(
            [
                new MapEntry("Node", new VariableRef(nodeAlias)),
                new MapEntry(
                    "ComplexProperties",
                    loadProperties ? new VariableRef(propertiesAlias) : new ListExpression([])),
            ]);

        private static MapExpression PathSegmentProjection(
            string sourceAlias,
            string relationshipAlias,
            string targetAlias,
            CypherExpression sourceProperties,
            CypherExpression targetProperties) => new(
            [
                new MapEntry("StartNode", new MapExpression(
                [
                    new MapEntry("Node", new VariableRef(sourceAlias)),
                    new MapEntry("ComplexProperties", sourceProperties),
                ])),
                new MapEntry("Relationship", new VariableRef(relationshipAlias)),
                new MapEntry("EndNode", new MapExpression(
                [
                    new MapEntry("Node", new VariableRef(targetAlias)),
                    new MapEntry("ComplexProperties", targetProperties),
                ])),
            ]);

        private static string ProjectionOrderAlias(int index) => $"{ProjectionOrderPrefix}{index}";
    }

    private sealed class EntityProjectionRewriter
    {
        private readonly Dictionary<string, string> optionalPathRelationships = new(StringComparer.Ordinal);

        public bool Changed { get; private set; }

        public ICypherClause[] RewriteClauses(IReadOnlyList<ICypherClause> clauses) =>
            clauses.Select(RewriteClause).ToArray();

        private ICypherClause RewriteClause(ICypherClause clause) => clause switch
        {
            MatchClause match => RewriteMatch(match),
            WhereClause where => new WhereClause(Rewrite(where.Predicate)),
            WithClause { Wildcard: true } => WithClause.All,
            WithClause with => new WithClause(RewriteItems(with.Items), with.Distinct),
            ReturnClause @return => new ReturnClause(RewriteItems(@return.Items), @return.Distinct),
            CallSubqueryClause subquery => RewriteSubquery(subquery),
            CallClause call => CallClause.WithAliasedYields(
                call.Procedure,
                call.Arguments.Select(Rewrite).ToArray(),
                call.Yields.Select(yield => new CallYield(
                    yield.Name,
                    yield.Alias is null ? null : RewriteAlias(yield.Alias))).ToArray()),
            UnwindClause unwind => new UnwindClause(Rewrite(unwind.Source), RewriteAlias(unwind.Alias)),
            OrderByClause orderBy => new OrderByClause(orderBy.Items
                .Select(item => new OrderByItem(Rewrite(item.Expression), item.Descending))
                .ToArray()),
            SkipClause skip => new SkipClause(Rewrite(skip.Count)),
            LimitClause limit => new LimitClause(Rewrite(limit.Count)),
            _ => clause,
        };

        private MatchClause RewriteMatch(MatchClause match)
        {
            var patterns = match.Patterns.Select(pattern =>
            {
                if (!match.Optional || pattern.Alias is null ||
                    !TryGetVariableLengthRelationshipAlias(pattern, out var relationshipsAlias))
                {
                    return pattern;
                }

                optionalPathRelationships[pattern.Alias] = relationshipsAlias;
                Changed = true;
                return new PathPattern(pattern.Elements, alias: null);
            }).ToArray();
            return new MatchClause(patterns, match.Optional);
        }

        private CallSubqueryClause RewriteSubquery(CallSubqueryClause subquery)
        {
            var nested = new EntityProjectionRewriter();
            var body = nested.RewriteClauses(subquery.Body);
            Changed |= nested.Changed;
            return new CallSubqueryClause(subquery.ImportedVariables, body);
        }

        private ReturnItem[] RewriteItems(IReadOnlyList<ReturnItem> items) => items
            .Select(item => new ReturnItem(
                Rewrite(item.Expression),
                item.Alias is null ? null : RewriteAlias(item.Alias)))
            .ToArray();

        private string RewriteAlias(string alias)
        {
            var rewritten = EscapeReservedAlias(alias);
            Changed |= !string.Equals(rewritten, alias, StringComparison.Ordinal);
            return rewritten;
        }

        private CypherExpression Rewrite(CypherExpression expression)
        {
            if (TryRewriteOptionalPathNullTest(expression, out var nullTest) ||
                TryRewriteOptionalPathNodeAccess(expression, out nullTest) ||
                TryRewriteMarkerAll(expression, out nullTest) ||
                TryRewriteCollectedPathReduction(expression, out nullTest) ||
                TryRewriteTemporalMember(expression, out nullTest) ||
                TryRewriteStringContains(expression, out nullTest))
            {
                Changed = true;
                return nullTest;
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
                FunctionCall function => RewriteFunction(function),
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
                IndexExpression index => RewriteIndex(index),
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

        private FunctionCall RewriteFunction(FunctionCall function)
        {
            var arguments = function.Arguments.Select(Rewrite).ToArray();
            if (!string.Equals(function.Name, "sum", StringComparison.OrdinalIgnoreCase))
            {
                return new FunctionCall(function.Name, arguments);
            }

            Changed = true;
            return Function(
                "coalesce",
                new FunctionCall(function.Name, arguments),
                new Literal(0));
        }

        private IndexExpression RewriteIndex(IndexExpression index)
        {
            var target = Rewrite(index.Target);
            var rewrittenIndex = Rewrite(index.Index);
            if (!IsPathIndex(index.Index))
            {
                return new IndexExpression(target, rewrittenIndex);
            }

            Changed = true;
            return new IndexExpression(target, Function("toInteger", rewrittenIndex));
        }

        private bool TryRewriteOptionalPathNullTest(
            CypherExpression expression,
            out CypherExpression rewritten)
        {
            rewritten = null!;
            if (expression is not UnaryExpression
                {
                    Op: CypherUnaryOperator.IsNull,
                    Operand: VariableRef path,
                } || !optionalPathRelationships.TryGetValue(path.Alias, out var relationshipsAlias))
            {
                return false;
            }

            rewritten = new UnaryExpression(
                CypherUnaryOperator.IsNull,
                new VariableRef(relationshipsAlias));
            return true;
        }

        private bool TryRewriteOptionalPathNodeAccess(
            CypherExpression expression,
            out CypherExpression rewritten)
        {
            rewritten = null!;
            if (expression is not IndexExpression
                {
                    Target: FunctionCall
                    {
                        Name: var functionName,
                        Arguments: [VariableRef path],
                    },
                    Index: var index,
                } || !string.Equals(functionName, "nodes", StringComparison.OrdinalIgnoreCase) ||
                !optionalPathRelationships.TryGetValue(path.Alias, out var relationshipsAlias) ||
                !TryGetPathNodeFunction(index, out var nodeFunction))
            {
                return false;
            }

            rewritten = Function(
                nodeFunction,
                new IndexExpression(
                    new VariableRef(relationshipsAlias),
                    Function("toInteger", new VariableRef("i"))));
            return true;
        }

        private bool TryRewriteMarkerAll(
            CypherExpression expression,
            out CypherExpression rewritten)
        {
            rewritten = null!;
            if (expression is not AllExpression
                {
                    IteratorAlias: var iteratorAlias,
                    Source: var source,
                    Predicate: BinaryExpression
                    {
                        Op: CypherBinaryOperator.Equal,
                        Left: PropertyAccess
                        {
                            Target: VariableRef predicateAlias,
                            Property: var marker,
                        },
                        Right: Literal { Value: true },
                    },
                } || !string.Equals(iteratorAlias, predicateAlias.Alias, StringComparison.Ordinal))
            {
                return false;
            }

            rewritten = new BinaryExpression(
                CypherBinaryOperator.Equal,
                Function(
                    "coalesce",
                    new PropertyAccess(
                        new IndexExpression(
                            Rewrite(source),
                            Function("toInteger", new Literal(0))),
                        marker),
                    new Literal(false)),
                new Literal(true));
            return true;
        }

        private bool TryRewriteCollectedPathReduction(
            CypherExpression expression,
            out CypherExpression rewritten)
        {
            rewritten = null!;
            if (expression is not ReduceExpression
                {
                    AccumulatorAlias: "flat",
                    Seed: ListExpression { Items.Count: 0 },
                    IteratorAlias: "path",
                    Source: FunctionCall
                    {
                        Name: var functionName,
                        Arguments: [var collectedPath],
                    },
                    Reducer: BinaryExpression
                    {
                        Op: CypherBinaryOperator.Add,
                        Left: VariableRef { Alias: "flat" },
                        Right: VariableRef { Alias: "path" },
                    },
                } || !string.Equals(functionName, "collect", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            rewritten = Function("collect", Rewrite(collectedPath));
            return true;
        }

        private bool TryRewriteTemporalMember(
            CypherExpression expression,
            out CypherExpression rewritten)
        {
            rewritten = null!;
            if (expression is not PropertyAccess
                {
                    Target: PropertyAccess { Target: VariableRef } value,
                    Property: var member,
                } || !TryGetTemporalMember(member, out var offset, out var length))
            {
                return false;
            }

            rewritten = Function(
                "toInteger",
                Function(
                    "substring",
                    Rewrite(value),
                    new Literal(offset),
                    new Literal(length)));
            return true;
        }

        private bool TryRewriteStringContains(
            CypherExpression expression,
            out CypherExpression rewritten)
        {
            rewritten = null!;
            if (expression is not BinaryExpression
                {
                    Op: CypherBinaryOperator.Contains,
                    Left: var left,
                    Right: var right,
                } || !IsContainsOperand(left) || !IsContainsOperand(right))
            {
                return false;
            }

            rewritten = new BinaryExpression(
                CypherBinaryOperator.Matches,
                Rewrite(left),
                new BinaryExpression(
                    CypherBinaryOperator.Add,
                    new BinaryExpression(
                        CypherBinaryOperator.Add,
                        new Literal(".*"),
                        Rewrite(right)),
                    new Literal(".*")));
            return true;
        }

        private static bool TryGetVariableLengthRelationshipAlias(
            PathPattern pattern,
            out string relationshipsAlias)
        {
            relationshipsAlias = null!;
            var relationships = pattern.Elements.OfType<RelationshipPattern>().ToArray();
            if (relationships is not [{ Alias: { } alias, Depth: not null }])
            {
                return false;
            }

            relationshipsAlias = alias;
            return true;
        }

        private static bool TryGetPathNodeFunction(CypherExpression index, out string function)
        {
            function = null!;
            if (index is VariableRef { Alias: "i" })
            {
                function = "startNode";
                return true;
            }

            if (index is BinaryExpression
                {
                    Op: CypherBinaryOperator.Add,
                    Left: VariableRef { Alias: "i" },
                    Right: Literal { Value: 1 },
                })
            {
                function = "endNode";
                return true;
            }

            return false;
        }

        private static bool IsPathIndex(CypherExpression index) =>
            index is VariableRef { Alias: "i" } ||
            index is BinaryExpression
            {
                Op: CypherBinaryOperator.Add,
                Left: VariableRef { Alias: "i" },
                Right: Literal { Value: 1 },
            };

        private static bool IsContainsOperand(CypherExpression expression) => expression is
            QueryParameter or
            PropertyAccess { Target: VariableRef };

        private static bool TryGetTemporalMember(string member, out int offset, out int length)
        {
            (offset, length) = member.ToLowerInvariant() switch
            {
                "year" => (0, 4),
                "month" => (5, 2),
                "day" => (8, 2),
                "hour" => (11, 2),
                "minute" => (14, 2),
                "second" => (17, 2),
                _ => (-1, -1),
            };
            return offset >= 0;
        }
    }
}
