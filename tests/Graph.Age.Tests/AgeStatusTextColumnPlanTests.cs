// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using System.Text;
using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Querying.Cypher.Lowering;
using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.Age.Schema;
using Cvoya.Graph.Age.Serialization;

public sealed class AgeIndexedPlanTests
{
    public AgeIndexedPlanTests()
    {
        AgeTypeHierarchyRegistry.Instance.Register("Nul70Contract", "Nul70Contract_Kauf");
        AgeTypeHierarchyRegistry.Instance.Register("Nul72Complex", "Nul72ComplexChild");
        AgeTypeHierarchyRegistry.Instance.Register("Nul91AggContract", "Nul91AggContract_Kauf");
        AgeTypeHierarchyRegistry.Instance.Register("Nul96NumericPage", "Nul96NumericPageChild");
        AgeTypeHierarchyRegistry.Instance.Register("Nul96Ünïcode", "Nul96ÜnïcodeChild");
    }

    [Fact]
    public void GroupedStatusCountUsesIndexedPlan()
    {
        var query = Translate(Nodes()
            .GroupBy(contract => contract.Status)
            .Select(group => new { group.Key, Count = group.Count() }).Expression);

        var plan = AssertPlan(query, "group");
        var sql = BuildSql(query);

        var column = AgePhysicalName.TextColumn("Nul70Contract", "Status");
        Assert.Contains("\"" + column + "\"", sql, StringComparison.Ordinal);
        Assert.Contains("to_jsonb(\"__group\")", sql, StringComparison.Ordinal);
        Assert.Contains("count(*)", sql, StringComparison.Ordinal);
        Assert.Contains("AS \"Count\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("`Count`", sql, StringComparison.Ordinal);
        Assert.Contains("\"" + AgePhysicalName.IndexTable("Nul70Contract") + "\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"" + AgePhysicalName.IndexTable("Nul70Contract_Kauf") + "\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("__status", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("status_text", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("agtype_access_operator(VARIADIC ARRAY[properties, '\"Status\"'", sql, StringComparison.Ordinal);
        Assert.Equal(2, plan.Labels.Count);
    }

    [Fact]
    public void DistinctStatusUsesIndexedPlan()
    {
        var query = Translate(Nodes().Select(contract => contract.Status).Distinct().Expression);

        _ = AssertPlan(query, "distinct");
        var sql = BuildSql(query);

        Assert.Contains("to_jsonb(\"__group\")::text::ag_catalog.agtype", sql, StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY \"__group\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusFilterOnEntityProjectionUsesIndexedPlan()
    {
        var query = Translate(Nodes().Where(contract => contract.Status == "aktiv").Expression);

        _ = AssertPlan(query, "filter");
        var sql = BuildSql(query);

        Assert.Contains("ag_catalog.agtype_typecast_vertex", sql, StringComparison.Ordinal);
        Assert.Contains(
            "JOIN \"nul70\".\"" + AgePhysicalName.IndexTable("Nul70Contract") + "\" AS __index",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE __index.\"" + AgePhysicalName.TextColumn("Nul70Contract", "Status") +
            "\" = (($1::text::jsonb) ->> 'p0')",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StatusNotEqualFilterExcludesNullStatus()
    {
        var query = Translate(Nodes().Where(contract => contract.Status != "aktiv").Expression);
        _ = AssertPlan(query, "filter");
        var sql = BuildSql(query);

        var column = AgePhysicalName.TextColumn("Nul70Contract", "Status");
        Assert.Contains("__index.\"" + column + "\" IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("__index.\"" + column + "\" IS DISTINCT FROM", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void NegatedIndexedPredicateWithLiteralExcludesNullStatus()
    {
        var builder = new StringBuilder();
        var column = "__group." + AgeIndexTable.QuoteIdentifier(
            AgePhysicalName.TextColumn("Nul70Contract", "Status"), "column name");
        AgeIndexedAggregatePlanBuilder.AppendPredicate(
            builder,
            new AgeIndexedAggregatePlanBuilder.IndexPredicate("Status", null, "aktiv", Negated: true),
            column);

        Assert.Equal(
            " WHERE " + column + " IS NOT NULL AND " +
            column + " IS DISTINCT FROM 'aktiv'",
            builder.ToString());
    }

    [Fact]
    public void AverageKaufpreisUsesAggregateCompanionPlan()
    {
        var query = Translate72(Nodes72()
            .GroupBy(contract => contract.Status)
            .Select(group => new { group.Key, Average = group.Average(contract => contract.Kaufpreis) }).Expression);

        var plan = AssertPlan(query, "aggregate");
        var sql = BuildSql(query);

        Assert.Contains(
            "\"" + AgePhysicalName.AggregateTable("Nul72Contract", "Status", "Kaufpreis") + "\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"" + AgePhysicalName.AggregateTable("Nul72Contract_Kauf", "Status", "Kaufpreis") + "\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("to_jsonb(\"__group\")", sql, StringComparison.Ordinal);
        Assert.Contains("sum(\"sum_value\")", sql, StringComparison.Ordinal);
        Assert.Contains("NULLIF(sum(\"cnt\"), 0)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("__num_agg", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ag_catalog.cypher(", sql, StringComparison.Ordinal);
        Assert.Equal(2, plan.Labels.Count);
    }

    [Fact]
    public void FilteredNumericAggregateTargetsTheAggregateGroupColumn()
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { "Nul91AggContract", "Nul91AggContract_Kauf" })
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "kategorie"));
            registry.AddPropertyAggregate(new PropertyAggregateDefinition(
                label,
                "kategorie",
                "betrag",
                AggregateFunction.Average));
        }

        var catalog = AgeIndexCatalog.From(registry);
        Assert.Equal("Nul91AggContract__idx", catalog.GetTableName("Nul91AggContract"));
        var visitor = new CypherQueryVisitor(
            typeof(Nul91AggContract),
            hasComplexProperties: false,
            indexCatalog: catalog);
        visitor.Visit(NodesAgg()
            .Where(contract => contract.Kategorie == "gold")
            .GroupBy(contract => contract.Kategorie)
            .Select(group => new { group.Key, Average = group.Average(contract => contract.Betrag) })
            .Expression);

        _ = AssertPlan(visitor.Query, "aggregate");
        var sql = BuildSql(visitor.Query);
        Assert.Contains(
            "WHERE \"" + AgeAggregateTable.GroupTextColumn + "\" =",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE \"p_", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MinMaxKaufpreisUseAggregateCompanionPlan()
    {
        var min = Translate72(Nodes72()
            .GroupBy(contract => contract.Status)
            .Select(group => new { group.Key, Min = group.Min(contract => contract.Kaufpreis) }).Expression);
        var max = Translate72(Nodes72()
            .GroupBy(contract => contract.Status)
            .Select(group => new { group.Key, Max = group.Max(contract => contract.Kaufpreis) }).Expression);

        _ = AssertPlan(min, "aggregate");
        _ = AssertPlan(max, "aggregate");
        Assert.Contains("to_jsonb(min(\"min_value\"))", BuildSql(min), StringComparison.Ordinal);
        Assert.Contains("to_jsonb(max(\"max_value\"))", BuildSql(max), StringComparison.Ordinal);
    }

    [Fact]
    public void UndeclaredAggregateFunctionKeepsAgePath()
    {
        var registry = Registry(
            ("Nul72Contract", "Status"),
            ("Nul72Contract_Kauf", "Status"));
        foreach (var label in new[] { "Nul72Contract", "Nul72Contract_Kauf" })
        {
            registry.AddPropertyAggregate(new PropertyAggregateDefinition(
                label,
                "Status",
                "Kaufpreis",
                AggregateFunction.Count));
        }

        var visitor = new CypherQueryVisitor(
            typeof(Nul72Contract),
            hasComplexProperties: false,
            indexCatalog: AgeIndexCatalog.From(registry));
        visitor.Visit(Nodes72()
            .GroupBy(contract => contract.Status)
            .Select(group => new { group.Key, Average = group.Average(contract => contract.Kaufpreis) })
            .Expression);

        Assert.Null(visitor.Query.Plan);
    }

    [Fact]
    public void PagePlanMovesOffsetBeforeComplexPropertyLoad()
    {
        var query = TranslateComplex(Nodes72Complex()
            .OrderBy(node => node.Name)
            .Skip(1000)
            .Take(100).Expression);

        var plan = Assert.IsType<AgePageSelectionPlan>(query.Plan);
        Assert.Equal("Name", plan.SortProperty);
        Assert.Equal(1000, plan.Skip);
        Assert.Equal(100, plan.Limit);
        Assert.Contains("__graphModelComplexProperty", query.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("cvoya-page-plan:", query.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PagePlanUsesEqualityCompanionSourceWhenEveryLabelDeclaresTheSortProperty()
    {
        var query = TranslateComplex(
            Nodes72Complex()
                .OrderBy(node => node.Name)
                .Skip(20)
                .Take(10).Expression,
            ComplexRegistry(equality: true));

        var plan = Assert.IsType<AgePageSelectionPlan>(query.Plan);
        Assert.Equal(2, plan.Sources.Count);
        foreach (var label in new[] { "Nul72Complex", "Nul72ComplexChild" })
        {
            var source = Assert.Single(plan.Sources, candidate => candidate.Label == label);
            Assert.Equal(AgePhysicalName.IndexTable(label), source.TableName);
            Assert.Equal(AgePhysicalName.TextColumn(label, "Name"), source.ColumnName);
        }
    }

    [Fact]
    public void PagePlanKeepsAccessorOrderingWhenACompanionEqualityIsMissing()
    {
        var query = TranslateComplex(Nodes72Complex()
            .OrderBy(node => node.Name)
            .Skip(20)
            .Take(10).Expression);

        var plan = Assert.IsType<AgePageSelectionPlan>(query.Plan);
        Assert.Empty(plan.Sources);
    }

    [Fact]
    public void PagePlanKeepsAccessorOrderingForANonStringSortProperty()
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { "Nul96NumericPage", "Nul96NumericPageChild" })
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Rank"));
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Rank", PropertyIndexKind.Ordering));
        }

        var visitor = new CypherQueryVisitor(
            typeof(Nul96NumericPage),
            hasComplexProperties: true,
            indexCatalog: AgeIndexCatalog.From(registry));
        visitor.Visit(NodesFor<Nul96NumericPage>()
            .OrderBy(node => node.Rank)
            .Skip(20)
            .Take(10).Expression);

        var plan = Assert.IsType<AgePageSelectionPlan>(visitor.Query.Plan);
        Assert.Empty(plan.Sources);
    }

    [Fact]
    public void PagePlanDetectsEscapedAndUnicodeNamesFromTheAst()
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { "Nul96Ünïcode", "Nul96ÜnïcodeChild" })
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "nïck name"));
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "nïck name", PropertyIndexKind.Ordering));
        }

        var visitor = new CypherQueryVisitor(
            typeof(Nul96UnicodePage),
            hasComplexProperties: true,
            indexCatalog: AgeIndexCatalog.From(registry));
        visitor.Visit(NodesFor<Nul96UnicodePage>()
            .OrderBy(node => node.Nickname)
            .Skip(20)
            .Take(10).Expression);

        var plan = Assert.IsType<AgePageSelectionPlan>(visitor.Query.Plan);
        Assert.Equal("nïck name", plan.SortProperty);
        Assert.Equal(2, plan.Sources.Count);
        var physicalLabel = SerializationBridge.GetRootStorageName("Nul96Ünïcode", relationship: false);
        var source = Assert.Single(plan.Sources, candidate => candidate.Label == physicalLabel);
        Assert.Equal(
            AgePhysicalName.TextColumn(physicalLabel, "nïck name"),
            source.ColumnName);
        // The label is encoded to a native physical name, while the Unicode property still needs
        // backtick escaping in the rendered ORDER BY; the AST detector handles both.
        Assert.Contains("`nïck name`", visitor.Query.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeStringEqualityUsesContainmentPlan()
    {
        var query = TranslatePerson(NodesPerson()
            .Where(person => person.UnicodeName == "Müller-😀-東京-<b>&\"quote\"-5000").Expression);

        var plan = AssertPlan(query, "containment");
        var sql = BuildSql(query);

        Assert.Contains("properties @> (jsonb_build_object('UnicodeName'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("agtype_access_operator(VARIADIC ARRAY[properties, '\"UnicodeName\"'] =", sql, StringComparison.Ordinal);
        Assert.Equal(["Nul72Person"], plan.Labels);
    }

    [Fact]
    public void UndeclaredPropertyKeepsAgePath()
    {
        var query = TranslatePerson(NodesPerson()
            .Where(person => person.Age == 42).Expression);

        Assert.Null(query.Plan);
    }

    [Fact]
    public void TypeSafeDeclarationDrivesTheGeneralizedIndexPlan()
    {
        var registry = new SchemaRegistry();
        registry.Node<Nul70Contract>().AddPropertyIndex(contract => contract.Status);
        registry.Node<Nul70ContractKauf>().AddPropertyIndex(contract => contract.Status);
        var catalog = AgeIndexCatalog.From(registry);
        var table = AgePhysicalName.IndexTable("Nul70Contract");
        var column = AgePhysicalName.TextColumn("Nul70Contract", "Status");
        Assert.Equal(table, catalog.GetTableName("Nul70Contract"));

        var visitor = new CypherQueryVisitor(
            typeof(Nul70Contract),
            hasComplexProperties: false,
            indexCatalog: catalog);
        visitor.Visit(Nodes().Where(contract => contract.Status == "aktiv").Expression);

        _ = AssertPlan(visitor.Query, "filter");
        var sql = BuildSql(visitor.Query);
        Assert.Contains("\"" + table + "\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"" + column + "\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UndeclaredNonAdditiveAggregateKeepsAgePath()
    {
        var query = Translate(Nodes()
            .GroupBy(contract => contract.Status)
            .Select(group => new { group.Key, Average = group.Average(contract => contract.Price) }).Expression);

        Assert.Null(query.Plan);
    }

    [Fact]
    public void NonStringStatusKeepsAgePath()
    {
        var query = TranslateNumeric(NumericNodes()
            .GroupBy(node => node.Status)
            .Select(group => new { group.Key, Count = group.Count() }).Expression);

        Assert.Null(query.Plan);
    }

    [Fact]
    public void TypedPlanBindsTokensWithoutTransportMarker()
    {
        var plan = new AgeSqlPlan(
            ["A", "B"],
            "SELECT 1 FROM " + AgeIndexTable.GraphToken + ".\"A\" WHERE p_abc = " + AgeIndexTable.ParameterToken,
            "test",
            ["A"]);
        var sql = AgeQueryRunner.BuildCommandText(
            "g",
            "MATCH (src:A) RETURN src.Status AS s",
            ["s"],
            postProcessing: null,
            "@p",
            plan);

        Assert.Equal("SELECT 1 FROM \"g\".\"A\" WHERE p_abc = @p", sql);
        Assert.DoesNotContain("cvoya-index-column", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionSqlIsIdempotentAndLengthBounded()
    {
        var textColumn = AgePhysicalName.TextColumn("A label", "Status");
        var property = new AgeIndexedProperty(
            "A label",
            "Status",
            AgeIndexTable.TableName("A label"),
            textColumn,
            AgePhysicalName.TextIndex("A label", "Status"));
        var createTable = AgeIndexTable.CreateTableSql("graph_name", "A label", [property], []);
        Assert.Contains("CREATE TABLE IF NOT EXISTS", createTable, StringComparison.Ordinal);
        Assert.Contains("\"" + textColumn + "\" text", createTable, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY", createTable, StringComparison.Ordinal);
        Assert.DoesNotContain("status_text", createTable, StringComparison.Ordinal);

        var createIndex = AgeIndexTable.CreateIndexSql("graph_name", "A label", property);
        Assert.Contains("CREATE INDEX IF NOT EXISTS", createIndex, StringComparison.Ordinal);
        Assert.Contains("(\"" + textColumn + "\")", createIndex, StringComparison.Ordinal);

        var longLabel = new string('L', 80);
        Assert.True(AgePhysicalName.TextIndex(longLabel, "Status").Length <= 63);
        Assert.True(AgeIndexTable.TableName(longLabel).Length <= 63);
    }

    [Fact]
    public void StatusAndKaufpreisAggregateNamesAreGeneralizedAndBounded()
    {
        var table = AgePhysicalName.AggregateTable("Contract", "Status", "Kaufpreis");
        Assert.StartsWith("Contract__agg_", table, StringComparison.Ordinal);
        Assert.Equal(
            table + "_ins_fn",
            AgePhysicalName.AggregateFunction("Contract", "Status", "Kaufpreis", "ins"));
        Assert.Equal(
            table + "_upd_trg",
            AgePhysicalName.AggregateTrigger("Contract", "Status", "Kaufpreis", "upd"));

        var longLabel = new string('L', 80);
        Assert.StartsWith("idx_", AgePhysicalName.IndexTable(longLabel), StringComparison.Ordinal);
        Assert.StartsWith(
            "agg_",
            AgePhysicalName.AggregateTable(longLabel, "Status", "Kaufpreis"),
            StringComparison.Ordinal);
        Assert.True(AgePhysicalName.IndexTable(longLabel).Length <= 63);
        Assert.True(AgePhysicalName.AggregateTable(longLabel, "Status", "Kaufpreis").Length <= 63);
        Assert.DoesNotContain("num_agg", AgePhysicalName.AggregateTable(longLabel, "Status", "Kaufpreis"));
        Assert.DoesNotContain("status", AgePhysicalName.IndexTable(longLabel));
        Assert.NotEqual(
            AgePhysicalName.AggregateFunction("Contract", "Status", "Preis", "ins"),
            AgePhysicalName.AggregateFunction("Contract", "Status", "Kaufpreis", "ins"));
    }

    [Fact]
    public void IndexedValueExtractionUsesTheAgtypeTextCast()
    {
        Assert.Equal("(properties::text::jsonb) ->> 'Status'", AgeIndexTable.TextValueSql("Status"));
    }

    [Fact]
    public void PagePlanDetectsSingleLabelSortPropertyFromTheOrderByClause()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition("Nul97SinglePage", "Name"));
        registry.AddPropertyIndex(new PropertyIndexDefinition(
            "Nul97SinglePage",
            "Name",
            PropertyIndexKind.Ordering));

        var visitor = new CypherQueryVisitor(
            typeof(Nul97SinglePage),
            hasComplexProperties: true,
            indexCatalog: AgeIndexCatalog.From(registry));
        visitor.Visit(NodesFor<Nul97SinglePage>()
            .OrderBy(node => node.Name)
            .Skip(2)
            .Take(3).Expression);

        var plan = Assert.IsType<AgePageSelectionPlan>(visitor.Query.Plan);
        Assert.Equal("Name", plan.SortProperty);
        Assert.Equal(2, plan.Skip);
        Assert.Equal(3, plan.Limit);
        Assert.True(plan.ClauseLevelPaging);
        Assert.Contains("[rels]->", visitor.Query.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[rels*", visitor.Query.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FilteredSingleLabelPagingKeepsTheAgePath()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition("Nul97SinglePage", "Name"));
        registry.AddPropertyIndex(new PropertyIndexDefinition(
            "Nul97SinglePage",
            "Name",
            PropertyIndexKind.Ordering));

        var visitor = new CypherQueryVisitor(
            typeof(Nul97SinglePage),
            hasComplexProperties: true,
            indexCatalog: AgeIndexCatalog.From(registry));
        visitor.Visit(NodesFor<Nul97SinglePage>()
            .Where(node => node.Name != null)
            .OrderBy(node => node.Name)
            .Skip(2)
            .Take(3).Expression);

        Assert.Null(visitor.Query.Plan);
    }

    [Fact]
    public void NonNativeSingleLabelAverageUsesAggregateCompanionPlan()
    {
        const string label = "Nul97ÜnïcodeAgg";
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Region"));
        registry.AddPropertyAggregate(new PropertyAggregateDefinition(
            label,
            "Region",
            "Amount",
            AggregateFunction.Count | AggregateFunction.Average));

        var visitor = new CypherQueryVisitor(
            typeof(Nul97UnicodeAgg),
            hasComplexProperties: false,
            indexCatalog: AgeIndexCatalog.From(registry));
        visitor.Visit(NodesFor<Nul97UnicodeAgg>()
            .GroupBy(node => node.Region)
            .Select(group => new { group.Key, Average = group.Average(node => node.Amount) })
            .Expression);

        var plan = AssertPlan(visitor.Query, "aggregate");
        var physicalLabel = SerializationBridge.GetRootStorageName(label, relationship: false);
        Assert.Equal([physicalLabel], plan.Labels);
        Assert.Contains(
            AgePhysicalName.AggregateTable(physicalLabel, "Region", "Amount"),
            plan.Sql,
            StringComparison.Ordinal);
        Assert.Contains(physicalLabel, visitor.Query.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NonNativeSingleLabelReadUsesThePhysicalStorageLabel()
    {
        const string label = "Nul97ÜnïcodeAgg";
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Region"));

        var visitor = new CypherQueryVisitor(
            typeof(Nul97UnicodeAgg),
            hasComplexProperties: false,
            indexCatalog: AgeIndexCatalog.From(registry));
        visitor.Visit(NodesFor<Nul97UnicodeAgg>().Expression);

        var physicalLabel = SerializationBridge.GetRootStorageName(label, relationship: false);
        Assert.Contains("MATCH (src:" + physicalLabel + ")", visitor.Query.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("MATCH (src:`" + label + "`)", visitor.Query.Text, StringComparison.Ordinal);
    }

    private static SchemaRegistry Registry(params (string Label, string Property)[] indexes)
    {
        var registry = new SchemaRegistry();
        foreach (var (label, property) in indexes)
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, property));
        }

        return registry;
    }

    private static SchemaRegistry Registry72()
    {
        var registry = Registry(
            ("Nul72Contract", "Status"),
            ("Nul72Contract_Kauf", "Status"));
        foreach (var label in new[] { "Nul72Contract", "Nul72Contract_Kauf" })
        {
            registry.AddPropertyAggregate(new PropertyAggregateDefinition(
                label,
                "Status",
                "Kaufpreis",
                AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
                AggregateFunction.Max | AggregateFunction.Average));
        }

        return registry;
    }

    private static IQueryable<Nul72Contract> Nodes72() =>
        new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "nul72_translation").Graph.Nodes<Nul72Contract>();

    private static IQueryable<Nul72Complex> Nodes72Complex() =>
        new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "nul72_translation").Graph.Nodes<Nul72Complex>();

    private static CypherQuery Translate72(System.Linq.Expressions.Expression expression)
    {
        var visitor = new CypherQueryVisitor(
            typeof(Nul72Contract),
            hasComplexProperties: false,
            indexCatalog: AgeIndexCatalog.From(Registry72()));
        visitor.Visit(expression);
        return visitor.Query;
    }

    private static CypherQuery TranslateComplex(
        System.Linq.Expressions.Expression expression,
        SchemaRegistry? registry = null)
    {
        registry ??= ComplexRegistry(equality: false);
        var visitor = new CypherQueryVisitor(
            typeof(Nul72Complex),
            hasComplexProperties: true,
            indexCatalog: AgeIndexCatalog.From(registry));
        visitor.Visit(expression);
        return visitor.Query;
    }

    private static SchemaRegistry ComplexRegistry(bool equality)
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { "Nul72Complex", "Nul72ComplexChild" })
        {
            if (equality)
            {
                registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Name"));
            }

            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Name", PropertyIndexKind.Ordering));
        }

        return registry;
    }

    private static IQueryable<T> NodesFor<T>()
        where T : class, INode =>
        new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "nul72_translation").Graph.Nodes<T>();

    private static IQueryable<Nul72Person> NodesPerson() =>
        new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "nul72_translation").Graph.Nodes<Nul72Person>();

    private static CypherQuery TranslatePerson(System.Linq.Expressions.Expression expression)
    {
        var visitor = new CypherQueryVisitor(
            typeof(Nul72Person),
            hasComplexProperties: false,
            indexCatalog: AgeIndexCatalog.From(Registry(("Nul72Person", "UnicodeName"))));
        visitor.Visit(expression);
        return visitor.Query;
    }

    private static AgeSqlPlan AssertPlan(CypherQuery query, string kind)
    {
        var plan = Assert.IsType<AgeSqlPlan>(query.Plan);
        Assert.Equal(kind, plan.Kind);
        return plan;
    }

    private static IQueryable<Nul70Contract> Nodes() =>
        new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "nul70_translation").Graph.Nodes<Nul70Contract>();

    private static IQueryable<Nul91AggContract> NodesAgg() =>
        new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "nul91_translation").Graph.Nodes<Nul91AggContract>();

    private static IQueryable<Nul70NumericStatus> NumericNodes() =>
        new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "nul70_translation").Graph.Nodes<Nul70NumericStatus>();

    private static CypherQuery Translate(System.Linq.Expressions.Expression expression) =>
        Translate(expression, Registry(("Nul70Contract", "Status"), ("Nul70Contract_Kauf", "Status")));

    private static CypherQuery Translate(System.Linq.Expressions.Expression expression, SchemaRegistry registry)
    {
        var visitor = new CypherQueryVisitor(
            typeof(Nul70Contract),
            hasComplexProperties: false,
            indexCatalog: AgeIndexCatalog.From(registry));
        visitor.Visit(expression);
        return visitor.Query;
    }

    private static CypherQuery TranslateNumeric(System.Linq.Expressions.Expression expression)
    {
        var visitor = new CypherQueryVisitor(
            typeof(Nul70NumericStatus),
            hasComplexProperties: false,
            indexCatalog: AgeIndexCatalog.Empty);
        visitor.Visit(expression);
        return visitor.Query;
    }

    private static string BuildSql(CypherQuery query)
    {
        var (cypher, columns, _) = AgeQueryRunner.NormalizeCommandForTesting(
            query.Text,
            query.ProjectionColumns ?? []);
        return AgeQueryRunner.BuildCommandText(
            "nul70",
            cypher,
            columns,
            query.PostProcessing,
            "$1",
            query.Plan);
    }

    [Node("Nul70Contract")]
    public record Nul70Contract : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? Status { get; set; }
        public long Price { get; set; }
    }

    [Node("Nul70Contract_Kauf")]
    public record Nul70ContractKauf : Nul70Contract;

    [Node("Nul70NumericStatus")]
    public record Nul70NumericStatus : Node
    {
        public int Status { get; set; }
    }

    [Node("Nul72Contract")]
    public record Nul72Contract : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? Status { get; set; }
        public long Kaufpreis { get; set; }
    }

    [Node("Nul72Contract_Kauf")]
    public record Nul72ContractKauf : Nul72Contract;

    public sealed class Nul72Address
    {
        public string City { get; set; } = string.Empty;
    }

    [Node("Nul72Complex")]
    public record Nul72Complex : Node
    {
        public string Name { get; set; } = string.Empty;
        public Nul72Address? Address { get; set; }
    }

    [Node("Nul72ComplexChild")]
    public record Nul72ComplexChild : Nul72Complex;

    [Node("Nul96NumericPage")]
    public record Nul96NumericPage : Node
    {
        public long Rank { get; set; }

        public Nul72Address? Address { get; set; }
    }

    [Node("Nul96NumericPageChild")]
    public record Nul96NumericPageChild : Nul96NumericPage;

    [Node("Nul96Ünïcode")]
    public record Nul96UnicodePage : Node
    {
        [Property(Label = "nïck name")]
        public string? Nickname { get; set; }

        public Nul72Address? Address { get; set; }
    }

    [Node("Nul96ÜnïcodeChild")]
    public record Nul96UnicodePageChild : Nul96UnicodePage;

    [Node("Nul91AggContract")]
    public record Nul91AggContract : Node
    {
        [Property(Label = "kategorie")]
        public string? Kategorie { get; set; }

        [Property(Label = "betrag")]
        public long Betrag { get; set; }
    }

    [Node("Nul91AggContract_Kauf")]
    public record Nul91AggContractKauf : Nul91AggContract;

    [Node("Nul97SinglePage")]
    public record Nul97SinglePage : Node
    {
        public string? Name { get; set; }

        public Nul72Address? Address { get; set; }
    }

    [Node("Nul97ÜnïcodeAgg")]
    public record Nul97UnicodeAgg : Node
    {
        public string? Region { get; set; }

        public long Amount { get; set; }
    }

    [Node("Nul72Person")]
    public record Nul72Person : Node
    {
        public string Name { get; set; } = string.Empty;
        public string? UnicodeName { get; set; }
        public int Age { get; set; }
    }
}
