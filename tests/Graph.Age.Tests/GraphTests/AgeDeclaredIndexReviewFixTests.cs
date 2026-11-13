// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using System.Globalization;
using Cvoya.Graph.Age;
using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Schema;
using Cvoya.Graph.CompatibilityTests;
using Npgsql;
using Npgsql.Age;

/// <summary>
/// Regression coverage for the review findings on the declared-index write paths: concurrent group
/// rebuilds must serialize before reading their aggregate inputs, a null group key must stay
/// distinct from every valid string, and subgraph creation must synchronize the declared index rows
/// of its new endpoints before the transient correlation tokens are removed. Every fast-path
/// result is compared with the ordinary AGE path on the same graph.
/// </summary>
public sealed class AgeDeclaredIndexReviewFixTests(AgeGraphCleanupFixture graphCleanup)
{
    private const string Label = "Nul99ReviewWidget";
    private static readonly string AggregateTable =
        AgePhysicalName.AggregateTable(Label, "Status", "Kaufpreis");
    private static readonly string IndexTable = AgePhysicalName.IndexTable(Label);

    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    private static readonly TimeSpan BlockedWaitTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConcurrentGroupUpdates_SerializeRebuildsBeforeReadingAggregateInputs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_review_concurrent_update");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await CreateAsync(baseline.Graph, "a", "g", 10, cancellationToken);
        await CreateAsync(baseline.Graph, "b", "g", 20, cancellationToken);
        _ = await indexed.EnsureIndexesAsync(cancellationToken);

        // The winner updates the first row of group "g" without committing, so it holds the
        // group's rebuild lock; the loser updates the second row and must wait for that lock
        // before it reads the aggregate inputs.
        await using var winner = await indexed.Graph.GetTransactionAsync(cancellationToken);
        var winnerWrite = await indexed.Graph.Nodes<Nul99ReviewWidget>(winner)
            .Where(widget => widget.Name == "a")
            .UpdateAsync(
                setters => setters.SetProperty(widget => widget.Kaufpreis, 100L),
                cancellationToken);
        Assert.Equal(1, winnerWrite);
        var contended = await HeldAdvisoryLocksAsync(winner, cancellationToken);
        Assert.NotEmpty(contended);

        await using var loser = await indexed.Graph.GetTransactionAsync(cancellationToken);
        var loserWrite = Task.Run(
            () => indexed.Graph.Nodes<Nul99ReviewWidget>(loser)
                .Where(widget => widget.Name == "b")
                .UpdateAsync(
                    setters => setters.SetProperty(widget => widget.Kaufpreis, 200L),
                    cancellationToken),
            cancellationToken);
        await WaitUntilWaitingForAsync(indexed, contended, cancellationToken);
        await winner.CommitAsync();
        Assert.Equal(1, await loserWrite);
        await loser.CommitAsync();

        // Both writes committed, so the declared aggregate must total 100 + 200 = 300 and match
        // the ordinary AGE path; a rebuild that read its inputs before the winner committed
        // would retain the stale total 210.
        Assert.Equal(150, await AverageAsync(baseline.Graph, "g", cancellationToken));
        Assert.Equal(150, await AverageAsync(indexed.Graph, "g", cancellationToken));
        Assert.Equal(
            300,
            await ScalarAsync(
                dataSource,
                $"SELECT sum_value FROM {graphName}.\"{AggregateTable}\" " +
                $"WHERE \"{AgeAggregateTable.GroupTextColumn}\" = 'g'",
                cancellationToken));
    }

    [Fact]
    public async Task ConcurrentGroupUpdateAndDelete_SerializeRebuildsBeforeReadingAggregateInputs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_review_concurrent_delete");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await CreateAsync(baseline.Graph, "a", "g", 10, cancellationToken);
        await CreateAsync(baseline.Graph, "b", "g", 20, cancellationToken);
        await CreateAsync(baseline.Graph, "c", "g", 30, cancellationToken);
        _ = await indexed.EnsureIndexesAsync(cancellationToken);

        await using var winner = await indexed.Graph.GetTransactionAsync(cancellationToken);
        var winnerWrite = await indexed.Graph.Nodes<Nul99ReviewWidget>(winner)
            .Where(widget => widget.Name == "a")
            .UpdateAsync(
                setters => setters.SetProperty(widget => widget.Kaufpreis, 100L),
                cancellationToken);
        Assert.Equal(1, winnerWrite);
        var contended = await HeldAdvisoryLocksAsync(winner, cancellationToken);
        Assert.NotEmpty(contended);

        await using var loser = await indexed.Graph.GetTransactionAsync(cancellationToken);
        var loserWrite = Task.Run(
            () => indexed.Graph.Nodes<Nul99ReviewWidget>(loser)
                .Where(widget => widget.Name == "c")
                .DeleteAsync(cancellationToken: cancellationToken),
            cancellationToken);
        await WaitUntilWaitingForAsync(indexed, contended, cancellationToken);
        await winner.CommitAsync();
        Assert.Equal(1, await loserWrite);
        await loser.CommitAsync();

        // The delete rebuild must observe the winner's committed update: 100 + 20 remain.
        Assert.Equal(2, await CountInGroupAsync(baseline.Graph, "g", cancellationToken));
        Assert.Equal(2, await CountInGroupAsync(indexed.Graph, "g", cancellationToken));
        Assert.Equal(60, await AverageAsync(baseline.Graph, "g", cancellationToken));
        Assert.Equal(60, await AverageAsync(indexed.Graph, "g", cancellationToken));
        Assert.Equal(
            "2:120",
            await ScalarStringAsync(
                dataSource,
                $"SELECT cnt::text || ':' || sum_value::text FROM {graphName}.\"{AggregateTable}\" " +
                $"WHERE \"{AgeAggregateTable.GroupTextColumn}\" = 'g'",
                cancellationToken));
    }

    [Fact]
    public async Task NullGroupAndControlCharacterGroup_StayDistinctOnBackfillAndLaterWrites()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_rvw_nullgroup");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        const string sentinelLookalike = "\u0001";
        await CreateAsync(baseline.Graph, "null-a", null, 100, cancellationToken);
        await CreateAsync(baseline.Graph, "ctrl-a", sentinelLookalike, 200, cancellationToken);
        await CreateAsync(baseline.Graph, "null-b", null, 300, cancellationToken);

        // The backfill must not merge the null group with the real "\u0001" group (SQLSTATE 21000).
        _ = await indexed.EnsureIndexesAsync(cancellationToken);

        var expected = await GroupedCountsAsync(baseline.Graph, cancellationToken);
        Assert.Equal(2, expected.Count);
        Assert.Equal(expected, await GroupedCountsAsync(indexed.Graph, cancellationToken));
        Assert.Equal(
            2,
            await ScalarAsync(
                dataSource,
                $"SELECT count(*) FROM {graphName}.\"{AggregateTable}\"",
                cancellationToken));
        Assert.Equal(
            "null",
            await ScalarStringAsync(
                dataSource,
                $"SELECT \"{AgeAggregateTable.GroupKeyColumn}\" FROM {graphName}.\"{AggregateTable}\" " +
                $"WHERE \"{AgeAggregateTable.GroupTextColumn}\" IS NULL",
                cancellationToken));
        Assert.Equal(
            1,
            await ScalarAsync(
                dataSource,
                $"SELECT count(*) FROM {graphName}.\"{AggregateTable}\" " +
                $"WHERE \"{AgeAggregateTable.GroupTextColumn}\" = chr(1) " +
                $"AND \"{AgeAggregateTable.GroupKeyColumn}\" <> 'null'",
                cancellationToken));

        // A subsequent write through the insert trigger must keep the two groups distinct too.
        await CreateAsync(indexed.Graph, "ctrl-b", sentinelLookalike, 400, cancellationToken);

        expected = await GroupedCountsAsync(baseline.Graph, cancellationToken);
        Assert.Equal(expected, await GroupedCountsAsync(indexed.Graph, cancellationToken));
        Assert.Equal(2, expected.Count);
        Assert.Equal(2L, expected[sentinelLookalike]);
        Assert.Equal(2L, expected["<null>"]);
        Assert.Equal(
            2,
            await ScalarAsync(
                dataSource,
                $"SELECT count(DISTINCT \"{AgeAggregateTable.GroupKeyColumn}\") FROM {graphName}.\"{AggregateTable}\"",
                cancellationToken));
    }

    [Fact]
    public async Task SubgraphCreateWithNewEndpoints_SynchronizesDeclaredIndexRowsBeforeCleanup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_review_subgraph_sync");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await CreateAsync(baseline.Graph, "seed", "seed", 1, cancellationToken);
        _ = await indexed.EnsureIndexesAsync(cancellationToken);

        // Two new endpoints in one batched subgraph create.
        var source = new Nul99ReviewWidget { Name = "s1", Status = "other", Kaufpreis = 10 };
        var target = new Nul99ReviewWidget { Name = "t1", Status = "other", Kaufpreis = 20 };
        await indexed.Graph.CreateAsync(source, new Knows(), target, cancellationToken: cancellationToken);

        // One existing and one new endpoint in a hybrid create.
        var hybrid = new Nul99ReviewWidget { Name = "h1", Status = "other", Kaufpreis = 30 };
        await indexed.Graph.CreateAsync(
            indexed.Graph.Nodes<Nul99ReviewWidget>().Where(widget => widget.Name == "seed"),
            new Knows(),
            hybrid,
            cancellationToken: cancellationToken);

        // The declared equality path must see the rows the batch created; re-matching the
        // correlation token after the cleanup command would find zero rows and skip them.
        var baselineNegated = await baseline.Graph.Nodes<Nul99ReviewWidget>()
            .Where(widget => widget.Status != "seed")
            .CountAsync(cancellationToken);
        var indexedNegated = await indexed.Graph.Nodes<Nul99ReviewWidget>()
            .Where(widget => widget.Status != "seed")
            .CountAsync(cancellationToken);
        Assert.Equal(3, baselineNegated);
        Assert.Equal(baselineNegated, indexedNegated);

        Assert.Equal(
            await GroupedCountsAsync(baseline.Graph, cancellationToken),
            await GroupedCountsAsync(indexed.Graph, cancellationToken));
        Assert.Equal(20, await AverageAsync(baseline.Graph, "other", cancellationToken));
        Assert.Equal(20, await AverageAsync(indexed.Graph, "other", cancellationToken));
        Assert.Equal(
            4,
            await ScalarAsync(
                dataSource,
                $"SELECT count(*) FROM {graphName}.\"{IndexTable}\"",
                cancellationToken));
    }

    private static SchemaRegistry CreateRegistry()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(Label, "Status"));
        registry.AddPropertyAggregate(new PropertyAggregateDefinition(
            Label,
            "Status",
            "Kaufpreis",
            AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
            AggregateFunction.Max | AggregateFunction.Average));
        return registry;
    }

    private static async Task CreateAsync(
        IGraph graph,
        string name,
        string? status,
        long kaufpreis,
        CancellationToken cancellationToken) =>
        await graph.CreateNodeAsync(
            new Nul99ReviewWidget { Name = name, Status = status, Kaufpreis = kaufpreis },
            cancellationToken: cancellationToken);

    private static async Task<Dictionary<string, long>> GroupedCountsAsync(
        IGraph graph,
        CancellationToken cancellationToken)
    {
        var rows = await graph.Nodes<Nul99ReviewWidget>()
            .GroupBy(widget => widget.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(
            row => row.Key ?? "<null>",
            row => (long)row.Count,
            StringComparer.Ordinal);
    }

    private static async Task<long> CountInGroupAsync(
        IGraph graph,
        string status,
        CancellationToken cancellationToken) =>
        await graph.Nodes<Nul99ReviewWidget>()
            .Where(widget => widget.Status == status)
            .CountAsync(cancellationToken);

    private static async Task<double> AverageAsync(
        IGraph graph,
        string status,
        CancellationToken cancellationToken)
    {
        var rows = await graph.Nodes<Nul99ReviewWidget>()
            .GroupBy(widget => widget.Status)
            .Select(group => new { group.Key, Value = group.Average(widget => widget.Kaufpreis) })
            .ToListAsync(cancellationToken);
        return rows.Single(row => row.Key == status).Value;
    }

    /// <summary>
    /// Returns the advisory locks currently granted to <paramref name="transaction"/>'s own backend,
    /// rendered as the <c>classid:objid</c> pair PostgreSQL splits a bigint lock key into.
    /// </summary>
    private static async Task<IReadOnlyList<string>> HeldAdvisoryLocksAsync(
        IGraphTransaction transaction,
        CancellationToken cancellationToken) =>
        await ((AgeGraphTransaction)transaction).Runner.QueryScalarStringsAsync(
            """
            SELECT classid::text || ':' || objid::text FROM pg_locks
            WHERE locktype = 'advisory' AND granted AND objsubid = 1 AND pid = pg_backend_pid()
            """,
            null,
            cancellationToken);

    /// <summary>
    /// Blocks until some backend is waiting on one of <paramref name="contended"/>. Matching the
    /// winner's exact keys keeps unrelated advisory waits elsewhere in the database - other test
    /// classes share the container - from satisfying the wait.
    /// </summary>
    private static async Task WaitUntilWaitingForAsync(
        AgeGraphStore store,
        IReadOnlyList<string> contended,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + BlockedWaitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var observer = await store.Graph.GetTransactionAsync(cancellationToken);
            var waiting = await ((AgeGraphTransaction)observer).Runner.QueryScalarStringsAsync(
                """
                SELECT classid::text || ':' || objid::text FROM pg_locks
                WHERE locktype = 'advisory' AND NOT granted AND objsubid = 1
                """,
                null,
                cancellationToken);
            await observer.RollbackAsync();

            if (waiting.Intersect(contended, StringComparer.Ordinal).Any())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        Assert.Fail(
            "No backend ever waited on the group rebuild locks the first transaction holds " +
            $"({string.Join(", ", contended)}), so the rebuild inputs are not serialized " +
            "against a competing transaction.");
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseAge();
        return builder.Build();
    }

    private static async Task<long> ScalarAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        var value = await ExecuteScalarAsync(dataSource, sql, cancellationToken);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken) =>
        (string)(await ExecuteScalarAsync(dataSource, sql, cancellationToken))!;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test helper prepends a fixed LOAD/SET preamble to test-controlled SQL; no user input.")]
    private static async Task<object?> ExecuteScalarAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var connectionLease = connection.ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public; " + sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    [Node(Label)]
    public record Nul99ReviewWidget : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? Status { get; set; }
        public long Kaufpreis { get; set; }
    }
}
