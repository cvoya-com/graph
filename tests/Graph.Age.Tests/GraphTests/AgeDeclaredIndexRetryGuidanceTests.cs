// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph;
using Cvoya.Graph.Age;
using Cvoya.Graph.Age.Schema;
using Npgsql;
using Npgsql.Age;

/// <summary>
/// Verifies that the documented atomic drop window reports the retry guidance inside a
/// <see cref="GraphException"/> instead of leaking the raw Npgsql missing-relation error.
/// </summary>
[Collection(AgeIndexPresenceCacheIsolation.Name)]
public sealed class AgeDeclaredIndexRetryGuidanceTests(AgeGraphCleanupFixture graphCleanup)
{
    private const string Label = "Nul112RetryWidget";

    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    [Fact]
    public async Task CreateAfterExternalDropReportsTheRetryGuidance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_retry_create");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new RetryWidget { Name = "a", Region = "eu", Amount = 10 },
            cancellationToken: cancellationToken);
        _ = await store.EnsureIndexesAsync(cancellationToken);

        // A write after ensure stores a positive presence answer, so the next write reaches the
        // INSERT without re-probing. The raw drop models another instance dropping the structures.
        await store.Graph.CreateNodeAsync(
            new RetryWidget { Name = "warm", Region = "eu", Amount = 20 },
            cancellationToken: cancellationToken);
        await DropIndexTableAsync(dataSource, graphName, cancellationToken);
        store.IndexPresenceCache.Set(
            graphName, store.IndexCatalog.Version, [AgePhysicalName.IndexTable(Label)], present: true);

        var exception = await Assert.ThrowsAsync<GraphException>(
            () => store.Graph.CreateNodeAsync(
                new RetryWidget { Name = "boom", Region = "eu", Amount = 30 },
                cancellationToken: cancellationToken));

        Assert.Equal(
            $"The declared index structures for label '{Label}' were dropped by another instance " +
            "within the presence-cache TTL. Retry the write, or run EnsureIndexesAsync to recreate them.",
            exception.Message);
        var inner = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UndefinedTable, inner.SqlState);
        Assert.Contains(AgePhysicalName.IndexTable(Label), inner.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAfterExternalDropReportsTheRetryGuidance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_retry_update");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new RetryWidget { Name = "a", Region = "eu", Amount = 10 },
            cancellationToken: cancellationToken);
        _ = await store.EnsureIndexesAsync(cancellationToken);

        // The first update after ensure caches the positive answer; the second one must fail.
        var updated = await GraphCommandExtensions.UpdateAsync(
            store.Graph.Nodes<RetryWidget>().Where(widget => widget.Name == "a"),
            setters => setters.SetProperty(widget => widget.Region, "us"),
            cancellationToken);
        Assert.Equal(1, updated);
        Assert.True(store.IndexPresenceCache.TryGet(
            graphName, store.IndexCatalog.Version, [AgePhysicalName.IndexTable(Label)], out var cachedPresent));
        Assert.True(cachedPresent);
        await DropIndexTableAsync(dataSource, graphName, cancellationToken);
        store.IndexPresenceCache.Set(
            graphName, store.IndexCatalog.Version, [AgePhysicalName.IndexTable(Label)], present: true);

        var exception = await Assert.ThrowsAsync<GraphException>(
            () => GraphCommandExtensions.UpdateAsync(
                store.Graph.Nodes<RetryWidget>().Where(widget => widget.Name == "a"),
                setters => setters.SetProperty(widget => widget.Region, "uk"),
                cancellationToken));

        Assert.Equal(
            $"The declared index structures for label '{Label}' were dropped by another instance " +
            "within the presence-cache TTL. Retry the write, or run EnsureIndexesAsync to recreate them.",
            exception.Message);
        var inner = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UndefinedTable, inner.SqlState);
        Assert.Contains(AgePhysicalName.IndexTable(Label), inner.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteAfterExternalDropReportsTheRetryGuidance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_retry_delete");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new RetryWidget { Name = "keep", Region = "eu", Amount = 10 },
            cancellationToken: cancellationToken);
        await store.Graph.CreateNodeAsync(
            new RetryWidget { Name = "gone", Region = "eu", Amount = 20 },
            cancellationToken: cancellationToken);
        _ = await store.EnsureIndexesAsync(cancellationToken);

        // The raw drop models another instance dropping the companion table. The positive cache
        // answer forces the delete path to reach the DELETE instead of skipping it.
        await DropIndexTableAsync(dataSource, graphName, cancellationToken);
        store.IndexPresenceCache.Set(
            graphName, store.IndexCatalog.Version, [AgePhysicalName.IndexTable(Label)], present: true);

        var exception = await Assert.ThrowsAsync<GraphException>(
            () => GraphCommandExtensions.DeleteAsync(
                store.Graph.Nodes<RetryWidget>().Where(widget => widget.Name == "gone"),
                cancellationToken: cancellationToken));

        Assert.Equal(
            $"The declared index structures for label '{Label}' were dropped by another instance " +
            "within the presence-cache TTL. Retry the write, or run EnsureIndexesAsync to recreate them.",
            exception.Message);
        var inner = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UndefinedTable, inner.SqlState);
        Assert.Contains(AgePhysicalName.IndexTable(Label), inner.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsertAfterExternalAggregateDropReportsTheRetryGuidance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_retry_aggregate_insert");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new RetryWidget { Name = "warm", Region = "eu", Amount = 10 },
            cancellationToken: cancellationToken);
        _ = await store.EnsureIndexesAsync(cancellationToken);

        // A write after ensure caches the companion as present; the missing aggregate table is
        // reached only through the companion insert trigger.
        await store.Graph.CreateNodeAsync(
            new RetryWidget { Name = "warm-2", Region = "eu", Amount = 20 },
            cancellationToken: cancellationToken);
        var aggregateTable = AgePhysicalName.AggregateTable(Label, "Region", "Amount");
        await DropTableAsync(dataSource, graphName, aggregateTable, cancellationToken);

        var exception = await Assert.ThrowsAsync<GraphException>(
            () => store.Graph.CreateNodeAsync(
                new RetryWidget { Name = "boom", Region = "eu", Amount = 30 },
                cancellationToken: cancellationToken));

        Assert.Equal(
            $"The declared index structures for label '{Label}' were dropped by another instance " +
            "within the presence-cache TTL. Retry the write, or run EnsureIndexesAsync to recreate them.",
            exception.Message);
        var inner = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UndefinedTable, inner.SqlState);
        Assert.Contains(aggregateTable, inner.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAfterExternalAggregateDropReportsTheRetryGuidance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_retry_aggregate_update");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new RetryWidget { Name = "a", Region = "eu", Amount = 10 },
            cancellationToken: cancellationToken);
        await store.Graph.CreateNodeAsync(
            new RetryWidget { Name = "b", Region = "eu", Amount = 20 },
            cancellationToken: cancellationToken);
        _ = await store.EnsureIndexesAsync(cancellationToken);

        var aggregateTable = AgePhysicalName.AggregateTable(Label, "Region", "Amount");
        await DropTableAsync(dataSource, graphName, aggregateTable, cancellationToken);

        var exception = await Assert.ThrowsAsync<GraphException>(
            () => GraphCommandExtensions.UpdateAsync(
                store.Graph.Nodes<RetryWidget>().Where(widget => widget.Name == "a"),
                setters => setters.SetProperty(widget => widget.Amount, 30L),
                cancellationToken));

        Assert.Equal(
            $"The declared index structures for label '{Label}' were dropped by another instance " +
            "within the presence-cache TTL. Retry the write, or run EnsureIndexesAsync to recreate them.",
            exception.Message);
        var inner = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UndefinedTable, inner.SqlState);
        Assert.Contains(aggregateTable, inner.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnrelatedMissingRelationIsNotRewrittenAsRetryGuidance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_retry_unrelated");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new RetryWidget { Name = "warm", Region = "eu", Amount = 10 },
            cancellationToken: cancellationToken);
        _ = await store.EnsureIndexesAsync(cancellationToken);

        // A write after ensure caches the companion as present, so the user trigger runs on the
        // next companion insert. It raises a 42P01 for a table the provider does not own.
        await store.Graph.CreateNodeAsync(
            new RetryWidget { Name = "warm-2", Region = "eu", Amount = 20 },
            cancellationToken: cancellationToken);
        await CreateUnrelatedInsertTriggerAsync(dataSource, graphName, cancellationToken);

        var exception = await Assert.ThrowsAsync<GraphException>(
            () => store.Graph.CreateNodeAsync(
                new RetryWidget { Name = "boom", Region = "eu", Amount = 30 },
                cancellationToken: cancellationToken));

        Assert.DoesNotContain("declared index structures", exception.Message, StringComparison.Ordinal);
        var inner = Assert.IsType<PostgresException>(FindPostgresException(exception));
        Assert.Equal(PostgresErrorCodes.UndefinedTable, inner.SqlState);
        Assert.Contains("Nul112MissingUserTable", inner.MessageText, StringComparison.Ordinal);
    }

    private static SchemaRegistry CreateRegistry()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(Label, "Region"));
        registry.AddPropertyAggregate(new PropertyAggregateDefinition(
            Label,
            "Region",
            "Amount",
            AggregateFunction.Count));
        return registry;
    }

    private static Task DropIndexTableAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        CancellationToken cancellationToken) =>
        DropTableAsync(dataSource, graphName, AgePhysicalName.IndexTable(Label), cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test helper composes the table name from provider-quoted identifiers only.")]
    private static async Task DropTableAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        string tableName,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var connectionLease = connection.ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText =
            $"DROP TABLE IF EXISTS {AgeIndexTable.QuoteIdentifier(graphName, "graph name")}." +
            $"{AgeIndexTable.QuoteIdentifier(tableName, "table")} CASCADE";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test helper composes SQL from provider-quoted identifiers and fixed trigger text only.")]
    private static async Task CreateUnrelatedInsertTriggerAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        CancellationToken cancellationToken)
    {
        const string FunctionName = "Nul112UnrelatedInsertFunction";
        const string TriggerName = "Nul112UnrelatedInsertTrigger";
        const string MissingTable = "Nul112MissingUserTable";

        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var connectionLease = connection.ConfigureAwait(false);

        var function = connection.CreateCommand();
        await using var functionLease = function.ConfigureAwait(false);
        function.CommandText =
            $"CREATE OR REPLACE FUNCTION {AgeIndexTable.QuoteIdentifier(graphName, "graph name")}." +
            $"{AgeIndexTable.QuoteIdentifier(FunctionName, "function")}() RETURNS trigger " +
            "LANGUAGE plpgsql AS $fn$ BEGIN " +
            $"INSERT INTO {AgeIndexTable.QuoteIdentifier(graphName, "graph name")}." +
            $"{AgeIndexTable.QuoteIdentifier(MissingTable, "table")} (id) VALUES (NEW.id); " +
            "RETURN NEW; END; $fn$";
        await function.ExecuteNonQueryAsync(cancellationToken);

        var trigger = connection.CreateCommand();
        await using var triggerLease = trigger.ConfigureAwait(false);
        trigger.CommandText =
            $"CREATE TRIGGER {AgeIndexTable.QuoteIdentifier(TriggerName, "trigger")} AFTER INSERT ON " +
            $"{AgeIndexTable.QuoteIdentifier(graphName, "graph name")}." +
            $"{AgeIndexTable.QuoteIdentifier(AgePhysicalName.IndexTable(Label), "table")} " +
            "FOR EACH ROW EXECUTE FUNCTION " +
            $"{AgeIndexTable.QuoteIdentifier(graphName, "graph name")}." +
            $"{AgeIndexTable.QuoteIdentifier(FunctionName, "function")}()";
        await trigger.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseAge();
        return builder.Build();
    }

    [Node(Label)]
    public sealed record RetryWidget : Node
    {
        public string Name { get; set; } = string.Empty;

        [Property(Label = "Region")]
        public string? Region { get; set; }

        public long Amount { get; set; }
    }
}
