// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using System.Globalization;
using Cvoya.Graph.Age;
using Cvoya.Graph.Age.Schema;
using Npgsql;
using Npgsql.Age;

/// <summary>
/// Covers the provider-owned generalized numeric aggregate companion table end to end for a label
/// whose properties are literally named <c>Status</c>/<c>Kaufpreis</c>: migration/backfill, create,
/// update of the numeric property, update of the status property, delete, revert, and re-apply.
/// Every fast-path result is compared with the ordinary AGE path.
/// </summary>
public sealed class AgeNumericAggregateIntegrationTests(AgeGraphCleanupFixture graphCleanup)
{
    private const string Label = "Nul72AggWidget";
    private static readonly string AggregateTable =
        AgePhysicalName.AggregateTable(Label, "Status", "Kaufpreis");

    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    [Fact]
    public async Task AggregateFastPathTracksCreateUpdateDeleteAndRevert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_num_agg");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await CreateAsync(store, "a", "aktiv", 100);
        await CreateAsync(store, "b", "aktiv", 200);
        await CreateAsync(store, "c", "inaktiv", 300);
        await CreateAsync(store, "d", null, 400);
        await CreateAsync(store, "e", null, 500);

        var agePath = await StatsAsync(store.Graph, cancellationToken);
        Assert.Equal("<null>:2:450:400:500;aktiv:2:150:100:200;inaktiv:1:300:300:300;", Format(agePath));

        _ = await store.EnsureIndexesAsync(cancellationToken);
        var fastPath = await StatsAsync(store.Graph, cancellationToken);
        Assert.Equal(Format(agePath), Format(fastPath));
        Assert.Equal(
            1,
            await ScalarAsync(
                dataSource,
                $"SELECT count(*) FROM {graphName}.\"{AggregateTable}\" " +
                $"WHERE \"{AgeAggregateTable.GroupTextColumn}\" = 'aktiv' AND cnt = 2 AND sum_value = 300 " +
                "AND min_value = 100 AND max_value = 200",
                cancellationToken));

        var updatedValue = await GraphCommandExtensions.UpdateAsync(
            store.Graph.Nodes<Nul72AggWidget>().Where(widget => widget.Name == "a"),
            setters => setters.SetProperty(widget => widget.Kaufpreis, 500L),
            cancellationToken);
        Assert.Equal(1, updatedValue);
        var afterValue = await StatsAsync(store.Graph, cancellationToken);
        Assert.Equal("<null>:2:450:400:500;aktiv:2:350:200:500;inaktiv:1:300:300:300;", Format(afterValue));

        var updatedStatus = await GraphCommandExtensions.UpdateAsync(
            store.Graph.Nodes<Nul72AggWidget>().Where(widget => widget.Name == "b"),
            setters => setters.SetProperty(widget => widget.Status, "inaktiv"),
            cancellationToken);
        Assert.Equal(1, updatedStatus);
        var afterStatus = await StatsAsync(store.Graph, cancellationToken);
        Assert.Equal("<null>:2:450:400:500;aktiv:1:500:500:500;inaktiv:2:250:200:300;", Format(afterStatus));

        var deleted = await GraphCommandExtensions.DeleteAsync(
            store.Graph.Nodes<Nul72AggWidget>().Where(widget => widget.Name == "c"),
            cancellationToken: cancellationToken);
        Assert.Equal(1, deleted);
        var afterDelete = await StatsAsync(store.Graph, cancellationToken);
        Assert.Equal("<null>:2:450:400:500;aktiv:1:500:500:500;inaktiv:1:200:200:200;", Format(afterDelete));

        _ = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(Format(afterDelete), Format(await StatsAsync(store.Graph, cancellationToken)));
        Assert.Equal(
            0,
            await ScalarAsync(
                dataSource,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{graphName}' " +
                $"AND table_name = '{AggregateTable}'",
                cancellationToken));

        _ = await store.EnsureIndexesAsync(cancellationToken);
        Assert.Equal(Format(afterDelete), Format(await StatsAsync(store.Graph, cancellationToken)));

        await CreateAsync(store, "f", "neu", 700);
        var afterCreate = await StatsAsync(store.Graph, cancellationToken);
        Assert.Equal("<null>:2:450:400:500;aktiv:1:500:500:500;inaktiv:1:200:200:200;neu:1:700:700:700;", Format(afterCreate));
    }

    [Fact]
    public async Task TypeSafeAggregateDeclarationExecutesGroupedAggregate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_num_agg_typed");
        var registry = new SchemaRegistry();
        registry.Node<Nul72AggWidget>()
            .AddPropertyIndex(widget => widget.Status)
            .AddPropertyAggregate(
                widget => widget.Status,
                widget => widget.Kaufpreis,
                AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
                AggregateFunction.Max | AggregateFunction.Average);

        await using var store = new AgeGraphStore(dataSource, graphName, registry);
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await CreateAsync(store, "a", "aktiv", 100);
        await CreateAsync(store, "b", "aktiv", 200);

        var agePath = await StatsAsync(store.Graph, cancellationToken);
        Assert.Equal("aktiv:2:150:100:200;", Format(agePath));

        _ = await store.EnsureIndexesAsync(cancellationToken);
        var fastPath = await StatsAsync(store.Graph, cancellationToken);
        Assert.Equal(Format(agePath), Format(fastPath));
        Assert.Equal(
            1,
            await ScalarAsync(
                dataSource,
                $"SELECT count(*) FROM {graphName}.\"{AggregateTable}\" " +
                $"WHERE \"{AgeAggregateTable.GroupTextColumn}\" = 'aktiv' AND cnt = 2 AND sum_value = 300 " +
                "AND min_value = 100 AND max_value = 200",
                cancellationToken));

        _ = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(Format(agePath), Format(await StatsAsync(store.Graph, cancellationToken)));
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
        AgeGraphStore store,
        string name,
        string? status,
        long kaufpreis) =>
        await store.Graph.CreateNodeAsync(
            new Nul72AggWidget { Name = name, Status = status, Kaufpreis = kaufpreis },
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<Dictionary<string, GroupStats>> StatsAsync(
        IGraph graph,
        CancellationToken cancellationToken)
    {
        var counts = await graph.Nodes<Nul72AggWidget>()
            .GroupBy(widget => widget.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var averages = await graph.Nodes<Nul72AggWidget>()
            .GroupBy(widget => widget.Status)
            .Select(group => new { group.Key, Value = group.Average(widget => widget.Kaufpreis) })
            .ToListAsync(cancellationToken);
        var minimums = await graph.Nodes<Nul72AggWidget>()
            .GroupBy(widget => widget.Status)
            .Select(group => new { group.Key, Value = group.Min(widget => widget.Kaufpreis) })
            .ToListAsync(cancellationToken);
        var maximums = await graph.Nodes<Nul72AggWidget>()
            .GroupBy(widget => widget.Status)
            .Select(group => new { group.Key, Value = group.Max(widget => widget.Kaufpreis) })
            .ToListAsync(cancellationToken);

        var stats = counts.ToDictionary(
            row => row.Key ?? "<null>",
            row => new GroupStats(row.Count, 0, long.MaxValue, long.MinValue),
            StringComparer.Ordinal);
        foreach (var row in averages)
        {
            var key = row.Key ?? "<null>";
            stats[key] = stats[key] with { Average = row.Value };
        }

        foreach (var row in minimums)
        {
            var key = row.Key ?? "<null>";
            stats[key] = stats[key] with { Min = row.Value };
        }

        foreach (var row in maximums)
        {
            var key = row.Key ?? "<null>";
            stats[key] = stats[key] with { Max = row.Value };
        }

        return stats;
    }

    private static string Format(Dictionary<string, GroupStats> stats) =>
        string.Concat(stats.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            $"{pair.Key}:{pair.Value.Count.ToString(CultureInfo.InvariantCulture)}:" +
            $"{pair.Value.Average.ToString("R", CultureInfo.InvariantCulture)}:" +
            $"{pair.Value.Min.ToString(CultureInfo.InvariantCulture)}:" +
            $"{pair.Value.Max.ToString(CultureInfo.InvariantCulture)};"));

    private static NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseAge();
        return builder.Build();
    }

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
            CultureInfo.InvariantCulture);
    }

    [Node(Label)]
    public record Nul72AggWidget : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? Status { get; set; }
        public long Kaufpreis { get; set; }
    }

    private sealed record GroupStats(long Count, double Average, long Min, long Max);
}
