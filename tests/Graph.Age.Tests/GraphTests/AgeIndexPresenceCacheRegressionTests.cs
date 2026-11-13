// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using System.Globalization;
using Cvoya.Graph.Age;
using Cvoya.Graph.Age.Schema;
using Npgsql;
using Npgsql.Age;

/// <summary>
/// Defines a non-parallel collection for tests that observe the process-wide presence-cache
/// generation. Another test's maintenance call would otherwise clear the cache mid-test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgeIndexPresenceCacheIsolation
{
    /// <summary>The collection name shared by the cache-window tests.</summary>
    public const string Name = "AgeIndexPresenceCacheIsolation";
}

/// <summary>
/// Regression coverage for the stale-negative presence cache. The recreate step calls the
/// maintenance routine directly on a raw connection so it bypasses the store's process-wide
/// generation bump, exactly like another process's <c>EnsureIndexesAsync</c>.
/// </summary>
[Collection(AgeIndexPresenceCacheIsolation.Name)]
public sealed class AgeIndexPresenceCacheRegressionTests(AgeGraphCleanupFixture graphCleanup)
{
    private const string Label = "Nul112CacheWidget";
    private const string GroupKey = "Region";

    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    [Fact]
    public async Task CrossProcessReEnsureDoesNotLeaveTheCompanionTableIncomplete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_negcache_regression");
        var registry = CreateRegistry();
        await using var store = new AgeGraphStore(dataSource, graphName, registry);
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new CacheWidget { Name = "node-1", Region = "eu", Amount = 10 },
            cancellationToken: cancellationToken);
        _ = await store.EnsureIndexesAsync(cancellationToken);

        // A write after ensure caches a positive answer for the companion table.
        await store.Graph.CreateNodeAsync(
            new CacheWidget { Name = "node-2", Region = "eu", Amount = 20 },
            cancellationToken: cancellationToken);
        Assert.True(store.IndexPresenceCache.TryGet(
            graphName, store.IndexCatalog.Version, [AgePhysicalName.IndexTable(Label)], out var cachedPresent));
        Assert.True(cachedPresent);

        // A separate process drops the structures. The direct maintenance call deliberately skips
        // AgeGraphStore, so it does not bump this process's cache generation.
        await RunMaintenanceAsync(
            dataSource, graphName, registry, drop: true, cancellationToken);
        Assert.Equal(0, await ScalarAsync(
            dataSource,
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = @graph AND table_name = @table",
            cancellationToken,
            ("graph", graphName),
            ("table", AgePhysicalName.IndexTable(Label))));

        // Wait out the stale positive, then write while the table is absent. The old cache stored
        // this negative answer for five seconds; the fixed cache never stores it.
        await Task.Delay(TimeSpan.FromMilliseconds(5_200), cancellationToken);
        await store.Graph.CreateNodeAsync(
            new CacheWidget { Name = "node-skip", Region = "eu", Amount = 30 },
            cancellationToken: cancellationToken);

        Assert.False(store.IndexPresenceCache.TryGet(
            graphName, store.IndexCatalog.Version, [AgePhysicalName.IndexTable(Label)], out cachedPresent));
        Assert.False(cachedPresent);

        // The other process re-ensures and backfills both existing label rows.
        var ensure = await RunMaintenanceAsync(
            dataSource, graphName, registry, drop: false, cancellationToken);
        Assert.True(ensure.RowsBackfilled >= 2);

        await store.Graph.CreateNodeAsync(
            new CacheWidget { Name = "node-3", Region = "eu", Amount = 40 },
            cancellationToken: cancellationToken);

        // The old negative answer would still be valid here and would skip the companion row.
        Assert.True(store.IndexPresenceCache.TryGet(
            graphName, store.IndexCatalog.Version, [AgePhysicalName.IndexTable(Label)], out cachedPresent));
        Assert.True(cachedPresent);

        // Wait past the old TTL as well, then verify the durable rows, not just a fresh probe.
        await Task.Delay(TimeSpan.FromMilliseconds(5_200), cancellationToken);

        var labelRows = await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM {AgeIndexTable.QuoteIdentifier(graphName, "graph name")}." +
            $"{AgeIndexTable.QuoteIdentifier(Label, "label")}",
            cancellationToken);
        var companionRows = await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM {AgeIndexTable.QuoteIdentifier(graphName, "graph name")}." +
            $"{AgeIndexTable.QuoteIdentifier(AgePhysicalName.IndexTable(Label), "table")}",
            cancellationToken);
        var aggregateCount = await ScalarAsync(
            dataSource,
            $"SELECT cnt FROM {AgeIndexTable.QuoteIdentifier(graphName, "graph name")}." +
            $"{AgeIndexTable.QuoteIdentifier(AgePhysicalName.AggregateTable(Label, GroupKey, "Amount"), "table")} " +
            "WHERE group_text = 'eu'",
            cancellationToken);

        Assert.Equal(4, labelRows);
        Assert.Equal(labelRows, companionRows);
        Assert.Equal(4, aggregateCount);

        var groups = await store.Graph.Nodes<CacheWidget>()
            .GroupBy(widget => widget.Region)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var row = Assert.Single(groups);
        Assert.Equal("eu", row.Key);
        Assert.Equal(4, row.Count);
    }

    private static SchemaRegistry CreateRegistry()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(Label, GroupKey));
        registry.AddPropertyAggregate(new PropertyAggregateDefinition(
            Label,
            GroupKey,
            "Amount",
            AggregateFunction.Count));
        return registry;
    }

    private static AgeIndexCatalog Catalog(SchemaRegistry registry) => AgeIndexCatalog.From(registry);

    private static async Task<GraphIndexMaintenanceReport> RunMaintenanceAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        SchemaRegistry registry,
        bool drop,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var connectionLease = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var transactionLease = transaction.ConfigureAwait(false);
        var catalog = Catalog(registry);
        var report = drop
            ? await AgeIndexMaintenance.DropAsync(connection, transaction, graphName, catalog, cancellationToken)
            : await AgeIndexMaintenance.EnsureAsync(connection, transaction, graphName, catalog, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return report;
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseAge();
        return builder.Build();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test helper composes SQL from provider-quoted identifiers and bound parameters only.")]
    private static async Task<long> ScalarAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var connectionLease = connection.ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    [Node(Label)]
    public sealed record CacheWidget : Node
    {
        public string Name { get; set; } = string.Empty;

        [Property(Label = GroupKey)]
        public string? Region { get; set; }

        public long Amount { get; set; }
    }
}
