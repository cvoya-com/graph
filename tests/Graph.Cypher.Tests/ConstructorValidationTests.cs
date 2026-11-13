// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Cypher.Tests;

using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;

public class ConstructorValidationTests
{
    [Fact]
    public void CypherStatement_RejectsEmptyClauses()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new CypherStatement([], new Dictionary<string, object?>()));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DepthRange_RejectsNegativeMinimum()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new DepthRange(-1, 1));

        Assert.Contains("minimum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DepthRange_RejectsInvertedRange()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new DepthRange(2, 1));

        Assert.Contains("maximum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathPattern_RejectsNonAlternatingElements()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(
            () => new PathPattern(
            [
                new NodePattern("a", []),
                new NodePattern("b", [])
            ]));

        Assert.Contains("alternate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathPattern_RejectsRelationshipEndpoint()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(
            () => new PathPattern(
            [
                new NodePattern("a", []),
                new RelationshipPattern("r", "KNOWS", CypherDirection.Outgoing, null)
            ]));

        Assert.Contains("node", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathPattern_RejectsUnsupportedPatternElement()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(
            () => new PathPattern(
            [
                new NodePattern("a", []),
                new UnsupportedPatternElement(),
                new NodePattern("b", [])
            ]));

        Assert.Contains("alternate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PathPattern_AcceptsMultipleHops()
    {
        var pattern = new PathPattern(
        [
            new NodePattern("a", []),
            new RelationshipPattern("r1", "KNOWS", CypherDirection.Outgoing, null),
            new NodePattern("b", []),
            new RelationshipPattern("r2", "REPORTS_TO", CypherDirection.Incoming, null),
            new NodePattern("c", [])
        ]);

        Assert.Equal(5, pattern.Elements.Count);
    }

    [Fact]
    public void RequiredNames_RejectWhitespace()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new VariableRef(" "));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptionalNames_RejectEmptyValues()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new NodePattern(string.Empty, []));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequiredCollections_RejectNullElements()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(
            () => new MatchClause([null!], optional: false));

        Assert.Contains("null", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructors_CopyInputCollections()
    {
        var labels = new List<string> { "Person" };
        var node = new NodePattern("n", labels);

        labels.Add("Mutated");

        Assert.Equal(["Person"], node.Labels);
    }

    [Fact]
    public void EntityProjectionClause_ValidatesAndCopiesRowIdentityAliases()
    {
        var aliases = new List<string> { "r" };
        var projection = new EntityProjectionClause(
            EntityProjectionShape.Node,
            "tgt",
            relationshipAlias: null,
            targetAlias: null,
            loadSourceProperties: true,
            loadTargetProperties: false,
            includePathCoordinates: false,
            ordering: [],
            rowIdentityAliases: aliases);

        aliases.Add("other");

        Assert.Equal(["r"], projection.RowIdentityAliases);
        Assert.Throws<ArgumentException>(() => new EntityProjectionClause(
            EntityProjectionShape.Node,
            "tgt",
            relationshipAlias: null,
            targetAlias: null,
            loadSourceProperties: true,
            loadTargetProperties: false,
            includePathCoordinates: false,
            ordering: [],
            rowIdentityAliases: ["r", "r"]));
        Assert.Throws<ArgumentException>(() => new EntityProjectionClause(
            EntityProjectionShape.Node,
            "tgt",
            relationshipAlias: null,
            targetAlias: null,
            loadSourceProperties: true,
            loadTargetProperties: false,
            includePathCoordinates: false,
            ordering: [],
            rowIdentityAliases: ["tgt"]));
    }

    [Fact]
    public void ExpressionConstructors_RejectNullOperands()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new PropertyAccess(null!, "name"));

        Assert.Contains("target", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListComprehensionExpression_RejectsMissingPredicateAndProjection()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new ListComprehensionExpression(new VariableRef("items"), "item"));

        Assert.Contains("predicate", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("projection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReduceExpression_RejectsMatchingAccumulatorAndIteratorAliases()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new ReduceExpression(
                "item",
                new Literal(0),
                "item",
                new VariableRef("items"),
                new VariableRef("item")));

        Assert.Equal("iteratorAlias", ex.ParamName);
        Assert.Contains("different", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalExpressionAliases_RejectWhitespace()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new ListComprehensionExpression(new VariableRef("items"), " ", projection: new Literal(1)));
        Assert.ThrowsAny<ArgumentException>(
            () => new ReduceExpression(" ", new Literal(0), "item", new VariableRef("items"), new Literal(1)));
        Assert.ThrowsAny<ArgumentException>(
            () => new ReduceExpression("total", new Literal(0), " ", new VariableRef("items"), new Literal(1)));
        Assert.ThrowsAny<ArgumentException>(
            () => new AllExpression(" ", new VariableRef("items"), new Literal(true)));
    }

    [Fact]
    public void LocalExpressions_RejectNullRequiredExpressions()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ListComprehensionExpression(null!, "item", projection: new Literal(1)));
        Assert.Throws<ArgumentNullException>(
            () => new ReduceExpression("total", null!, "item", new VariableRef("items"), new Literal(1)));
        Assert.Throws<ArgumentNullException>(
            () => new ReduceExpression("total", new Literal(0), "item", null!, new Literal(1)));
        Assert.Throws<ArgumentNullException>(
            () => new ReduceExpression("total", new Literal(0), "item", new VariableRef("items"), null!));
        Assert.Throws<ArgumentNullException>(
            () => new AllExpression("item", null!, new Literal(true)));
        Assert.Throws<ArgumentNullException>(
            () => new AllExpression("item", new VariableRef("items"), null!));
    }

    [Fact]
    public void LocalExpressions_UseRecordValueEquality()
    {
        Assert.Equal(
            new ListComprehensionExpression(new VariableRef("items"), "item", projection: new VariableRef("item")),
            new ListComprehensionExpression(new VariableRef("items"), "item", projection: new VariableRef("item")));
        Assert.Equal(
            new ReduceExpression(
                "total",
                new Literal(0),
                "item",
                new VariableRef("items"),
                new VariableRef("total")),
            new ReduceExpression(
                "total",
                new Literal(0),
                "item",
                new VariableRef("items"),
                new VariableRef("total")));
        Assert.Equal(
            new AllExpression("item", new VariableRef("items"), new VariableRef("item")),
            new AllExpression("item", new VariableRef("items"), new VariableRef("item")));
    }

    [Fact]
    public void RelationshipPattern_RejectsUndefinedDirection()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new RelationshipPattern(null, null, (CypherDirection)99, null));

        Assert.Equal("direction", ex.ParamName);
    }

    [Fact]
    public void RelationshipPattern_AcceptsDefinedDirection()
    {
        var relationship = new RelationshipPattern(null, null, CypherDirection.Both, null);

        Assert.Equal(CypherDirection.Both, relationship.Direction);
    }

    [Fact]
    public void RelationshipPattern_AcceptsMultipleTypes()
    {
        var relationship = new RelationshipPattern(
            "r",
            CypherDirection.Outgoing,
            depth: null,
            ["KNOWS", "REPORTS_TO"]);

        Assert.Equal(["KNOWS", "REPORTS_TO"], relationship.Types);
    }

    [Fact]
    public void BinaryExpression_RejectsUndefinedOperator()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new BinaryExpression((CypherBinaryOperator)99, new Literal(1), new Literal(2)));

        Assert.Equal("op", ex.ParamName);
    }

    [Fact]
    public void BinaryExpression_AcceptsDefinedOperator()
    {
        var expression = new BinaryExpression(CypherBinaryOperator.Add, new Literal(1), new Literal(2));

        Assert.Equal(CypherBinaryOperator.Add, expression.Op);
    }

    [Fact]
    public void UnaryExpression_RejectsUndefinedOperator()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new UnaryExpression((CypherUnaryOperator)99, new Literal(1)));

        Assert.Equal("op", ex.ParamName);
    }

    [Fact]
    public void UnaryExpression_AcceptsDefinedOperator()
    {
        var expression = new UnaryExpression(CypherUnaryOperator.Negate, new Literal(1));

        Assert.Equal(CypherUnaryOperator.Negate, expression.Op);
    }

    [Fact]
    public void EntityProjectionExpression_KeepsTheTwoArgumentConstructorForBinaryCompatibility()
    {
        // The (string, bool) overload must remain a real CLR constructor: a consumer compiled
        // against the existing assembly resolves GetConstructor(string, bool) and fails with
        // MissingMethodException when the signature only exists through an optional parameter.
        var twoArgument = typeof(EntityProjectionExpression).GetConstructor([typeof(string), typeof(bool)]);
        Assert.NotNull(twoArgument);

        var expression = (EntityProjectionExpression)twoArgument.Invoke(["node", true]);
        Assert.Equal("node", expression.Alias);
        Assert.True(expression.LoadComplexProperties);
        Assert.Equal(GraphDataModel.DefaultDepthAllowed, expression.ComplexPropertyDepth);

        var threeArgument = typeof(EntityProjectionExpression).GetConstructor(
            [typeof(string), typeof(bool), typeof(int)]);
        Assert.NotNull(threeArgument);

        var deep = (EntityProjectionExpression)threeArgument.Invoke(["node", false, 2]);
        Assert.Equal(2, deep.ComplexPropertyDepth);
    }

    private sealed record UnsupportedPatternElement : PatternElement;
}
