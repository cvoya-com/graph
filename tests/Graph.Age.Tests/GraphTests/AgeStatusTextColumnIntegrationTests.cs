// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph.Age;
using Cvoya.Graph.Age.Schema;
using Npgsql;
using Npgsql.Age;

/// <summary>
/// Covers the provider-owned generalized companion table for a label whose property is literally
/// named <c>Status</c>: migration and backfill, fast-path grouping/distinct/filter, provider
/// write-path consistency on update and null assignment, delete cleanup, revert, and re-apply.
/// </summary>
public sealed class AgeStatusTextColumnIntegrationTests(AgeGraphCleanupFixture graphCleanup)
{
    private const string Label = "Nul70StatusWidget";
    private static readonly string StatusTable = AgePhysicalName.IndexTable(Label);
    private static readonly string StatusColumn = AgePhysicalName.TextColumn(Label, "Status");
    private static readonly string StatusIndex = AgePhysicalName.TextIndex(Label, "Status");

    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    [Fact]
    public async Task ApplyFastPathUpdateDeleteAndRevertRoundTrip()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_status_generalized");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new StatusWidget { Name = "a", Status = "aktiv" },
            cancellationToken: cancellationToken);
        await store.Graph.CreateNodeAsync(
            new StatusWidget { Name = "b", Status = "inaktiv" },
            cancellationToken: cancellationToken);
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, StatusTable, cancellationToken));

        var applied = await store.EnsureIndexesAsync(cancellationToken);
        Assert.Equal(1, applied.DefinitionsInspected);
        Assert.True(applied.StructuresCreated >= 2);
        Assert.True(applied.RowsBackfilled >= 2);
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, StatusTable, cancellationToken));
        Assert.Equal(1, await IndexCountAsync(dataSource, graphName, cancellationToken));
        Assert.Equal(2, await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM {graphName}.\"{StatusTable}\"",
            cancellationToken));
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, "__cvoya_index", cancellationToken));
        Assert.True(await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM {graphName}.\"__cvoya_index\"",
            cancellationToken) >= 1);

        var reappliedWithoutChanges = await store.EnsureIndexesAsync(cancellationToken);
        Assert.Equal(0, reappliedWithoutChanges.StructuresCreated);
        Assert.True(reappliedWithoutChanges.RowsBackfilled >= 2);

        var groups = await store.Graph.Nodes<StatusWidget>()
            .GroupBy(widget => widget.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        Assert.Equal("aktiv:1,inaktiv:1", JoinGroups(groups));

        var distinct = await store.Graph.Nodes<StatusWidget>()
            .Select(widget => widget.Status)
            .Distinct()
            .ToListAsync(cancellationToken);
        Assert.Equal(["aktiv", "inaktiv"], distinct.OrderBy(value => value, StringComparer.Ordinal));

        var filtered = await store.Graph.Nodes<StatusWidget>()
            .Where(widget => widget.Status == "aktiv")
            .ToListAsync(cancellationToken);
        Assert.Single(filtered);

        var updated = await GraphCommandExtensions.UpdateAsync(
            store.Graph.Nodes<StatusWidget>().Where(widget => widget.Name == "a"),
            setters => setters.SetProperty(widget => widget.Status, "inaktiv"),
            cancellationToken);
        Assert.Equal(1, updated);
        Assert.Equal(2, await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM {graphName}.\"{StatusTable}\" WHERE \"{StatusColumn}\" = 'inaktiv'",
            cancellationToken));

        var cleared = await GraphCommandExtensions.UpdateAsync(
            store.Graph.Nodes<StatusWidget>().Where(widget => widget.Name == "a"),
            setters => setters.SetProperty(widget => widget.Status, (string?)null),
            cancellationToken);
        Assert.Equal(1, cleared);
        Assert.Equal(1, await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM {graphName}.\"{StatusTable}\" WHERE \"{StatusColumn}\" IS NULL",
            cancellationToken));

        var deleted = await GraphCommandExtensions.DeleteAsync(
            store.Graph.Nodes<StatusWidget>().Where(widget => widget.Name == "b"),
            cancellationToken: cancellationToken);
        Assert.Equal(1, deleted);
        Assert.Equal(1, await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM {graphName}.\"{StatusTable}\"",
            cancellationToken));

        var reverted = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(1, reverted.DefinitionsInspected);
        Assert.True(reverted.StructuresRemoved >= 1);
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, StatusTable, cancellationToken));
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, "__cvoya_index", cancellationToken));

        var revertedAgain = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(1, revertedAgain.DefinitionsInspected);
        Assert.Equal(0, revertedAgain.StructuresRemoved);

        var fallback = await store.Graph.Nodes<StatusWidget>()
            .Where(widget => widget.Name == "a")
            .SingleAsync(cancellationToken);
        Assert.Null(fallback.Status);

        var reapplied = await store.EnsureIndexesAsync(cancellationToken);
        Assert.True(reapplied.StructuresCreated >= 1);
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, StatusTable, cancellationToken));
        Assert.Equal(1, await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM {graphName}.\"{StatusTable}\"",
            cancellationToken));
        Assert.Equal(" :1", JoinGroups(await store.Graph.Nodes<StatusWidget>()
            .GroupBy(widget => widget.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)));

        await store.Graph.CreateNodeAsync(
            new StatusWidget { Name = "c", Status = "neu" },
            cancellationToken: cancellationToken);
        Assert.Equal(1, await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM {graphName}.\"{StatusTable}\" WHERE \"{StatusColumn}\" = 'neu'",
            cancellationToken));
    }

    [Fact]
    public async Task StatusNotEqualExcludesNodesWithoutStatusAndMatchesAgePath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_status_not_equal");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new StatusWidget { Name = "a", Status = "aktiv" },
            cancellationToken: cancellationToken);
        await store.Graph.CreateNodeAsync(
            new StatusWidget { Name = "b", Status = "inaktiv" },
            cancellationToken: cancellationToken);
        await store.Graph.CreateNodeAsync(
            new StatusWidget { Name = "missing", Status = null },
            cancellationToken: cancellationToken);

        // Before the companion table exists this runs the AGE Cypher path; `<>` must exclude the
        // node whose Status property is missing.
        var agePath = await store.Graph.Nodes<StatusWidget>()
            .Where(widget => widget.Status != "aktiv")
            .Select(widget => widget.Name)
            .ToListAsync(cancellationToken);
        Assert.Equal(["b"], agePath.OrderBy(name => name, StringComparer.Ordinal).ToArray());

        _ = await store.EnsureIndexesAsync(cancellationToken);

        var planPath = await store.Graph.Nodes<StatusWidget>()
            .Where(widget => widget.Status != "aktiv")
            .Select(widget => widget.Name)
            .ToListAsync(cancellationToken);
        Assert.Equal(
            agePath.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            planPath.OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("missing", planPath);

        // The unguarded `IS DISTINCT FROM` would count the node without Status as a second row.
        Assert.Equal(2, await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM {graphName}.\"{StatusTable}\" WHERE \"{StatusColumn}\" IS DISTINCT FROM 'aktiv'",
            cancellationToken));
    }

    private static SchemaRegistry CreateRegistry()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(Label, "Status"));
        return registry;
    }

    private static string JoinGroups(IEnumerable<dynamic> groups) =>
        string.Join(",", groups.Select(group => $"{(group.Key is null ? " " : group.Key)}:{group.Count}"));

    private static NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseAge();
        return builder.Build();
    }

    private static async Task<long> TableCountAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        string tableName,
        CancellationToken cancellationToken) =>
        await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{graphName}' " +
            $"AND table_name = '{tableName}'",
            cancellationToken);

    private static async Task<long> IndexCountAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        CancellationToken cancellationToken) =>
        await ScalarAsync(
            dataSource,
            $"SELECT count(*) FROM pg_indexes WHERE schemaname = '{graphName}' " +
            $"AND tablename = '{StatusTable}' AND indexname = '{StatusIndex}'",
            cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test helper prepends a fixed LOAD/SET preamble to test-controlled SQL; no user input.")]
    private static async Task<long> ScalarAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var connectionLease = connection.ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public; " + sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [Node(Label)]
    public record StatusWidget : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? Status { get; set; }
    }
}
