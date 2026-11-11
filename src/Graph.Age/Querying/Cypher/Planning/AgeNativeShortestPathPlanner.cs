// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Planning;

using System.Linq.Expressions;
using Cvoya.Graph.Age.Serialization;
using Cvoya.Graph.Cypher;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;
using Cvoya.Graph.Cypher.Planning;
using Cvoya.Graph.Querying;
using AstBinaryExpression = Cvoya.Graph.Cypher.Ast.Expressions.BinaryExpression;
using AstIndexExpression = Cvoya.Graph.Cypher.Ast.Expressions.IndexExpression;

/// <summary>
/// Plans Apache AGE 1.8 native shortest-path functions. AGE 1.8 rejects the Cypher
/// <c>shortestPath()</c>/<c>allShortestPaths()</c> pattern syntax, so this planner emits a
/// plain node-pair match followed by <c>ag_catalog.age_shortest_path</c> or
/// <c>ag_catalog.age_all_shortest_paths</c>.
/// </summary>
internal sealed class AgeNativeShortestPathPlanner
{
    private const string PathAlias = "p";
    private const string SourceAlias = "src";
    private const string TargetAlias = "tgt";
    private const string ShortestPathFunction = "ag_catalog.age_shortest_path";
    private const string AllShortestPathsFunction = "ag_catalog.age_all_shortest_paths";

    private readonly AgeDialect dialect;
    private readonly string graphName;

    public AgeNativeShortestPathPlanner(AgeDialect dialect, string graphName)
    {
        this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        ArgumentException.ThrowIfNullOrWhiteSpace(graphName);
        this.graphName = graphName;
    }

    /// <summary>Gets whether the model selects shortest paths.</summary>
    public static bool IsShortestPath(GraphQueryModel model) =>
        model.Traversal.Any(step => !step.IsComplexPropertyTraversal &&
            step.PathSelection != TraversalPathSelection.All);

    /// <summary>Plans the native AGE shortest-path statement.</summary>
    public CypherStatement Plan(GraphQueryModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!IsShortestPath(model))
        {
            throw new ArgumentException("The query model does not select shortest paths.", nameof(model));
        }

        var steps = model.Traversal.Where(step => !step.IsComplexPropertyTraversal).ToArray();
        if (steps.Length != 1 || model.PathShape is null)
        {
            throw NotSupported("native shortest paths require exactly one explicit traversal step");
        }

        var step = steps[0];
        if (step.RelationshipPredicates.Count > 0)
        {
            throw NotSupported("relationship predicates are not supported on native shortest paths");
        }

        if (model.Join is not null ||
            model.RelationshipExistence.Count > 0 ||
            model.SearchFilter is not null ||
            model.GroupBy is not null ||
            model.SelectMany is not null ||
            model.Union is not null ||
            model.PostPaging is not null)
        {
            throw NotSupported("this query composition is not supported on native shortest paths");
        }

        if (model.Terminal is not (TerminalOperation.ToListOrArray or TerminalOperation.Count or TerminalOperation.Any))
        {
            throw new GraphQueryTranslationException(
                $"Terminal '{model.Terminal}' after TraversePaths is not supported yet.");
        }

        var parameters = new CypherParameterRegistry();
        var lowerer = new ExpressionToCypherAstLowerer(parameters, dialect);
        var clauses = new List<ICypherClause>
        {
            new MatchClause(
            [
                new PathPattern([new NodePattern(SourceAlias, TraversalNodeLabels(model.PathShape.SourceType))]),
                new PathPattern([new NodePattern(TargetAlias, TraversalNodeLabels(step.TargetType ?? model.PathShape.TargetType))]),
            ], optional: false),
        };

        var predicates = new List<CypherExpression>();
        var pathPredicates = new List<PredicateFragment>();
        foreach (var predicate in model.Predicates)
        {
            if (IsPathScope(predicate))
            {
                pathPredicates.Add(predicate);
                continue;
            }

            predicates.Add(lowerer.LowerLambda(predicate.Predicate, predicate.Alias ?? SourceAlias));
        }

        foreach (var filter in model.LabelFilters)
        {
            var target = new VariableRef(filter.Alias ?? SourceAlias);
            if (filter.Match == GraphLabelMatch.Any)
            {
                predicates.Add(new LabelTest(target, filter.Labels));
            }
            else
            {
                predicates.AddRange(filter.Labels.Select(label => (CypherExpression)new LabelTest(target, [label])));
            }
        }

        // A shortest path always traverses at least one relationship, so the endpoints must differ.
        predicates.Add(new AstBinaryExpression(
            CypherBinaryOperator.NotEqual,
            new VariableRef(SourceAlias),
            new VariableRef(TargetAlias)));

        foreach (var predicate in step.TargetPredicates)
        {
            predicates.Add(lowerer.LowerLambda(predicate.Predicate, step.TargetAlias ?? TargetAlias));
        }

        clauses.AddRange(lowerer.NavigationMatches);
        if (predicates.Count > 0)
        {
            clauses.Add(new WhereClause(predicates.Count == 1
                ? predicates[0]
                : new ConjunctionExpression(predicates)));
        }

        var edgeTypes = new ListExpression(TraversalRelationshipTypes(step)
            .Select(type => (CypherExpression)new Literal(
                SerializationBridge.GetRootStorageName(type, relationship: true)))
            .ToArray());
        var maxHops = step.Depth.Max == int.MaxValue
            ? new Literal(null)
            : new Literal(step.Depth.Max);
        var function = new FunctionCall(
            step.PathSelection == TraversalPathSelection.AllShortest
                ? AllShortestPathsFunction
                : ShortestPathFunction,
            [
                new Literal(graphName),
                new VariableRef(SourceAlias),
                new VariableRef(TargetAlias),
                edgeTypes,
                new Literal(DirectionName(step.Direction)),
                new Literal(step.Depth.Min),
                maxHops,
            ]);

        clauses.Add(new WithClause(
            [new ReturnItem(function, PathAlias)],
            distinct: false));

        if (pathPredicates.Count > 0)
        {
            var pathLowerer = new ExpressionToCypherAstLowerer(parameters, dialect);
            var lowered = pathPredicates
                .Select(predicate => pathLowerer.LowerLambda(predicate.Predicate, PathAlias))
                .ToArray();
            clauses.Add(new WhereClause(lowered.Length == 1
                ? lowered[0]
                : new ConjunctionExpression(lowered)));
        }

        var pathOrdering = model.Ordering
            .Where(key => IsPathScope(key.KeySelector))
            .Select(key => new OrderByItem(
                lowerer.LowerLambda(key.KeySelector, PathAlias),
                key.Descending))
            .ToArray();
        if (pathOrdering.Length > 0)
        {
            clauses.Add(new OrderByClause(pathOrdering));
        }

        var returnsPath = model.Projection is null && model.Terminal == TerminalOperation.ToListOrArray;
        if (returnsPath)
        {
            // Paging windows the whole-path rows before they are decomposed into per-hop rows,
            // exactly like the shared TraversePaths plan.
            AddPaging(clauses, model.Paging);
            AddDecomposedPathProjection(clauses, model);
        }
        else if (model.Terminal is TerminalOperation.Count or TerminalOperation.Any)
        {
            if (model.Paging.Skip is not null || model.Paging.Take is not null)
            {
                clauses.Add(new WithClause([new ReturnItem(new VariableRef(PathAlias), null)], distinct: false));
                AddPaging(clauses, model.Paging);
            }

            var count = new FunctionCall("count", [new VariableRef(PathAlias)]);
            clauses.Add(new ReturnClause(
            [
                new ReturnItem(
                    model.Terminal == TerminalOperation.Any
                        ? new AstBinaryExpression(CypherBinaryOperator.GreaterThan, count, new Literal(0))
                        : count,
                    model.Terminal == TerminalOperation.Any ? "exists" : null),
            ], distinct: false));
        }
        else
        {
            // Paging applies to the whole-path rows before the projection, matching the shared plan.
            AddPaging(clauses, model.Paging);
            AddPathProjection(clauses, model, lowerer);
        }

        var pathTypes = returnsPath
            ? new CypherPathTypes(
                model.PathShape.SourceType,
                step.RelationshipClrType ?? throw NotSupported("the shortest-path relationship type is unknown"),
                model.PathShape.TargetType)
            : null;
        return new CypherStatement(clauses, parameters.Parameters, pathTypes);
    }

    // Decomposes each whole native path value into one row per hop (pathIndex/hopIndex plus a
    // PathSegment map), the same shape the shared TraversePaths plan produces, so hydration —
    // including complex-property subgraphs via EntityProjectionExpression — runs through the
    // standard result pipeline instead of a native-path-only materializer.
    private static void AddDecomposedPathProjection(
        List<ICypherClause> clauses,
        GraphQueryModel model)
    {
        var pathShape = model.PathShape!;
        clauses.Add(new WithClause(
            [new ReturnItem(new FunctionCall("collect", [new VariableRef(PathAlias)]), "__paths")],
            distinct: false));
        clauses.Add(new UnwindClause(
            new FunctionCall("range",
            [
                new Literal(0),
                new AstBinaryExpression(
                    CypherBinaryOperator.Subtract,
                    new FunctionCall("size", [new VariableRef("__paths")]),
                    new Literal(1)),
            ]),
            "pathIndex"));
        clauses.Add(new WithClause(
        [
            new ReturnItem(new AstIndexExpression(new VariableRef("__paths"), new VariableRef("pathIndex")), PathAlias),
            new ReturnItem(new VariableRef("pathIndex"), null)
        ], distinct: false));
        clauses.Add(new UnwindClause(
            new FunctionCall("range",
            [
                new Literal(0),
                new AstBinaryExpression(
                    CypherBinaryOperator.Subtract,
                    new FunctionCall("length", [new VariableRef(PathAlias)]),
                    new Literal(1)),
            ]),
            "hopIndex"));
        clauses.Add(new WithClause(
        [
            new ReturnItem(new VariableRef("pathIndex"), null),
            new ReturnItem(new VariableRef("hopIndex"), null),
            new ReturnItem(
                new AstIndexExpression(new FunctionCall("nodes", [new VariableRef(PathAlias)]), new VariableRef("hopIndex")),
                "src"),
            new ReturnItem(
                new AstIndexExpression(new FunctionCall("relationships", [new VariableRef(PathAlias)]), new VariableRef("hopIndex")),
                "r"),
            new ReturnItem(
                new AstIndexExpression(
                    new FunctionCall("nodes", [new VariableRef(PathAlias)]),
                    new AstBinaryExpression(CypherBinaryOperator.Add, new VariableRef("hopIndex"), new Literal(1))),
                "tgt")
        ], distinct: false));
        // A shortest path can have more than one hop, so every hop column can also hold an
        // unconstrained intermediate node whose runtime type is unknowable at planning time —
        // load complex properties rather than silently under-hydrating it, exactly like the
        // shared plan does for multi-hop traversals.
        var intermediateNodesPossible =
            model.Traversal.LastOrDefault(step => !step.IsComplexPropertyTraversal) is not { Depth.Max: <= 1 };
        var loadSourceProperties = intermediateNodesPossible || CypherQueryPlanner.HasComplexProperties(pathShape.SourceType);
        var loadTargetProperties = intermediateNodesPossible || CypherQueryPlanner.HasComplexProperties(pathShape.TargetType);
        clauses.Add(new ReturnClause(
        [
            new ReturnItem(new VariableRef("pathIndex"), null),
            new ReturnItem(new VariableRef("hopIndex"), null),
            new ReturnItem(
                new MapExpression(
                [
                    new MapEntry("StartNode", new EntityProjectionExpression("src", loadSourceProperties)),
                    new MapEntry("Relationship", new VariableRef("r")),
                    new MapEntry("EndNode", new EntityProjectionExpression("tgt", loadTargetProperties)),
                ]),
                "PathSegment"),
        ], distinct: false));
        clauses.Add(new OrderByClause(
        [
            new OrderByItem(new VariableRef("pathIndex"), descending: false),
            new OrderByItem(new VariableRef("hopIndex"), descending: false)
        ]));
    }

    private static void AddPathProjection(
        List<ICypherClause> clauses,
        GraphQueryModel model,
        ExpressionToCypherAstLowerer lowerer)
    {
        var selector = model.Projection?.Selector
            ?? throw NotSupported("only path materialization, count, any, and simple projections are supported");

        var body = StripConvert(selector.Body);
        if (body is MemberExpression
            {
                Expression: ParameterExpression parameter,
                Member.Name: nameof(IGraphPath.Start) or nameof(IGraphPath.End),
            } member && parameter.Type == typeof(IGraphPath))
        {
            var entity = lowerer.LowerLambda(selector, PathAlias);
            // Start/End are declared INode, but the pattern label-constrains them to the path
            // shape's endpoint types — use those rather than the interface, which always reports
            // complex properties.
            var entityType = member.Member.Name == nameof(IGraphPath.Start)
                ? model.PathShape!.SourceType
                : model.PathShape!.TargetType;
            var loadProperties = CypherQueryPlanner.HasComplexProperties(entityType);
            clauses.Add(new WithClause(
                loadProperties
                    ? [new ReturnItem(new VariableRef(PathAlias), null), new ReturnItem(entity, "__pathEntity")]
                    : [new ReturnItem(entity, "__pathEntity")],
                distinct: false));
            clauses.Add(new EntityProjectionClause(
                EntityProjectionShape.Node,
                "__pathEntity",
                relationshipAlias: null,
                targetAlias: null,
                loadSourceProperties: loadProperties,
                loadTargetProperties: false,
                includePathCoordinates: false,
                ordering: [],
                rowIdentityAliases: loadProperties ? [PathAlias] : []));
            return;
        }

        clauses.Add(new ReturnClause(
            [new ReturnItem(lowerer.LowerLambda(selector, PathAlias), null)],
            distinct: false));
    }

    private static void AddPaging(List<ICypherClause> clauses, Paging paging)
    {
        if (paging.Skip is { } skip)
        {
            clauses.Add(new SkipClause(new Literal(skip)));
        }

        if (paging.Take is { } take)
        {
            clauses.Add(new LimitClause(new Literal(take)));
        }
    }

    private static string DirectionName(GraphTraversalDirection direction) => direction switch
    {
        GraphTraversalDirection.Outgoing => "out",
        GraphTraversalDirection.Incoming => "in",
        GraphTraversalDirection.Both => "any",
        _ => throw NotSupported($"traversal direction '{direction}' is not supported"),
    };

    // The base INode interface and DynamicNode match nodes whose labels need not correspond to any
    // loaded CLR type, so a compatible-labels list cannot bound them.
    private static List<string> TraversalNodeLabels(Type? type) =>
        type is null || type == typeof(INode) || type == typeof(DynamicNode)
            ? []
            : Labels.GetCompatibleLabels(type);

    // The base IRelationship interface and DynamicRelationship match edges whose stored type
    // strings need not correspond to any loaded CLR type.
    private static List<string> TraversalRelationshipTypes(TraversalStep step) =>
        step.RelationshipClrType is { } clrType
            ? clrType == typeof(IRelationship) || clrType == typeof(DynamicRelationship)
                ? []
                : Labels.GetCompatibleLabels(clrType)
            : step.RelationshipType is { } relationshipType ? [relationshipType] : [];

    private static bool IsPathScope(PredicateFragment predicate) =>
        predicate.Alias == PathAlias ||
        predicate.Predicate.Parameters.Any(parameter =>
            typeof(IGraphPath).IsAssignableFrom(parameter.Type));

    private static bool IsPathScope(LambdaExpression selector) =>
        selector.Parameters.Any(parameter =>
            typeof(IGraphPath).IsAssignableFrom(parameter.Type));

    private static Expression StripConvert(Expression expression)
    {
        while (expression is System.Linq.Expressions.UnaryExpression
            { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static GraphQueryTranslationException NotSupported(string reason) =>
        new($"The Apache AGE native shortest-path translation does not support {reason}.");
}
