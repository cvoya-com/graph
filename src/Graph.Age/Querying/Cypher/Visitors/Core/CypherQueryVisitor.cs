// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;

using System.Linq.Expressions;
using Cvoya.Graph.Age.Querying.Cypher.Lowering;
using Cvoya.Graph.Cypher;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Planning;
using Cvoya.Graph.Cypher.Validation;
using Cvoya.Graph.Querying;
using Microsoft.Extensions.Logging;
using AstFunctionCall = Cvoya.Graph.Cypher.Ast.Expressions.FunctionCall;

/// <summary>
/// Compatibility adapter that runs the shared semantic-model, planner, and Age renderer pipeline.
/// </summary>
internal sealed class CypherQueryVisitor : ExpressionVisitor
{
    private static readonly CypherPassRunner SetOperationProjectionPass = new(
    [
        new AgeSetOperationProjectionPass(),
    ]);

    private readonly Type _rootType;
    private readonly ILogger<CypherQueryVisitor>? _logger;
    private readonly bool _hasComplexProperties;

    public CypherQueryVisitor(
        Type type,
        ILoggerFactory? loggerFactory = null,
        bool hasComplexProperties = true)
    {
        _rootType = type ?? throw new ArgumentNullException(nameof(type));
        _logger = loggerFactory?.CreateLogger<CypherQueryVisitor>();
        _hasComplexProperties = hasComplexProperties;
    }

    private static CypherPassRunner CreateLoweringPasses(bool hasComplexProperties) => new(
    [
        new AgeRelationshipPredicatePass(),
        new AgeCorrelatedProjectionPass(),
        new AgeLabelPatternPass(hasComplexProperties),
        // Label lowering adds marker-based root-isolation EXISTS predicates. AGE parses those
        // subqueries but evaluates them incorrectly, so run the pattern-subquery lowering again
        // after labels have introduced them. The emitted ownership matches are label-free and do
        // not need another label pass.
        new AgeCorrelatedProjectionPass("__age_root_count"),
        new AgeClauseOrderPass(),
        new AgeTemporalParameterArithmeticPass(),
        new AgeInlineComplexPropertyProjectionPass(),
        new AgeEntityProjectionPass(),
        // Runs after projection lowering so every branch already ends in a RETURN it can
        // duplicate, and before set-operation projection normalization so the new branches get
        // identical column aliases. AgeLabelPatternPass keeps the selected pattern's labels in
        // MATCH for this pass.
        new AgeUnionAllExpansionPass(),
        new AgeSetOperationProjectionPass(),
    ]);

    public CypherQuery Query { get; private set; } = CypherQuery.Empty;

    public override Expression? Visit(Expression? node)
    {
        if (node is null)
        {
            return null;
        }

        _logger?.LogDebugCypherQueryVisitor36(_rootType);
        CypherStatement statement;
        var sumUnionCountBranches = false;
        try
        {
            WholeEntityOrderingValidator.Validate(node, AgeDialect.Instance);
            var model = GraphQueryModelBuilder.Build(node);
            statement = new CypherQueryPlanner(AgeDialect.PlanningInstance).Plan(model);
            statement = LowerStatement(statement, _hasComplexProperties);
            sumUnionCountBranches = model.Terminal == TerminalOperation.Count &&
                IsUnionOfGlobalCounts(statement.Clauses);
        }
        catch (GraphQueryTranslationException exception)
        {
            throw PreserveProviderException(exception);
        }

        var rendered = new CypherRenderer(AgeDialect.Instance).Render(statement);
        var projectionColumns = rendered.ProjectionColumns
            .Select(AgeEntityProjectionPass.NormalizeProjectionColumn)
            .ToArray();
        (Type Source, Type Relationship, Type Target)? pathTypes = statement.PathTypes is null
            ? null
            : (statement.PathTypes.Source, statement.PathTypes.Relationship, statement.PathTypes.Target);
        Query = new CypherQuery(
            rendered.Text,
            rendered.Parameters,
            pathTypes,
            projectionColumns,
            statement.PostProcessing,
            sumUnionCountBranches);
        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebugCypherQueryVisitor53(Query.Parameters.Keys.ToArray(), Query.Parameters.Count);
        }
        return node;
    }

    private sealed class WholeEntityOrderingValidator(AgeDialect dialect) : ExpressionVisitor
    {
        private static readonly HashSet<string> OrderingMethods = new(StringComparer.Ordinal)
        {
            nameof(Queryable.OrderBy),
            nameof(Queryable.OrderByDescending),
            nameof(Queryable.ThenBy),
            nameof(Queryable.ThenByDescending),
        };

        public static void Validate(Expression expression, AgeDialect dialect)
        {
            if (!dialect.Capabilities.Has(GraphCapability.OrderByEntity))
            {
                new WholeEntityOrderingValidator(dialect).Visit(expression);
            }
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (OrderingMethods.Contains(node.Method.Name) &&
                node.Arguments.Count >= 2 &&
                UnwrapLambda(node.Arguments[1]) is { } selector &&
                typeof(IEntity).IsAssignableFrom(StripConvert(selector.Body).Type))
            {
                throw new GraphQueryTranslationException(
                    $"Cypher construct 'OrderByEntity' requires capability '{GraphCapability.OrderByEntity}', " +
                    $"which dialect '{dialect.Name}' does not declare.");
            }

            return base.VisitMethodCall(node);
        }

        private static LambdaExpression? UnwrapLambda(Expression expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Quote or ExpressionType.Convert } unary)
            {
                expression = unary.Operand;
            }

            return expression as LambdaExpression;
        }

        private static Expression StripConvert(Expression expression)
        {
            while (expression is UnaryExpression
                { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            {
                expression = unary.Operand;
            }

            return expression;
        }
    }

    /// <summary>
    /// Gets whether the lowered statement is a UNION ALL whose every leaf returns exactly one
    /// global <c>count(...)</c>. The AGE provider sums those disjoint per-label counts client-side
    /// into the single scalar a LINQ count terminal expects.
    /// </summary>
    private static bool IsUnionOfGlobalCounts(IReadOnlyList<ICypherClause> clauses)
    {
        var hasSetOperation = false;
        foreach (var clause in clauses)
        {
            if (clause is not SetOperationClause setOperation)
            {
                continue;
            }

            hasSetOperation = true;
            if (!IsUnionOfGlobalCounts(setOperation.First) ||
                !IsUnionOfGlobalCounts(setOperation.Second))
            {
                return false;
            }
        }

        if (hasSetOperation)
        {
            return true;
        }

        return clauses.OfType<ReturnClause>().LastOrDefault() is { Items.Count: 1 } projection &&
            projection.Items[0].Expression is AstFunctionCall { Name: "count", Arguments.Count: 1 };
    }

    internal static CypherStatement LowerStatement(
        CypherStatement statement,
        bool hasComplexProperties = true)
    {
        if (!statement.Clauses.Any(clause => clause is SetOperationClause))
        {
            return CreateLoweringPasses(hasComplexProperties).Run(statement);
        }

        // Each branch is a complete query and needs the whole chain. Running that chain again over
        // the assembled set operation would lower labels a second time and expand the already
        // expanded branches again, so only the cross-branch projection normalization runs here.
        var branches = AgeClauseContainerTraversal.RunSetOperationBranches(
            statement,
            branch => LowerStatement(branch, hasComplexProperties));
        return SetOperationProjectionPass.Run(branches);
    }

    private static Exception PreserveProviderException(GraphQueryTranslationException exception)
    {
        if (exception.Message.StartsWith(
            "Cannot translate the correlated grouped projection:", StringComparison.Ordinal))
        {
            return exception;
        }

        if (exception.Message.Contains("SelectMany", StringComparison.Ordinal))
            return new NotSupportedException("SelectMany is not supported by LINQ-to-Cypher translation yet; see #100.");

        if (exception.Message.Contains("GroupBy", StringComparison.Ordinal))
            return new NotSupportedException(exception.Message);

        if (exception.Message.Contains("requires an explicit OrderBy", StringComparison.Ordinal))
            return new GraphException(exception.Message);

        if (exception.Message.Contains("chained after 'TraversePaths", StringComparison.Ordinal))
        {
            var methodEnd = exception.Message.IndexOf('(');
            var methodName = methodEnd > 2 ? exception.Message[2..methodEnd] : "operator";
            return new NotSupportedException(
                $"'.{methodName}(...)' chained after 'TraversePaths(...)' is not yet supported by LINQ-to-Cypher translation: " +
                $"TraversePaths returns IGraphPath, whose per-hop RETURN shape has no single row/alias a '{methodName}' " +
                "operator can translate against. Materialize the paths first (e.g. '.ToListAsync()') and continue with " +
                "IReadOnlyList<IGraphPath> client-side (LINQ-to-Objects) instead.");
        }

        return exception;
    }
}
