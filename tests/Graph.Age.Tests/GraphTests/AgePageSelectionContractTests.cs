// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.Age.Schema;
using Cvoya.Graph.Age.Serialization;
using Npgsql;
using Npgsql.Age;

/// <summary>
/// End-to-end coverage for the final-page pre-query of complex-property paging. Every case compares
/// the declared-index store against the ordinary AGE path over the same rows, so an ordering or
/// paging regression fails as a result difference rather than only as a SQL assertion.
/// </summary>
public sealed class AgePageSelectionContractTests(AgeGraphCleanupFixture graphCleanup)
{
    private const string PageLabel = "Nul96Page";
    private const string PageChildLabel = "Nul96PageChild";
    private const string UnicodePageLabel = "Nul96ÜnïcodePage";
    private const string UnicodePageChildLabel = "Nul96ÜnïcodePageChild";
    private const string SinglePageLabel = "Nul97SinglePageContract";
    private const string UnicodeAggLabel = "Nul97ÜnïcodeAggContract";

    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    static AgePageSelectionContractTests()
    {
        AgeTypeHierarchyRegistry.Instance.Register(PageLabel, PageChildLabel);
        AgeTypeHierarchyRegistry.Instance.Register(UnicodePageLabel, UnicodePageChildLabel);
    }

    [Fact]
    public async Task CompanionOrderingMatchesTheAgePathForAscendingDescendingNullsAndHierarchy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_page_order");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);
        await SeedAsync(baseline.Graph, cancellationToken);

        var ensured = await indexed.EnsureIndexesAsync(cancellationToken);
        Assert.True(ensured.StructuresCreated >= 2);
        Assert.Equal(1, await TableCountAsync(
            dataSource,
            graphName,
            AgePhysicalName.IndexTable(PageLabel),
            cancellationToken));

        var baselineAscending = await ReadPageAsync(baseline.Graph, ascending: true, cancellationToken);
        var indexedAscending = await ReadPageAsync(indexed.Graph, ascending: true, cancellationToken);
        Assert.Equal(baselineAscending, indexedAscending);
        Assert.Contains(indexedAscending, name => name is null);

        var baselineDescending = await ReadPageAsync(baseline.Graph, ascending: false, cancellationToken);
        var indexedDescending = await ReadPageAsync(indexed.Graph, ascending: false, cancellationToken);
        Assert.Equal(baselineDescending, indexedDescending);
        Assert.NotEqual(indexedAscending, indexedDescending);
        AssertPagePlan<Nul96Page>(
            indexed,
            query => query.OrderBy(node => node.Name).Skip(5).Take(4));
    }

    [Fact]
    public async Task CompanionOrderingMatchesTheAgePathForAnEscapedUnicodePropertyAndLabel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_page_unicode");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateUnicodeRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);
        await SeedUnicodeAsync(baseline.Graph, cancellationToken);

        _ = await indexed.EnsureIndexesAsync(cancellationToken);
        var physicalLabel = SerializationBridge.GetRootStorageName(UnicodePageLabel, relationship: false);
        Assert.Equal(1, await TableCountAsync(
            dataSource,
            graphName,
            AgePhysicalName.IndexTable(physicalLabel),
            cancellationToken));

        var baselinePage = await ReadUnicodePageAsync(baseline.Graph, cancellationToken);
        var indexedPage = await ReadUnicodePageAsync(indexed.Graph, cancellationToken);
        Assert.Equal(baselinePage, indexedPage);
        Assert.Equal(3, indexedPage.Count);
        AssertPagePlan<Nul96UnicodePage>(
            indexed,
            query => query.OrderBy(node => node.Nickname).Skip(1).Take(3));
    }

    [Fact]
    public async Task MissingCompanionTableFallsBackToTheAgeAccessorOrdering()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_page_missing_companion");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);
        await SeedAsync(baseline.Graph, cancellationToken);
        _ = await indexed.EnsureIndexesAsync(cancellationToken);

        foreach (var label in new[] { PageLabel, PageChildLabel })
        {
            await DropTableAsync(
                dataSource,
                graphName,
                AgePhysicalName.IndexTable(label),
                cancellationToken);
        }

        var expected = await ReadPageAsync(baseline.Graph, ascending: true, cancellationToken);
        var actual = await ReadPageAsync(indexed.Graph, ascending: true, cancellationToken);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SingleLabelPageSelectsThePlanAndMatchesTheAgePath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_page_single_label");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateSinglePageRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);
        await SeedSinglePageAsync(baseline.Graph, cancellationToken);
        _ = await indexed.EnsureIndexesAsync(cancellationToken);

        var baselinePage = await ReadSinglePageAsync(baseline.Graph, cancellationToken);
        var indexedPage = await ReadSinglePageAsync(indexed.Graph, cancellationToken);
        Assert.Equal(baselinePage, indexedPage);
        Assert.Equal(3, indexedPage.Count);
        AssertPagePlan<Nul97ContractSinglePage>(
            indexed,
            query => query.OrderBy(node => node.Name).Skip(1).Take(3));
    }

    [Fact]
    public async Task NonNativeSingleLabelReadReturnsRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_page_unicode_read");
        await using var store = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);
        await SeedUnicodeAggregateAsync(store.Graph, cancellationToken);

        var rows = await store.Graph.Nodes<Nul97ContractUnicodeAgg>()
            .OrderBy(node => node.Region)
            .ToListAsync(cancellationToken);

        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, row => row.Amount == 700L);
    }

    [Fact]
    public async Task NonNativeSingleLabelAverageSelectsTheAggregatePlan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_page_unicode_aggregate");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateUnicodeAggregateRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);
        await SeedUnicodeAggregateAsync(baseline.Graph, cancellationToken);
        _ = await indexed.EnsureIndexesAsync(cancellationToken);

        var baselineRows = await baseline.Graph.Nodes<Nul97ContractUnicodeAgg>()
            .GroupBy(node => node.Region)
            .Select(group => new { group.Key, Average = group.Average(node => node.Amount) })
            .ToListAsync(cancellationToken);
        var indexedRows = await indexed.Graph.Nodes<Nul97ContractUnicodeAgg>()
            .GroupBy(node => node.Region)
            .Select(group => new { group.Key, Average = group.Average(node => node.Amount) })
            .ToListAsync(cancellationToken);

        Assert.Equal(
            baselineRows.OrderBy(row => row.Key, StringComparer.Ordinal)
                .Select(row => (row.Key, row.Average)).ToArray(),
            indexedRows.OrderBy(row => row.Key, StringComparer.Ordinal)
                .Select(row => (row.Key, row.Average)).ToArray());
        Assert.NotEmpty(indexedRows);
        AssertSqlPlan(
            indexed,
            (IQueryable<Nul97ContractUnicodeAgg> query) => query
                .GroupBy(node => node.Region)
                .Select(group => new { group.Key, Average = group.Average(node => node.Amount) }));
    }

    [Fact]
    public async Task FilteredHierarchyPagingKeepsTheAgeFallback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_page_filtered_fallback");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);
        await SeedAsync(baseline.Graph, cancellationToken);
        _ = await indexed.EnsureIndexesAsync(cancellationToken);

        static IQueryable<Nul96Page> Query(IQueryable<Nul96Page> nodes) => nodes
            .Where(node => node.Name != null)
            .OrderBy(node => node.Name)
            .Skip(1)
            .Take(2);
        var expected = (await Query(baseline.Graph.Nodes<Nul96Page>()).ToListAsync(cancellationToken))
            .Select(node => node.Name).ToArray();
        var actual = (await Query(indexed.Graph.Nodes<Nul96Page>()).ToListAsync(cancellationToken))
            .Select(node => node.Name).ToArray();

        Assert.Equal(expected, actual);
        Assert.NotEmpty(actual);
        AssertNoPlan<Nul96Page>(indexed, Query);
    }

    private static void AssertPagePlan<T>(
        AgeGraphStore store,
        Func<IQueryable<T>, IQueryable<T>> query)
        where T : class, INode
    {
        var visitor = new CypherQueryVisitor(
            typeof(T),
            hasComplexProperties: true,
            indexCatalog: store.IndexCatalog);
        visitor.Visit(query(store.Graph.Nodes<T>()).Expression);
        Assert.IsType<AgePageSelectionPlan>(visitor.Query.Plan);
    }

    private static void AssertNoPlan<T>(
        AgeGraphStore store,
        Func<IQueryable<T>, IQueryable<T>> query)
        where T : class, INode
    {
        var visitor = new CypherQueryVisitor(
            typeof(T),
            hasComplexProperties: true,
            indexCatalog: store.IndexCatalog);
        visitor.Visit(query(store.Graph.Nodes<T>()).Expression);
        Assert.Null(visitor.Query.Plan);
    }

    private static AgeSqlPlan AssertSqlPlan<T, TResult>(
        AgeGraphStore store,
        Func<IQueryable<T>, IQueryable<TResult>> query)
        where T : class, INode
    {
        var visitor = new CypherQueryVisitor(
            typeof(T),
            hasComplexProperties: false,
            indexCatalog: store.IndexCatalog);
        visitor.Visit(query(store.Graph.Nodes<T>()).Expression);
        return Assert.IsType<AgeSqlPlan>(visitor.Query.Plan);
    }

    private static async Task<IReadOnlyList<string?>> ReadPageAsync(
        IGraph graph,
        bool ascending,
        CancellationToken cancellationToken)
    {
        var query = graph.Nodes<Nul96Page>();
        var ordered = ascending
            ? query.OrderBy(node => node.Name)
            : query.OrderByDescending(node => node.Name);
        var page = await ordered.Skip(5).Take(4).ToListAsync(cancellationToken);
        return page.Select(node => node.Name).ToArray();
    }

    private static async Task<IReadOnlyList<string?>> ReadUnicodePageAsync(
        IGraph graph,
        CancellationToken cancellationToken)
    {
        var page = await graph.Nodes<Nul96UnicodePage>()
            .OrderBy(node => node.Nickname)
            .Skip(1)
            .Take(3)
            .ToListAsync(cancellationToken);
        return page.Select(node => node.Nickname).ToArray();
    }

    private static async Task SeedAsync(IGraph graph, CancellationToken cancellationToken)
    {
        var nodes = new Nul96Page[]
        {
            new() { Name = "alpha", City = "A" },
            new() { Name = "bravo", City = "B" },
            new Nul96PageChild { Name = "charlie", City = "C" },
            new Nul96PageChild { Name = "delta", City = "D" },
            new() { Name = null, City = "E" },
            new Nul96PageChild { Name = "echo", City = "F" },
            new() { Name = "foxtrot", City = "G" },
            new Nul96PageChild { Name = null, City = "H" },
            new() { Name = "golf", City = "I" },
        };
        foreach (var node in nodes)
        {
            await graph.CreateNodeAsync(node, cancellationToken: cancellationToken);
        }
    }

    private static async Task SeedUnicodeAsync(IGraph graph, CancellationToken cancellationToken)
    {
        var nodes = new Nul96UnicodePage[]
        {
            new() { Nickname = "alpha", City = "A" },
            new Nul96UnicodePageChild { Nickname = "zulu", City = "Z" },
            new() { Nickname = null, City = "N" },
            new Nul96UnicodePageChild { Nickname = "mike", City = "M" },
        };
        foreach (var node in nodes)
        {
            await graph.CreateNodeAsync(node, cancellationToken: cancellationToken);
        }
    }

    private static SchemaRegistry CreateRegistry()
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { PageLabel, PageChildLabel })
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Name"));
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Name", PropertyIndexKind.Ordering));
        }

        return registry;
    }

    private static SchemaRegistry CreateUnicodeRegistry()
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { UnicodePageLabel, UnicodePageChildLabel })
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "nïck name"));
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "nïck name", PropertyIndexKind.Ordering));
        }

        return registry;
    }

    private static SchemaRegistry CreateSinglePageRegistry()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(SinglePageLabel, "Name"));
        registry.AddPropertyIndex(new PropertyIndexDefinition(
            SinglePageLabel,
            "Name",
            PropertyIndexKind.Ordering));
        return registry;
    }

    private static SchemaRegistry CreateUnicodeAggregateRegistry()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(UnicodeAggLabel, "Region"));
        registry.AddPropertyAggregate(new PropertyAggregateDefinition(
            UnicodeAggLabel,
            "Region",
            "Amount",
            AggregateFunction.Count | AggregateFunction.Average));
        return registry;
    }

    private static async Task SeedSinglePageAsync(IGraph graph, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot" })
        {
            await graph.CreateNodeAsync(
                new Nul97ContractSinglePage
                {
                    Name = name,
                    Address = new Nul96Address { City = name },
                },
                cancellationToken: cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<string?>> ReadSinglePageAsync(
        IGraph graph,
        CancellationToken cancellationToken)
    {
        var page = await graph.Nodes<Nul97ContractSinglePage>()
            .OrderBy(node => node.Name)
            .Skip(1)
            .Take(3)
            .ToListAsync(cancellationToken);
        return page.Select(node => node.Name).ToArray();
    }

    private static async Task SeedUnicodeAggregateAsync(IGraph graph, CancellationToken cancellationToken)
    {
        foreach (var (region, amount) in new[]
        {
            ("EU", 100L),
            ("EU", 300L),
            ("US", 500L),
            ("US", 700L),
        })
        {
            await graph.CreateNodeAsync(
                new Nul97ContractUnicodeAgg { Region = region, Amount = amount },
                cancellationToken: cancellationToken);
        }
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
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

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
#pragma warning disable CA2100 // The identifiers are provider-derived test names, not user input.
        command.CommandText =
            $"DROP TABLE IF EXISTS {Quote(graphName)}.{Quote(tableName)} CASCADE";
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    [Node(PageLabel)]
    public record Nul96Page : Node
    {
        public string? Name { get; set; }

        public Nul96Address? Address { get; set; }

        public string? City { get; set; }
    }

    [Node(PageChildLabel)]
    public sealed record Nul96PageChild : Nul96Page;

    [Node(UnicodePageLabel)]
    public record Nul96UnicodePage : Node
    {
        [Property(Label = "nïck name")]
        public string? Nickname { get; set; }

        public Nul96Address? Address { get; set; }

        public string? City { get; set; }
    }

    [Node(UnicodePageChildLabel)]
    public sealed record Nul96UnicodePageChild : Nul96UnicodePage;

    [Node(SinglePageLabel)]
    public sealed record Nul97ContractSinglePage : Node
    {
        public string? Name { get; set; }

        public Nul96Address? Address { get; set; }
    }

    [Node(UnicodeAggLabel)]
    public sealed record Nul97ContractUnicodeAgg : Node
    {
        public string? Region { get; set; }

        public long Amount { get; set; }
    }

    public sealed class Nul96Address
    {
        public string City { get; set; } = string.Empty;
    }
}
