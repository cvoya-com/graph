// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Lowering;
using Cvoya.Graph.Cypher;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;

/// <summary>Provider-free regression tests for the generic native-id list lowering pass.</summary>
public sealed class AgeNativeIdListPassTests
{
    [Fact]
    public void SharedCollectionMembership_UsesOneUnwindForNativeIds()
    {
        var statement = Statement(new CollectionContainsExpression(
            new QueryParameter("ids"),
            new NativeElementIdentity(new VariableRef("n"))));

        var text = Render(new AgeNativeIdListPass().Run(statement));

        Assert.Equal(1, CountOccurrences(text, "UNWIND"));
        Assert.Contains("id(n) = __cvoya_native_id_0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("id(n) IN $ids", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalSharedCollectionMembership_KeepsThePortableRowPreservingPredicate()
    {
        var statement = Statement(
            new CollectionContainsExpression(
                new QueryParameter("ids"),
                new NativeElementIdentity(new VariableRef("n"))),
            optional: true);

        var text = Render(new AgeNativeIdListPass().Run(statement));

        Assert.DoesNotContain("UNWIND", text, StringComparison.Ordinal);
        Assert.Contains("__cvoya_collection_item = id(n)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("id(n) IN $ids", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SameIdParameterOnTwoAliases_UsesCastedInInsteadOfStackedUnwinds()
    {
        var predicate = new BinaryExpression(
            CypherBinaryOperator.And,
            NativeMembership("a"),
            NativeMembership("b"));
        var statement = Statement(
            predicate,
            returnAlias: "a",
            patterns:
            [
                new PathPattern([new NodePattern("a", ["A"])]),
                new PathPattern([new NodePattern("b", ["B"])]),
            ]);

        var rewritten = new AgeNativeIdListPass().Run(statement);
        var text = Render(rewritten);

        // Two UNWINDs of the same parameter would build |ids|^2 rows before the filter. Each alias
        // instead evaluates its own casted membership on the current row, so the row source stays
        // at the matching label pairs and the comparison remains a native list membership.
        Assert.DoesNotContain("UNWIND", text, StringComparison.Ordinal);
        Assert.Contains("id(a)::agtype IN $ids", text, StringComparison.Ordinal);
        Assert.Contains("id(b)::agtype IN $ids", text, StringComparison.Ordinal);
        Assert.DoesNotContain("size(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("id(a) IN $ids", text, StringComparison.Ordinal);
        Assert.DoesNotContain("id(b) IN $ids", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentParametersOnTwoAliases_AlsoAvoidsStackedUnwinds()
    {
        var predicate = new BinaryExpression(
            CypherBinaryOperator.And,
            NativeMembership("a", "idsA"),
            NativeMembership("b", "idsB"));
        var statement = new CypherStatement(
            StatementClauses(
                predicate,
                returnAlias: "a",
                patterns:
                [
                    new PathPattern([new NodePattern("a", ["A"])]),
                    new PathPattern([new NodePattern("b", ["B"])]),
                ]),
            new Dictionary<string, object?>
            {
                ["idsA"] = new long[] { 1, 2 },
                ["idsB"] = new long[] { 3, 4 },
            });

        var text = Render(new AgeNativeIdListPass().Run(statement));

        Assert.DoesNotContain("UNWIND", text, StringComparison.Ordinal);
        Assert.Contains("id(a)::agtype IN $idsA", text, StringComparison.Ordinal);
        Assert.Contains("id(b)::agtype IN $idsB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeInSupported_LeavesTheStatementUnchangedForAge17()
    {
        var statement = Statement(
            new BinaryExpression(
                CypherBinaryOperator.And,
                NativeMembership("a"),
                NativeMembership("b")));

        var rewritten = new AgeNativeIdListPass(nativeIdInPredicatesSupported: true).Run(statement);

        Assert.Same(statement, rewritten);
    }

    [Fact]
    public void LiteralNativeIdList_UsesCastedIn()
    {
        var statement = Statement(
            new BinaryExpression(
                CypherBinaryOperator.In,
                new NativeElementIdentity(new VariableRef("n")),
                new ListExpression([new Literal(1L), new Literal(2L)])));

        var text = Render(new AgeNativeIdListPass().Run(statement));

        Assert.Contains("id(n)::agtype IN [1, 2]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UNWIND", text, StringComparison.Ordinal);
        Assert.DoesNotContain("id(n) IN [1, 2]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LiteralNativeIdListWithANonInteger_FailsClosed()
    {
        var statement = Statement(
            new BinaryExpression(
                CypherBinaryOperator.In,
                new NativeElementIdentity(new VariableRef("n")),
                new ListExpression([new Literal(1L), new Literal("two")])));

        var exception = Assert.Throws<GraphQueryTranslationException>(
            () => new AgeNativeIdListPass().Run(statement));

        Assert.Contains("integer literals", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SameParameterAndAlias_SharesOneUnwind()
    {
        var predicate = new BinaryExpression(
            CypherBinaryOperator.And,
            NativeMembership("a"),
            NativeMembership("a"));
        var statement = Statement(
            predicate,
            returnAlias: "a",
            patterns: [new PathPattern([new NodePattern("a", ["A"])])]);

        var text = Render(new AgeNativeIdListPass().Run(statement));

        Assert.Equal(1, CountOccurrences(text, "UNWIND $ids AS "));
        Assert.Contains("id(a) = __cvoya_native_id_0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OrWithNativeIdMembership_UsesCastedIn()
    {
        var predicate = new BinaryExpression(
            CypherBinaryOperator.Or,
            NativeMembership("n"),
            new BinaryExpression(
                CypherBinaryOperator.Equal,
                new PropertyAccess(new VariableRef("n"), "Status"),
                new Literal("active")));
        var statement = Statement(predicate);

        var text = Render(new AgeNativeIdListPass().Run(statement));

        Assert.Contains("id(n)::agtype IN $ids", text, StringComparison.Ordinal);
        Assert.DoesNotContain("size(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UNWIND", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NotWithNativeIdMembership_WrapsTheCastedInPredicate()
    {
        var predicate = new UnaryExpression(CypherUnaryOperator.Not, NativeMembership("n"));
        var statement = Statement(predicate);

        var text = Render(new AgeNativeIdListPass().Run(statement));

        Assert.Contains("NOT (id(n)::agtype IN $ids)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("size(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalMatchWithNativeIdMembership_FailsClosed()
    {
        var statement = Statement(NativeMembership("n"), optional: true);

        var exception = Assert.Throws<GraphQueryTranslationException>(
            () => new AgeNativeIdListPass().Run(statement));

        Assert.Contains("OPTIONAL MATCH", exception.Message, StringComparison.Ordinal);
        Assert.Contains("native-id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonIdListParameter_FailsClosed()
    {
        var statement = new CypherStatement(
            StatementClauses(NativeMembership("n")),
            new Dictionary<string, object?> { ["ids"] = "not-an-id-list" });

        var exception = Assert.Throws<GraphQueryTranslationException>(
            () => new AgeNativeIdListPass().Run(statement));

        Assert.Contains("64-bit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("$ids", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyIdList_FoldsToFalseWithoutAnUnwind()
    {
        var statement = new CypherStatement(
            StatementClauses(NativeMembership("n")),
            new Dictionary<string, object?> { ["ids"] = Array.Empty<long>() });

        var text = Render(new AgeNativeIdListPass().Run(statement));

        Assert.Contains("WHERE false", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UNWIND", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IN $ids", text, StringComparison.Ordinal);
    }

    private static CypherStatement Statement(
        CypherExpression predicate,
        string returnAlias = "n",
        bool optional = false,
        IReadOnlyList<PathPattern>? patterns = null) =>
        new(
            StatementClauses(predicate, returnAlias, optional, patterns),
            new Dictionary<string, object?> { ["ids"] = new long[] { 1, 2 } });

    private static List<ICypherClause> StatementClauses(
        CypherExpression predicate,
        string returnAlias = "n",
        bool optional = false,
        IReadOnlyList<PathPattern>? patterns = null) =>
    [
        new MatchClause(
            patterns ?? [new PathPattern([new NodePattern("n", ["N"])])],
            optional),
        new WhereClause(predicate),
        new ReturnClause([new ReturnItem(new VariableRef(returnAlias), null)], distinct: false),
    ];

    private static BinaryExpression NativeMembership(string alias, string parameterName = "ids") =>
        new(
            CypherBinaryOperator.In,
            new NativeElementIdentity(new VariableRef(alias)),
            new QueryParameter(parameterName));

    private static string Render(CypherStatement statement) =>
        new CypherRenderer(AgeDialect.Instance).Render(statement).Text;

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
