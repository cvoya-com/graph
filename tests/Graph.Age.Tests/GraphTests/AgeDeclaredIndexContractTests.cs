// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using System.Globalization;
using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Schema;
using Npgsql;
using Npgsql.Age;

/// <summary>
/// End-to-end contract coverage for the generalized <see cref="PropertyIndexDefinition"/> and
/// <see cref="PropertyAggregateDefinition"/> declarations. Most declarations use mapped property
/// names; one contract proves that declarations over properties literally named <c>Status</c> and
/// <c>Kaufpreis</c> use the same generalized <c>__idx</c> layout. Results are compared against a
/// store with no declarations, which is the ordinary AGE path.
/// </summary>
public sealed class AgeDeclaredIndexContractTests(AgeGraphCleanupFixture graphCleanup)
{
    private const string Label = "Nul91Contract";
    private const string ChildLabel = "Nul91Contract_Kauf";

    private const string LongLabel = "Nul91LongLabel_012345678901234567890123456789012345678901234";
    private const string LongProperty =
        "nul91_bucket_012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789";

    private const string MixedPropertyLabel = "Nul95MixedProperty";
    private const string OrderingOnlyLabel = "Nul95OrderingOnly";

    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    static AgeDeclaredIndexContractTests()
    {
        AgeTypeHierarchyRegistry.Instance.Register(Label, ChildLabel);
    }

    [Fact]
    public async Task DeclaredIndexResultsMatchTheAgePathForFilterGroupDistinctAndAggregates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_declared_contract");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await SeedAsync(baseline.Graph, cancellationToken);

        var before = await SnapshotAsync(baseline.Graph, cancellationToken);
        Assert.Equal("<null>:2,Müller-😀-東京:1,gold:2,silver:1", before.Grouped);

        var report = await indexed.EnsureIndexesAsync(cancellationToken);
        Assert.True(report.RowsBackfilled >= 6);
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, AgePhysicalName.IndexTable(Label), cancellationToken));
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, AgePhysicalName.IndexTable(ChildLabel), cancellationToken));

        var after = await SnapshotAsync(indexed.Graph, cancellationToken);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task EnsureAndDropAreIdempotentAndReEnsureBackfillsNewRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_declared_idempotent");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateIndexOnlyRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await CreateAsync(store.Graph, new Contract { Name = "a", Tier = "gold", Amount = 1 }, cancellationToken);
        await CreateAsync(store.Graph, new Contract { Name = "b", Tier = "silver", Amount = 2 }, cancellationToken);
        await CreateAsync(store.Graph, new ContractChild { Name = "c", Tier = "gold", Amount = 3 }, cancellationToken);
        await CreateAsync(store.Graph, new ContractChild { Name = "d", Tier = null, Amount = 4 }, cancellationToken);

        var first = await store.EnsureIndexesAsync(cancellationToken);
        Assert.True(first.StructuresCreated >= 2);
        Assert.True(first.RowsBackfilled >= 4);
        Assert.Contains(AgePhysicalName.IndexTable(Label), first.CreatedTableNames);
        Assert.Contains(AgePhysicalName.IndexTable(ChildLabel), first.CreatedTableNames);

        var second = await store.EnsureIndexesAsync(cancellationToken);
        Assert.Equal(0, second.StructuresCreated);
        Assert.Empty(second.CreatedTableNames);
        Assert.True(second.RowsBackfilled >= 4);
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, AgePhysicalName.IndexTable(Label), cancellationToken));
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, AgePhysicalName.IndexTable(ChildLabel), cancellationToken));

        var baseColumns = await ColumnNamesAsync(dataSource, graphName, AgePhysicalName.IndexTable(Label), cancellationToken);
        var childColumns = await ColumnNamesAsync(dataSource, graphName, AgePhysicalName.IndexTable(ChildLabel), cancellationToken);
        Assert.Contains(AgePhysicalName.TextColumn(Label, "tarif_stufe"), baseColumns);
        Assert.Contains(AgePhysicalName.TextColumn(ChildLabel, "tarif_stufe"), childColumns);
        var groupedBeforeDrop = await GroupedAsync(store.Graph, cancellationToken);

        var dropped = await store.DropIndexesAsync(cancellationToken);
        Assert.True(dropped.StructuresRemoved >= 2);
        Assert.Contains(AgePhysicalName.IndexTable(Label), dropped.RemovedTableNames);
        Assert.Contains(AgePhysicalName.IndexTable(ChildLabel), dropped.RemovedTableNames);
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, AgePhysicalName.IndexTable(Label), cancellationToken));
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, AgePhysicalName.IndexTable(ChildLabel), cancellationToken));
        Assert.Equal(groupedBeforeDrop, await GroupedAsync(store.Graph, cancellationToken));

        var droppedAgain = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(0, droppedAgain.StructuresRemoved);
        Assert.Empty(droppedAgain.RemovedTableNames);

        await CreateAsync(store.Graph, new Contract { Name = "e", Tier = "gold", Amount = 5 }, cancellationToken);

        var reEnsured = await store.EnsureIndexesAsync(cancellationToken);
        Assert.True(reEnsured.RowsBackfilled >= 5);
        var columns = await ColumnNamesAsync(dataSource, graphName, AgePhysicalName.IndexTable(Label), cancellationToken);
        Assert.True(
            columns.Contains(AgePhysicalName.TextColumn(Label, "tarif_stufe")),
            $"{AgePhysicalName.IndexTable(Label)} columns: {string.Join(",", columns)}; report: {reEnsured}");
        Assert.Equal(
            "<null>:1,gold:3,silver:1",
            await GroupedAsync(store.Graph, cancellationToken));
    }

    [Fact]
    public async Task MaintenanceReportNamesTheHierarchyAndAggregateTables()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_report_names");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await CreateAsync(store.Graph, new Contract { Name = "a", Tier = "gold", Amount = 100 }, cancellationToken);
        await CreateAsync(store.Graph, new ContractChild { Name = "b", Tier = "silver", Amount = 200 }, cancellationToken);

        var baseIndex = AgePhysicalName.IndexTable(Label);
        var childIndex = AgePhysicalName.IndexTable(ChildLabel);
        var baseAggregate = AgePhysicalName.AggregateTable(Label, "tarif_stufe", "Amount");
        var childAggregate = AgePhysicalName.AggregateTable(ChildLabel, "tarif_stufe", "Amount");

        var ensured = await store.EnsureIndexesAsync(cancellationToken);
        Assert.Equal(4, ensured.CreatedTableNames.Count);
        Assert.Contains(baseIndex, ensured.CreatedTableNames);
        Assert.Contains(childIndex, ensured.CreatedTableNames);
        Assert.Contains(baseAggregate, ensured.CreatedTableNames);
        Assert.Contains(childAggregate, ensured.CreatedTableNames);
        // The structure count also includes the missing per-property indexes.
        Assert.True(ensured.StructuresCreated > ensured.CreatedTableNames.Count);

        var second = await store.EnsureIndexesAsync(cancellationToken);
        Assert.Empty(second.CreatedTableNames);
        Assert.Empty(second.RemovedTableNames);

        var dropped = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(4, dropped.RemovedTableNames.Count);
        Assert.Contains(baseIndex, dropped.RemovedTableNames);
        Assert.Contains(childIndex, dropped.RemovedTableNames);
        Assert.Contains(baseAggregate, dropped.RemovedTableNames);
        Assert.Contains(childAggregate, dropped.RemovedTableNames);

        var droppedAgain = await store.DropIndexesAsync(cancellationToken);
        Assert.Empty(droppedAgain.RemovedTableNames);
    }

    [Fact]
    public async Task AggregateDefinitionsAreCountedOnceOnEveryDrop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_decl_agg_two_drop");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await CreateAsync(store.Graph, new Contract { Name = "a", Tier = "gold", Amount = 100 }, cancellationToken);
        await CreateAsync(store.Graph, new ContractChild { Name = "b", Tier = "silver", Amount = 200 }, cancellationToken);

        // One property index and one aggregate per label: four user declarations in total.
        var ensured = await store.EnsureIndexesAsync(cancellationToken);
        Assert.Equal(4, ensured.DefinitionsInspected);

        var first = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(4, first.DefinitionsInspected);
        Assert.Equal(4, first.StructuresRemoved);

        var second = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(4, second.DefinitionsInspected);
        Assert.Equal(0, second.StructuresRemoved);
    }

    [Fact]
    public async Task AggregateOnlyDeclarationIsCountedOnceOnEveryDrop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_decl_agg_only");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateAggregateOnlyRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await CreateAsync(store.Graph, new Contract { Name = "a", Tier = "EU", Amount = 100 }, cancellationToken);

        // The group-key text column is derived from the aggregate and must not add a definition.
        var ensured = await store.EnsureIndexesAsync(cancellationToken);
        Assert.Equal(1, ensured.DefinitionsInspected);

        var first = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(1, first.DefinitionsInspected);
        Assert.Equal(2, first.StructuresRemoved);

        var second = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(1, second.DefinitionsInspected);
        Assert.Equal(0, second.StructuresRemoved);
    }

    [Fact]
    public async Task TwoGraphsDoNotSharePresenceCacheStateOrPhysicalStructures()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var firstGraph = graphCleanup.CreateGraphName("cvoya_declared_graph_a");
        var secondGraph = graphCleanup.CreateGraphName("cvoya_declared_graph_b");
        await using var first = new AgeGraphStore(dataSource, firstGraph, CreateRegistry());
        await using var second = new AgeGraphStore(dataSource, secondGraph, CreateRegistry());
        await first.CreateGraphIfNotExistsAsync(cancellationToken);
        await second.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(firstGraph);
        graphCleanup.MarkGraphCreated(secondGraph);

        Assert.NotSame(first.IndexPresenceCache, second.IndexPresenceCache);

        await CreateAsync(first.Graph, new Contract { Name = "a", Tier = "gold", Amount = 10 }, cancellationToken);
        await CreateAsync(first.Graph, new ContractChild { Name = "b", Tier = "silver", Amount = 20 }, cancellationToken);
        await CreateAsync(second.Graph, new Contract { Name = "c", Tier = "gold", Amount = 30 }, cancellationToken);

        var secondBaseline = await SnapshotAsync(second.Graph, cancellationToken);
        _ = await first.EnsureIndexesAsync(cancellationToken);

        // The first graph now has companion tables; the second must still answer through the AGE
        // path and must not observe the first store's presence-cache entries.
        Assert.Equal(1, await TableCountAsync(dataSource, firstGraph, AgePhysicalName.IndexTable(Label), cancellationToken));
        Assert.Equal(0, await TableCountAsync(dataSource, secondGraph, AgePhysicalName.IndexTable(Label), cancellationToken));
        Assert.Equal(secondBaseline, await SnapshotAsync(second.Graph, cancellationToken));

        _ = await second.EnsureIndexesAsync(cancellationToken);
        Assert.Equal(secondBaseline, await SnapshotAsync(second.Graph, cancellationToken));
        Assert.Equal("gold:1", await GroupedAsync(second.Graph, cancellationToken));
    }

    [Fact]
    public async Task NullGroupKeyAndNegatedPredicateMatchTheAgePath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_declared_null_group");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var indexed = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await SeedAsync(baseline.Graph, cancellationToken);
        var before = await SnapshotAsync(baseline.Graph, cancellationToken);
        _ = await indexed.EnsureIndexesAsync(cancellationToken);
        var after = await SnapshotAsync(indexed.Graph, cancellationToken);

        Assert.Equal(before.Negated, after.Negated);
        Assert.Equal(before.Grouped, after.Grouped);
        Assert.DoesNotContain("missing", after.Negated, StringComparison.Ordinal);

        // `<>` excludes rows whose grouped property is missing/NULL on both paths.
        var nullTier = await indexed.Graph.Nodes<Contract>()
            .Where(contract => contract.Tier == null)
            .Select(contract => contract.Name)
            .ToListAsync(cancellationToken);
        Assert.Equal(2, nullTier.Count);
    }

    [Fact]
    public async Task MissingCompanionTableKeepsTheOrdinaryAgePath()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_declared_missing_table");
        await using var baseline = new AgeGraphStore(dataSource, graphName, new SchemaRegistry());
        await using var declared = new AgeGraphStore(dataSource, graphName, CreateRegistry());
        await baseline.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await SeedAsync(baseline.Graph, cancellationToken);

        // Declarations are visible to the planner, but EnsureIndexesAsync was never called, so
        // every required relation probe fails and the query must stay on the AGE path.
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, AgePhysicalName.IndexTable(Label), cancellationToken));
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, AgePhysicalName.IndexTable(ChildLabel), cancellationToken));
        Assert.Equal(
            await SnapshotAsync(baseline.Graph, cancellationToken),
            await SnapshotAsync(declared.Graph, cancellationToken));
    }

    [Fact]
    public async Task LongPropertyAndLabelNamesUseBoundedPhysicalStorage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_declared_long_names");
        await using var store = new AgeGraphStore(dataSource, graphName, CreateLongRegistry());
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new LongNameContract { Name = "a", Bucket = "gold", Value = 10 },
            cancellationToken: cancellationToken);
        await store.Graph.CreateNodeAsync(
            new LongNameContract { Name = "b", Bucket = "silver", Value = 20 },
            cancellationToken: cancellationToken);
        await store.Graph.CreateNodeAsync(
            new LongNameContract { Name = "c", Bucket = null, Value = 30 },
            cancellationToken: cancellationToken);

        var before = await GroupedAsync<LongNameContract>(store.Graph, contract => contract.Bucket, cancellationToken);
        var report = await store.EnsureIndexesAsync(cancellationToken);
        Assert.True(report.RowsBackfilled >= 3);

        var tableName = AgePhysicalName.IndexTable(LongLabel);
        Assert.True(tableName.Length <= 63);
        Assert.True(AgePhysicalName.TextColumn(LongLabel, LongProperty).Length <= 63);
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, tableName, cancellationToken));
        Assert.True(await MaxColumnNameLengthAsync(dataSource, graphName, tableName, cancellationToken) <= 63);

        Assert.Equal(
            before,
            await GroupedAsync<LongNameContract>(store.Graph, contract => contract.Bucket, cancellationToken));
        Assert.Equal("<null>:1,gold:1,silver:1", before);
    }

    [Fact]
    public async Task StatusAndKaufpreisDeclarationsUseGeneralizedNames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_status_generalized");
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(MixedPropertyLabel, "Status"));
        registry.AddPropertyAggregate(new PropertyAggregateDefinition(
            MixedPropertyLabel,
            "Status",
            "Kaufpreis",
            AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
            AggregateFunction.Max | AggregateFunction.Average));
        await using var store = new AgeGraphStore(dataSource, graphName, registry);
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new MixedPropertyNode { Name = "a", Region = "EU", Status = "aktiv", Kaufpreis = 100 },
            cancellationToken: cancellationToken);

        var report = await store.EnsureIndexesAsync(cancellationToken);
        Assert.Equal(2, report.DefinitionsInspected);

        var indexTable = AgePhysicalName.IndexTable(MixedPropertyLabel);
        var aggregateTable = AgePhysicalName.AggregateTable(MixedPropertyLabel, "Status", "Kaufpreis");
        Assert.Equal(MixedPropertyLabel + "__idx", indexTable);
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, indexTable, cancellationToken));
        Assert.Equal(1, await TableCountAsync(dataSource, graphName, aggregateTable, cancellationToken));

        var columns = await ColumnNamesAsync(dataSource, graphName, indexTable, cancellationToken);
        Assert.Contains(AgePhysicalName.TextColumn(MixedPropertyLabel, "Status"), columns);
        Assert.Contains(AgePhysicalName.NumericColumn(MixedPropertyLabel, "Kaufpreis"), columns);
        Assert.DoesNotContain("status_text", columns);
        Assert.DoesNotContain("kaufpreis", columns);

        var aggregateColumns = await ColumnNamesAsync(
            dataSource, graphName, aggregateTable, cancellationToken);
        Assert.DoesNotContain("status_text", aggregateColumns);
        Assert.DoesNotContain("status_key", aggregateColumns);

        var tables = await TableNamesAsync(dataSource, graphName, cancellationToken);
        Assert.DoesNotContain(MixedPropertyLabel + "__status", tables);
        Assert.DoesNotContain(MixedPropertyLabel + "__num_agg", tables);
        Assert.DoesNotContain(tables, name => name.EndsWith("__status", StringComparison.Ordinal));
        Assert.DoesNotContain(tables, name => name.EndsWith("__num_agg", StringComparison.Ordinal));

        var groups = await store.Graph.Nodes<MixedPropertyNode>()
            .GroupBy(node => node.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        Assert.Equal("aktiv:1", string.Join(",", groups.Select(group => $"{group.Key}:{group.Count}")));

        var dropped = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(2, dropped.DefinitionsInspected);
        Assert.Equal(2, dropped.StructuresRemoved);
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, indexTable, cancellationToken));
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, aggregateTable, cancellationToken));
    }

    [Fact]
    public async Task ChangingAPropertyDeclarationToStatusKeepsOneGeneralizedIndexTable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_declaration_to_status");
        var indexTable = AgePhysicalName.IndexTable(MixedPropertyLabel);

        var regionRegistry = new SchemaRegistry();
        regionRegistry.AddPropertyIndex(new PropertyIndexDefinition(MixedPropertyLabel, "Region"));
        await using (var first = new AgeGraphStore(dataSource, graphName, regionRegistry))
        {
            await first.CreateGraphIfNotExistsAsync(cancellationToken);
            graphCleanup.MarkGraphCreated(graphName);
            await first.Graph.CreateNodeAsync(
                new MixedPropertyNode { Name = "a", Region = "EU", Status = "aktiv", Kaufpreis = 1 },
                cancellationToken: cancellationToken);
            _ = await first.EnsureIndexesAsync(cancellationToken);
        }

        Assert.Equal(1, await TableCountAsync(dataSource, graphName, indexTable, cancellationToken));
        var before = await ColumnNamesAsync(dataSource, graphName, indexTable, cancellationToken);
        Assert.Contains(AgePhysicalName.TextColumn(MixedPropertyLabel, "Region"), before);

        var statusRegistry = new SchemaRegistry();
        statusRegistry.AddPropertyIndex(new PropertyIndexDefinition(MixedPropertyLabel, "Status"));
        await using var store = new AgeGraphStore(dataSource, graphName, statusRegistry);
        _ = await store.EnsureIndexesAsync(cancellationToken);

        Assert.Equal(1, await TableCountAsync(dataSource, graphName, indexTable, cancellationToken));
        var after = await ColumnNamesAsync(dataSource, graphName, indexTable, cancellationToken);
        Assert.Contains(AgePhysicalName.TextColumn(MixedPropertyLabel, "Region"), after);
        Assert.Contains(AgePhysicalName.TextColumn(MixedPropertyLabel, "Status"), after);
        Assert.Equal(0, await TableCountAsync(
            dataSource, graphName, MixedPropertyLabel + "__status", cancellationToken));

        var dropped = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(1, dropped.DefinitionsInspected);
        Assert.Equal(1, dropped.StructuresRemoved);
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, indexTable, cancellationToken));
    }

    [Fact]
    public async Task OrderingOnlyDeclarationWithAPreCatalogIndexTableIsCountedOnceOnEveryDrop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        var graphName = graphCleanup.CreateGraphName("cvoya_ordering_pre_catalog");
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(
            OrderingOnlyLabel,
            "Region",
            PropertyIndexKind.Ordering));
        await using var store = new AgeGraphStore(dataSource, graphName, registry);
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        await store.Graph.CreateNodeAsync(
            new OrderingOnlyNode { Name = "a", Region = "EU" },
            cancellationToken: cancellationToken);
        var indexTable = AgePhysicalName.IndexTable(OrderingOnlyLabel);
        await CreateTableAsync(dataSource, graphName, indexTable, cancellationToken);

        var ensured = await store.EnsureIndexesAsync(cancellationToken);
        Assert.Equal(1, ensured.DefinitionsInspected);
        Assert.Equal(0, ensured.StructuresCreated);

        var first = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(1, first.DefinitionsInspected);
        Assert.Equal(1, first.StructuresRemoved);
        Assert.Equal(0, await TableCountAsync(dataSource, graphName, indexTable, cancellationToken));

        var second = await store.DropIndexesAsync(cancellationToken);
        Assert.Equal(1, second.DefinitionsInspected);
        Assert.Equal(0, second.StructuresRemoved);
    }


    private static async Task SeedAsync(IGraph graph, CancellationToken cancellationToken)
    {
        await CreateAsync(graph, new Contract { Name = "a", Tier = "gold", Amount = 100 }, cancellationToken);
        await CreateAsync(graph, new Contract { Name = "b", Tier = "silver", Amount = 200 }, cancellationToken);
        await CreateAsync(graph, new Contract { Name = "missing", Tier = null, Amount = 300 }, cancellationToken);
        await CreateAsync(graph, new ContractChild { Name = "c", Tier = "gold", Amount = 400 }, cancellationToken);
        await CreateAsync(graph, new ContractChild { Name = "d", Tier = null, Amount = 500 }, cancellationToken);
        await CreateAsync(graph, new ContractChild { Name = "Müller-😀-東京", Tier = "Müller-😀-東京", Amount = 600 }, cancellationToken);
    }

    private static Task CreateAsync(IGraph graph, Contract contract, CancellationToken cancellationToken) =>
        graph.CreateNodeAsync(contract, cancellationToken: cancellationToken);

    private static async Task<Snapshot> SnapshotAsync(IGraph graph, CancellationToken cancellationToken)
    {
        var filtered = await graph.Nodes<Contract>()
            .Where(contract => contract.Tier == "gold")
            .Select(contract => contract.Name)
            .ToListAsync(cancellationToken);
        var negated = await graph.Nodes<Contract>()
            .Where(contract => contract.Tier != "gold")
            .Select(contract => contract.Name)
            .ToListAsync(cancellationToken);
        var distinct = await graph.Nodes<Contract>()
            .Select(contract => contract.Tier)
            .Distinct()
            .ToListAsync(cancellationToken);
        var averages = await graph.Nodes<Contract>()
            .GroupBy(contract => contract.Tier)
            .Select(group => new { group.Key, Value = group.Average(contract => contract.Amount) })
            .ToListAsync(cancellationToken);
        var minimums = await graph.Nodes<Contract>()
            .GroupBy(contract => contract.Tier)
            .Select(group => new { group.Key, Value = group.Min(contract => contract.Amount) })
            .ToListAsync(cancellationToken);
        var maximums = await graph.Nodes<Contract>()
            .GroupBy(contract => contract.Tier)
            .Select(group => new { group.Key, Value = group.Max(contract => contract.Amount) })
            .ToListAsync(cancellationToken);

        return new Snapshot(
            Join(filtered),
            Join(negated),
            Join(distinct),
            await GroupedAsync(graph, cancellationToken),
            Format(averages),
            Format(minimums),
            Format(maximums));
    }

    private static async Task<string> GroupedAsync(IGraph graph, CancellationToken cancellationToken) =>
        await GroupedAsync<Contract>(graph, contract => contract.Tier, cancellationToken);

    private static async Task<string> GroupedAsync<T>(
        IGraph graph,
        System.Linq.Expressions.Expression<Func<T, string?>> key,
        CancellationToken cancellationToken)
        where T : class, INode
    {
        var rows = await graph.Nodes<T>()
            .GroupBy(key)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return string.Join(
            ",",
            rows
                .OrderBy(row => row.Key ?? "<null>", StringComparer.Ordinal)
                .Select(row => $"{(row.Key is null ? "<null>" : row.Key)}:{row.Count.ToString(CultureInfo.InvariantCulture)}"));
    }

    private static string Format(IEnumerable<dynamic> rows) => string.Join(
        ",",
        rows
            .OrderBy(row => (string?)row.Key ?? "<null>", StringComparer.Ordinal)
            .Select(row =>
                $"{(row.Key is null ? "<null>" : row.Key)}:" +
                $"{Convert.ToDouble(row.Value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)}"));

    private static string Join(IEnumerable<string?> values) => string.Join(
        ",",
        values
            .Select(value => value ?? "<null>")
            .OrderBy(value => value, StringComparer.Ordinal));

    private static SchemaRegistry CreateRegistry()
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { Label, ChildLabel })
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "tarif_stufe"));
            registry.AddPropertyAggregate(new PropertyAggregateDefinition(
                label,
                "tarif_stufe",
                "Amount",
                AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
                AggregateFunction.Max | AggregateFunction.Average));
        }

        return registry;
    }

    private static SchemaRegistry CreateIndexOnlyRegistry()
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { Label, ChildLabel })
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "tarif_stufe"));
        }

        return registry;
    }

    private static SchemaRegistry CreateAggregateOnlyRegistry()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyAggregate(new PropertyAggregateDefinition(
            Label,
            "tarif_stufe",
            "Amount",
            AggregateFunction.Count | AggregateFunction.Average));
        return registry;
    }

    private static SchemaRegistry CreateLongRegistry()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(LongLabel, LongProperty));
        registry.AddPropertyAggregate(new PropertyAggregateDefinition(
            LongLabel,
            LongProperty,
            "Value",
            AggregateFunction.Count | AggregateFunction.Average));
        return registry;
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseAge();
        return builder.Build();
    }

    private static async Task CreateTableAsync(
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
        command.CommandText = $"SET search_path = {Quote(graphName)}; CREATE TABLE {Quote(tableName)} (id bigint)";
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

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

    private static async Task<string[]> TableNamesAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var connectionLease = connection.ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText =
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @graph ORDER BY table_name";
        command.Parameters.AddWithValue("graph", graphName);
        var names = new List<string>();
        var reader = await command.ExecuteReaderAsync(cancellationToken);
        await using var readerLease = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    private static async Task<string[]> ColumnNamesAsync(
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
            "SELECT column_name FROM information_schema.columns " +
            "WHERE table_schema = @graph AND table_name = @table ORDER BY ordinal_position";
        command.Parameters.AddWithValue("graph", graphName);
        command.Parameters.AddWithValue("table", tableName);
        var names = new List<string>();
        var reader = await command.ExecuteReaderAsync(cancellationToken);
        await using var readerLease = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    private static async Task<int> MaxColumnNameLengthAsync(
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
            "SELECT COALESCE(max(length(column_name)), 0) FROM information_schema.columns " +
            "WHERE table_schema = @graph AND table_name = @table";
        command.Parameters.AddWithValue("graph", graphName);
        command.Parameters.AddWithValue("table", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    [Node(Label)]
    public record Contract : Node
    {
        public string Name { get; set; } = string.Empty;

        [Property(Label = "tarif_stufe")]
        public string? Tier { get; set; }

        public long Amount { get; set; }
    }

    [Node(ChildLabel)]
    public sealed record ContractChild : Contract;

    [Node(LongLabel)]
    public record LongNameContract : Node
    {
        public string Name { get; set; } = string.Empty;

        [Property(Label = LongProperty)]
        public string? Bucket { get; set; }

        public long Value { get; set; }
    }

    [Node(MixedPropertyLabel)]
    public record MixedPropertyNode : Node
    {
        public string Name { get; set; } = string.Empty;

        public string? Region { get; set; }

        // Deliberately the legacy nGen property names. They must resolve to the same generalized
        // physical layout as every other property.
        public string? Status { get; set; }

        public long Kaufpreis { get; set; }
    }

    [Node(OrderingOnlyLabel)]
    public record OrderingOnlyNode : Node
    {
        public string Name { get; set; } = string.Empty;

        public string? Region { get; set; }
    }

    private sealed record Snapshot(
        string Filtered,
        string Negated,
        string Distinct,
        string Grouped,
        string Averages,
        string Minimums,
        string Maximums);
}
