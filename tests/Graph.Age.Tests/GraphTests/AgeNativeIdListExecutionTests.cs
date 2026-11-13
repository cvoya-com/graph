// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using System.Diagnostics;
using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Querying.Cypher.Lowering;
using Cvoya.Graph.CompatibilityTests;
using Cvoya.Graph.Cypher;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;

/// <summary>
/// Executes the generic native-id list lowering against the live AGE version. The OR and NOT
/// shapes use the row-preserving comprehension; the two-alias shape proves that each native
/// identity stays independent without stacking UNWIND row sources.
/// </summary>
public sealed class AgeNativeIdListExecutionTests(AgeHarness harness)
    : AgeTest(harness, StoreIsolation.FreshStore)
{
    [Fact]
    public async Task OrWithNativeIdList_ReturnsEveryMatchingBranch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ids = await SeedAsync(cancellationToken);
        var predicate = new BinaryExpression(
            CypherBinaryOperator.Or,
            NativeMembership("n"),
            new BinaryExpression(
                CypherBinaryOperator.Equal,
                new PropertyAccess(new VariableRef("n"), "FirstName"),
                new Literal("active")));
        var statement = Statement(
            [new PathPattern([new NodePattern("n", ["Person"])])],
            predicate,
            [new ReturnItem(new NativeElementIdentity(new VariableRef("n")), "id")],
            new Dictionary<string, object?> { ["ids"] = new long[] { ids[1] } });

        var rows = await ExecuteAsync(statement, cancellationToken);

        // The second node is selected by id; the active node is selected by the alternative branch.
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task NotWithNativeIdList_ReturnsOnlyTheComplement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ids = await SeedAsync(cancellationToken);
        var predicate = new UnaryExpression(CypherUnaryOperator.Not, NativeMembership("n"));
        var statement = Statement(
            [new PathPattern([new NodePattern("n", ["Person"])])],
            predicate,
            [new ReturnItem(new NativeElementIdentity(new VariableRef("n")), "id")],
            new Dictionary<string, object?> { ["ids"] = new long[] { ids[0], ids[1] } });

        var rows = await ExecuteAsync(statement, cancellationToken);

        Assert.Single(rows);
    }

    [Fact]
    public async Task SameIdParameterOnTwoAliases_KeepsIndependentMembership()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ids = await SeedAsync(cancellationToken);
        var predicate = new BinaryExpression(
            CypherBinaryOperator.And,
            NativeMembership("a"),
            NativeMembership("b"));
        var statement = Statement(
            [
                new PathPattern([new NodePattern("a", ["Person"])]),
                new PathPattern([new NodePattern("b", ["Person"])]),
            ],
            predicate,
            [
                new ReturnItem(new NativeElementIdentity(new VariableRef("a")), "aid"),
                new ReturnItem(new NativeElementIdentity(new VariableRef("b")), "bid"),
            ],
            new Dictionary<string, object?> { ["ids"] = new long[] { ids[0], ids[1] } });

        var rows = await ExecuteAsync(statement, cancellationToken);

        // Both aliases independently range over the two ids. A shared UNWIND alias would force
        // a == b and return two rows instead of the four valid pairs.
        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public async Task TwoAliasesWithOneThousandIds_DoesNotBuildTheIdCrossProduct()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ids = await SeedBenchGraphAsync(nodeCount: 1000, cancellationToken);
        var predicate = new BinaryExpression(
            CypherBinaryOperator.And,
            NativeMembership("a"),
            NativeMembership("b"));
        var statement = Statement(
            [
                new PathPattern(
                [
                    new NodePattern("a", ["NativeIdBench"]),
                    new RelationshipPattern(null, "KNOWS", CypherDirection.Outgoing, depth: null),
                    new NodePattern("b", ["NativeIdBench"]),
                ]),
            ],
            predicate,
            [new ReturnItem(new NativeElementIdentity(new VariableRef("a")), "aid")],
            new Dictionary<string, object?> { ["ids"] = ids });

        // Force the portable lowering on both AGE versions so the test exercises the multi-alias
        // fix rather than the 1.7 native-IN fast path.
        var pass = new AgeNativeIdListPass();
        var rewritten = pass.Run(statement);
        var text = new CypherRenderer(AgeDialect.Instance).Render(rewritten).Text;
        Assert.DoesNotContain("UNWIND", text, StringComparison.Ordinal);
        Assert.Contains("id(a)::agtype IN $ids", text, StringComparison.Ordinal);
        Assert.Contains("id(b)::agtype IN $ids", text, StringComparison.Ordinal);

        var stopwatch = Stopwatch.StartNew();
        var rows = await ExecuteAsync(rewritten, pass, cancellationToken);
        stopwatch.Stop();

        // 1,000 nodes in a chain of 999 edges. Stacked UNWINDs would build a 1,000,000-row source
        // before MATCH; the casted membership keeps the source at the 999 matching edges.
        Assert.Equal(999, rows.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(60),
            $"The 1,000-id two-alias query took {stopwatch.Elapsed}, which indicates a row cross product.");
    }

    [Fact]
    public async Task LiteralNativeIdList_ExecutesThroughTheCastedInForm()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ids = await SeedAsync(cancellationToken);
        var statement = Statement(
            [new PathPattern([new NodePattern("n", ["Person"])])],
            new BinaryExpression(
                CypherBinaryOperator.In,
                new NativeElementIdentity(new VariableRef("n")),
                new ListExpression([new Literal(ids[0]), new Literal(ids[1])])),
            [new ReturnItem(new NativeElementIdentity(new VariableRef("n")), "id")],
            new Dictionary<string, object?>());

        var rows = await ExecuteAsync(statement, new AgeNativeIdListPass(), cancellationToken);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task OptionalMatchWithNativeIdList_MatchesOnAge17AndFailsClosedOnAge18()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var ids = await SeedAsync(cancellationToken);
        var statement = Statement(
            [new PathPattern([new NodePattern("n", ["Person"])])],
            NativeMembership("n"),
            [new ReturnItem(new NativeElementIdentity(new VariableRef("n")), "id")],
            new Dictionary<string, object?> { ["ids"] = new long[] { ids[0] } },
            optional: true);

        var version = await GetAgeVersionAsync(cancellationToken);
        var store = ((AgeGraph)this.Graph).Store;
        var nativeInSupported = version < new Version(1, 8);
        Assert.Equal(nativeInSupported, store.SupportsNativeIdInPredicates);

        var pass = new AgeNativeIdListPass(store.SupportsNativeIdInPredicates);
        if (!nativeInSupported)
        {
            // AGE 1.8 cannot type the comprehension in an optional-match predicate and the original
            // IN shape segfaults, so the pass must reject the shape instead of emitting it.
            var exception = Assert.Throws<GraphQueryTranslationException>(() => pass.Run(statement));
            Assert.Contains("OPTIONAL MATCH", exception.Message, StringComparison.Ordinal);
            return;
        }

        // AGE 1.7 keeps the native IN form; the matching shape executes without a rewrite.
        var matchingRows = await ExecuteAsync(statement, pass, cancellationToken);
        Assert.Single(matchingRows);
    }

    private async Task<long[]> SeedBenchGraphAsync(int nodeCount, CancellationToken cancellationToken)
    {
        var indices = string.Join(", ", Enumerable.Range(0, nodeCount));
        var edgeIndices = string.Join(", ", Enumerable.Range(0, nodeCount - 1));
        await using var transaction = await this.Graph.GetTransactionAsync(cancellationToken);
        var native = (AgeGraphTransaction)transaction;

        await using (var result = await native.Runner.RunAsync(
            $"UNWIND [{indices}] AS i CREATE (n:NativeIdBench) SET n.Idx = i",
            new { },
            cancellationToken))
        {
            _ = await result.ToListAsync(cancellationToken);
        }

        await using (var result = await native.Runner.RunAsync(
            $"UNWIND [{edgeIndices}] AS i " +
            "MATCH (a:NativeIdBench) WHERE a.Idx = i " +
            "MATCH (b:NativeIdBench) WHERE b.Idx = i + 1 " +
            "CREATE (a)-[:KNOWS]->(b)",
            new { },
            cancellationToken))
        {
            _ = await result.ToListAsync(cancellationToken);
        }

        await using (var result = await native.Runner.RunAsync(
            "MATCH (n:NativeIdBench) RETURN id(n) AS id ORDER BY n.Idx",
            new { },
            cancellationToken))
        {
            var records = await result.ToListAsync(cancellationToken);
            var ids = records.Select(record => record["id"].As<long>()).ToArray();
            await transaction.CommitAsync();
            return ids;
        }
    }

    private async Task<Version> GetAgeVersionAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await this.Graph.GetTransactionAsync(cancellationToken);
        var native = (AgeGraphTransaction)transaction;
        var values = await native.Runner.QueryScalarStringsAsync(
            "SELECT extversion FROM pg_extension WHERE extname = 'age'",
            queryParameter: null,
            cancellationToken);
        await transaction.RollbackAsync();
        return Version.Parse(values.Single());
    }

    private async Task<long[]> SeedAsync(CancellationToken cancellationToken)
    {
        var marker = "native-id-exec-" + Guid.NewGuid().ToString("N");
        await this.Graph.CreateNodeAsync(
            new Person { FirstName = "active", LastName = marker },
            transaction: null,
            cancellationToken);
        await this.Graph.CreateNodeAsync(
            new Person { FirstName = "inactive1", LastName = marker },
            transaction: null,
            cancellationToken);
        await this.Graph.CreateNodeAsync(
            new Person { FirstName = "inactive2", LastName = marker },
            transaction: null,
            cancellationToken);

        await using var transaction = await this.Graph.GetTransactionAsync(cancellationToken);
        await using var result = await ((AgeGraphTransaction)transaction).Runner.RunAsync(
            "MATCH (n:Person) WHERE n.LastName = $marker " +
            "RETURN id(n) AS id ORDER BY n.FirstName",
            new { marker },
            cancellationToken);
        var records = await result.ToListAsync(cancellationToken);
        await transaction.RollbackAsync();
        return records.Select(record => record["id"].As<long>()).ToArray();
    }

    private async Task<List<AgeRecord>> ExecuteAsync(
        CypherStatement statement,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(statement, new AgeNativeIdListPass(), cancellationToken);

    private async Task<List<AgeRecord>> ExecuteAsync(
        CypherStatement statement,
        AgeNativeIdListPass pass,
        CancellationToken cancellationToken)
    {
        var rewritten = pass.Run(statement);
        var rendered = new CypherRenderer(AgeDialect.Instance).Render(rewritten);
        await using var transaction = await this.Graph.GetTransactionAsync(cancellationToken);
        await using var result = await ((AgeGraphTransaction)transaction).Runner.RunAsync(
            rendered.Text,
            rendered.Parameters,
            rendered.ProjectionColumns,
            cancellationToken);
        var records = await result.ToListAsync(cancellationToken);
        await transaction.RollbackAsync();
        return records;
    }

    private static CypherStatement Statement(
        IReadOnlyList<PathPattern> patterns,
        CypherExpression predicate,
        IReadOnlyList<ReturnItem> returnItems,
        IReadOnlyDictionary<string, object?> parameters,
        bool optional = false) =>
        new(
            [
                new MatchClause(patterns, optional),
                new WhereClause(predicate),
                new ReturnClause(returnItems, distinct: false),
            ],
            parameters);

    private static BinaryExpression NativeMembership(string alias) =>
        new(
            CypherBinaryOperator.In,
            new NativeElementIdentity(new VariableRef(alias)),
            new QueryParameter("ids"));
}
