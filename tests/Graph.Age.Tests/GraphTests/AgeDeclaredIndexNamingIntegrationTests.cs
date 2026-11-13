// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using System.Globalization;
using Cvoya.Graph.Age;
using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.Age.Schema;
using Npgsql;
using Npgsql.Age;

/// <summary>
/// End-to-end coverage for declarations that use mapped property names. It proves the generalized
/// <c>__idx</c> table, aggregate table, filter, group, and average plans all execute, not just that
/// their SQL contains expected substrings.
/// </summary>
public sealed class AgeDeclaredIndexNamingIntegrationTests(AgeGraphCleanupFixture graphCleanup)
{
    private const string Label = "Nul91NamingWidget";
    private const string ChildLabel = "Nul91NamingWidget_Kauf";

    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    static AgeDeclaredIndexNamingIntegrationTests()
    {
        AgeTypeHierarchyRegistry.Instance.Register(Label, ChildLabel);
    }

    [Fact]
    public async Task ArbitraryDeclarationsUseTheGeneralizedTablesAndPlans()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_generalized_naming");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new NamingWidget { Name = "a", Region = "EU", Amount = 100 },
            cancellationToken: cancellationToken);
        await store.Graph.CreateNodeAsync(
            new NamingWidget { Name = "b", Region = "US", Amount = 200 },
            cancellationToken: cancellationToken);
        await store.Graph.CreateNodeAsync(
            new NamingWidgetChild { Name = "c", Region = "EU", Amount = 300 },
            cancellationToken: cancellationToken);

        var report = await store.EnsureIndexesAsync(cancellationToken);
        Assert.True(report.StructuresCreated >= 4);
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, Label + "__idx", cancellationToken));
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, ChildLabel + "__idx", cancellationToken));
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, Label + "__status", cancellationToken));

        var groupedExpression = store.Graph.Nodes<NamingWidget>()
            .GroupBy(widget => widget.Region)
            .Select(group => new { group.Key, Count = group.Count() })
            .Expression;
        var groupedVisitor = new CypherQueryVisitor(
            typeof(NamingWidget),
            hasComplexProperties: true,
            indexCatalog: store.IndexCatalog);
        groupedVisitor.Visit(groupedExpression);
        Assert.Equal("group", Assert.IsType<AgeSqlPlan>(groupedVisitor.Query.Plan).Kind);

        var grouped = await store.Graph.Nodes<NamingWidget>()
            .GroupBy(widget => widget.Region)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var groupRow = Assert.Single(grouped, row => row.Key == "EU");
        Assert.Equal(2, groupRow.Count);

        var averageExpression = store.Graph.Nodes<NamingWidget>()
            .Where(widget => widget.Region == "EU")
            .GroupBy(widget => widget.Region)
            .Select(group => new { group.Key, Average = group.Average(widget => widget.Amount) })
            .Expression;
        var averageVisitor = new CypherQueryVisitor(
            typeof(NamingWidget),
            hasComplexProperties: true,
            indexCatalog: store.IndexCatalog);
        averageVisitor.Visit(averageExpression);
        Assert.Equal("aggregate", Assert.IsType<AgeSqlPlan>(averageVisitor.Query.Plan).Kind);

        var averages = await store.Graph.Nodes<NamingWidget>()
            .Where(widget => widget.Region == "EU")
            .GroupBy(widget => widget.Region)
            .Select(group => new { group.Key, Average = group.Average(widget => widget.Amount) })
            .ToListAsync(cancellationToken);
        var averageRow = Assert.Single(averages);
        Assert.Equal("EU", averageRow.Key);
        Assert.Equal(200, averageRow.Average);

        var dropped = await store.DropIndexesAsync(cancellationToken);
        Assert.True(dropped.StructuresRemoved >= 2);
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, Label + "__idx", cancellationToken));

        var afterDrop = await store.Graph.Nodes<NamingWidget>()
            .Where(widget => widget.Region == "EU")
            .Select(widget => widget.Name)
            .ToListAsync(cancellationToken);
        Assert.Equal(["a", "c"], afterDrop.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task EdgeLabelWithProviderTableNameIsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_edge_label_collision");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new NamingWidget { Name = "a", Region = "EU", Amount = 100 },
            cancellationToken: cancellationToken);
        await store.Graph.CreateNodeAsync(
            new NamingWidget { Name = "b", Region = "US", Amount = 200 },
            cancellationToken: cancellationToken);
        await store.Graph.CreateRelationshipAsync(
            store.Graph.Nodes<NamingWidget>().Where(widget => widget.Name == "a"),
            new NamingWidgetCollidingRelationship(),
            store.Graph.Nodes<NamingWidget>().Where(widget => widget.Name == "b"),
            cancellationToken: cancellationToken);

        // Provider companion names are reserved for every AGE label kind, not only vertices.
        var exception = await Assert.ThrowsAsync<GraphException>(
            () => store.EnsureIndexesAsync(cancellationToken));
        Assert.Contains("already has a label named", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Label + "__idx", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclarationAddedAfterEnsureFallsBackUntilReensure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_late_decl");
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(Label, "Region"));
        await using var store = new AgeGraphStore(dataSource, graphName, registry);
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new NamingWidget { Name = "alpha", Region = "EU", Amount = 100 },
            cancellationToken: cancellationToken);
        _ = await store.EnsureIndexesAsync(cancellationToken);

        // Adding a declaration after maintenance must not make the existing table look complete.
        registry.AddPropertyIndex(new PropertyIndexDefinition(Label, "Name"));
        await store.Graph.CreateNodeAsync(
            new NamingWidget { Name = "beta", Region = "EU", Amount = 200 },
            cancellationToken: cancellationToken);

        var beforeReensure = await store.Graph.Nodes<NamingWidget>()
            .Where(widget => widget.Name == "beta")
            .ToListAsync(cancellationToken);
        Assert.Single(beforeReensure);

        var reensured = await store.EnsureIndexesAsync(cancellationToken);
        Assert.True(reensured.StructuresCreated >= 1);
        var afterReensure = await store.Graph.Nodes<NamingWidget>()
            .Where(widget => widget.Name == "beta")
            .ToListAsync(cancellationToken);
        Assert.Single(afterReensure);
    }

    private static SchemaRegistry CreateRegistry()
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { Label, ChildLabel })
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Region"));
            registry.AddPropertyAggregate(new PropertyAggregateDefinition(
                label,
                "Region",
                "Amount",
                AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
                AggregateFunction.Max | AggregateFunction.Average));
        }

        return registry;
    }

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
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var connectionLease = connection.ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText =
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = @graph AND table_name = @table";
        command.Parameters.AddWithValue("graph", graphName);
        command.Parameters.AddWithValue("table", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    [Node(Label)]
    public record NamingWidget : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? Region { get; set; }
        public long Amount { get; set; }
    }

    [Node(ChildLabel)]
    public record NamingWidgetChild : NamingWidget;

    [Relationship(Label + "__idx")]
    public sealed record NamingWidgetCollidingRelationship : Relationship;
}
