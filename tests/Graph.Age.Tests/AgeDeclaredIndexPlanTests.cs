// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using System.Linq.Expressions;
using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Querying.Cypher.Lowering;
using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.Age.Schema;

/// <summary>
/// Translation-level coverage for the generalized declared-index plans. Every declaration here uses
/// a property name that is not one of the historical nGen names, so a regression to a hard-coded
/// <c>Status</c>, <c>Kaufpreis</c>, or <c>Name</c> lookup fails these tests.
/// </summary>
public sealed class AgeDeclaredIndexPlanTests
{
    private const string TariffLabel = "Nul91Tariff";
    private const string TariffChildLabel = "Nul91TariffChild";
    private const string SingleTariffLabel = "Nul91SingleTariff";
    private const string LongLabel = "Nul91Long_0123456789012345678901234567890123456789012345678901234567890123";
    private const string LongChildLabel = LongLabel + "Child";
    private const string LongProperty =
        "nul91_bucket_012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789";

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    public AgeDeclaredIndexPlanTests()
    {
        AgeTypeHierarchyRegistry.Instance.Register(TariffLabel, TariffChildLabel);
        AgeTypeHierarchyRegistry.Instance.Register(LongLabel, LongChildLabel);
    }

    [Fact]
    public void HierarchicalRemappedEqualityFilterUsesCompanionPlan()
    {
        var query = Translate(
            Nodes<Tariff>().Where(tariff => tariff.Tier == "gold").Expression,
            Registry(),
            typeof(Tariff));

        _ = AssertPlan(query, "filter");
        var sql = BuildSql(query);

        foreach (var label in new[] { TariffLabel, TariffChildLabel })
        {
            Assert.Contains(
                $"\"{AgePhysicalName.TextColumn(label, "tarif_stufe")}\" =",
                sql,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain("'Tier'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleLabelRemappedEqualityFilterUsesContainmentPlan()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(SingleTariffLabel, "tarif_stufe"));

        var query = Translate(
            Nodes<SingleTariff>().Where(tariff => tariff.Tier == "gold").Expression,
            registry,
            typeof(SingleTariff),
            hasComplexProperties: true);

        var plan = AssertPlan(query, "containment");
        var sql = BuildSql(query);

        Assert.Equal([SingleTariffLabel], plan.Labels);
        Assert.Contains("properties @> (jsonb_build_object('tarif_stufe'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'Tier'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupByRemappedPropertyUsesCompanionPlan()
    {
        var query = Translate(
            Nodes<Tariff>()
                .GroupBy(tariff => tariff.Tier)
                .Select(group => new { group.Key, Count = group.Count() })
                .Expression,
            Registry(),
            typeof(Tariff));

        _ = AssertPlan(query, "group");
        var sql = BuildSql(query);
        var column = AgePhysicalName.TextColumn(TariffLabel, "tarif_stufe");
        var table = AgePhysicalName.IndexTable(TariffLabel);

        Assert.Contains($"\"{table}\"", sql, StringComparison.Ordinal);
        Assert.Contains($"\"{column}\"", sql, StringComparison.Ordinal);
        Assert.Contains($"\"{AgePhysicalName.TextColumn(TariffChildLabel, "tarif_stufe")}\"", sql, StringComparison.Ordinal);
        Assert.Contains("count(*)", sql, StringComparison.Ordinal);
        // The outer projection reads the union alias, never a per-label column that the
        // subquery does not expose.
        Assert.Contains("SELECT to_jsonb(\"__group\")", sql, StringComparison.Ordinal);
        Assert.DoesNotContain($"to_jsonb(\"{column}\")", sql, StringComparison.Ordinal);
        // Reserved aliases arrive backtick-quoted from the Cypher AST; the SQL alias must be plain.
        Assert.Contains("AS \"Count\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("`Count`", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("agtype_access_operator(VARIADIC ARRAY[properties, '\"tarif_stufe\"'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeSafeAggregateDeclarationBuildsNumericAggregatePlan()
    {
        var registry = new SchemaRegistry();
        foreach (var declare in new Action<SchemaRegistry>[]
                 {
                     target => target.Node<Tariff>()
                         .AddPropertyIndex(tariff => tariff.Tier)
                         .AddPropertyAggregate(
                             tariff => tariff.Tier,
                             tariff => tariff.Amount,
                             AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
                             AggregateFunction.Max | AggregateFunction.Average),
                     target => target.Node<TariffChild>()
                         .AddPropertyIndex(tariff => tariff.Tier)
                         .AddPropertyAggregate(
                             tariff => tariff.Tier,
                             tariff => tariff.Amount,
                             AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
                             AggregateFunction.Max | AggregateFunction.Average),
                 })
        {
            declare(registry);
        }

        var query = Translate(
            Nodes<Tariff>()
                .GroupBy(tariff => tariff.Tier)
                .Select(group => new
                {
                    group.Key,
                    Average = group.Average(tariff => tariff.Amount),
                })
                .Expression,
            registry,
            typeof(Tariff));

        var plan = AssertPlan(query, "aggregate");
        var sql = BuildSql(query);

        Assert.Equal([TariffLabel, TariffChildLabel], plan.Labels);
        foreach (var label in new[] { TariffLabel, TariffChildLabel })
        {
            Assert.Contains(
                $"\"{AgePhysicalName.AggregateTable(label, "tarif_stufe", "Amount")}\"",
                sql,
                StringComparison.Ordinal);
        }

        Assert.Contains("sum(\"sum_value\")", sql, StringComparison.Ordinal);
        Assert.Contains("NULLIF(sum(\"cnt\"), 0)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'Amount'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DistinctRemappedPropertyUsesCompanionPlan()
    {
        var query = Translate(
            Nodes<Tariff>().Select(tariff => tariff.Tier).Distinct().Expression,
            Registry(),
            typeof(Tariff));

        _ = AssertPlan(query, "distinct");
        var sql = BuildSql(query);

        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.Contains(
            $"\"{AgePhysicalName.TextColumn(TariffLabel, "tarif_stufe")}\"",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AverageOverArbitraryValuePropertyUsesAggregatePlan()
    {
        var query = Translate(
            Nodes<Tariff>()
                .GroupBy(tariff => tariff.Tier)
                .Select(group => new { group.Key, Value = group.Average(tariff => tariff.Amount) })
                .Expression,
            Registry(),
            typeof(Tariff));

        _ = AssertPlan(query, "aggregate");
        var sql = BuildSql(query);

        Assert.Contains(
            $"\"{AgePhysicalName.AggregateTable(TariffLabel, "tarif_stufe", "Amount")}\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("sum(\"sum_value\")", sql, StringComparison.Ordinal);
        Assert.Contains("NULLIF(sum(\"cnt\"), 0)", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT to_jsonb(\"__group\")", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Kaufpreis", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MinAndMaxOverArbitraryValuePropertyUseAggregatePlan()
    {
        var min = Translate(
            Nodes<Tariff>()
                .GroupBy(tariff => tariff.Tier)
                .Select(group => new { group.Key, Value = group.Min(tariff => tariff.Amount) })
                .Expression,
            Registry(),
            typeof(Tariff));
        var max = Translate(
            Nodes<Tariff>()
                .GroupBy(tariff => tariff.Tier)
                .Select(group => new { group.Key, Value = group.Max(tariff => tariff.Amount) })
                .Expression,
            Registry(),
            typeof(Tariff));

        _ = AssertPlan(min, "aggregate");
        _ = AssertPlan(max, "aggregate");
        Assert.Contains("to_jsonb(min(\"min_value\"))", BuildSql(min), StringComparison.Ordinal);
        Assert.Contains("to_jsonb(max(\"max_value\"))", BuildSql(max), StringComparison.Ordinal);
    }

    [Fact]
    public void NegatedPredicateOnArbitraryPropertyExcludesMissingValues()
    {
        var query = Translate(
            Nodes<Tariff>().Where(tariff => tariff.Tier != "gold").Expression,
            Registry(),
            typeof(Tariff));

        _ = AssertPlan(query, "filter");
        var sql = BuildSql(query);
        var column = AgePhysicalName.TextColumn(TariffLabel, "tarif_stufe");

        Assert.Contains($"\"{column}\" IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains($"\"{column}\" IS DISTINCT FROM", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UndeclaredPropertyKeepsOrdinaryAgePath()
    {
        var query = Translate(
            Nodes<Tariff>().Where(tariff => tariff.Region == "north").Expression,
            Registry(),
            typeof(Tariff));

        Assert.Null(query.Plan);
    }

    [Fact]
    public void DeclaredAggregateWithoutValueDeclarationStillUsesAgePath()
    {
        // The group key is declared, but no aggregate definition exists for Amount. The builder
        // must not accelerate the average; that would silently change the aggregate source.
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition(TariffLabel, "tarif_stufe"));

        var query = Translate(
            Nodes<Tariff>()
                .GroupBy(tariff => tariff.Tier)
                .Select(group => new { group.Key, Value = group.Average(tariff => tariff.Amount) })
                .Expression,
            registry,
            typeof(Tariff));

        Assert.Null(query.Plan);
    }

    [Fact]
    public void LongLabelAndPropertyNamesUseBoundedPhysicalNames()
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { LongLabel, LongChildLabel })
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, LongProperty));
            registry.AddPropertyAggregate(new PropertyAggregateDefinition(
                label,
                LongProperty,
                "Value",
                AggregateFunction.Count | AggregateFunction.Average));
        }

        var query = Translate(
            Nodes<LongNameWidget>()
                .GroupBy(widget => widget.Bucket)
                .Select(group => new { group.Key, Count = group.Count() })
                .Expression,
            registry,
            typeof(LongNameWidget));

        _ = AssertPlan(query, "group");
        Assert.True(LongLabel.Length > 63, "The test label must exceed the PostgreSQL identifier limit.");
        Assert.True(LongProperty.Length > 63, "The test property must exceed the PostgreSQL identifier limit.");
        Assert.True(AgeIndexTable.TableName(LongLabel).Length <= 63);
        Assert.True(AgeIndexTable.TableName(LongChildLabel).Length <= 63);
        Assert.True(AgePhysicalName.TextColumn(LongLabel, LongProperty).Length <= 63);
        Assert.True(AgePhysicalName.AggregateTable(LongLabel, LongProperty, "Value").Length <= 63);
        Assert.True(AgePhysicalName.TextIndex(LongLabel, LongProperty).Length <= 63);
    }

    private static SchemaRegistry Registry()
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { TariffLabel, TariffChildLabel })
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

    private static IQueryable<T> Nodes<T>()
        where T : class, INode =>
        new AgeGraphStore(ConnectionString, "nul91_translation").Graph.Nodes<T>();

    private static CypherQuery Translate(
        Expression expression,
        SchemaRegistry registry,
        Type rootType,
        bool hasComplexProperties = false)
    {
        var visitor = new CypherQueryVisitor(
            rootType,
            hasComplexProperties: hasComplexProperties,
            indexCatalog: AgeIndexCatalog.From(registry));
        visitor.Visit(expression);
        return visitor.Query;
    }

    private static AgeSqlPlan AssertPlan(CypherQuery query, string kind)
    {
        Assert.True(query.Plan is AgeSqlPlan, $"Plan was {query.Plan?.GetType().Name ?? "null"}. Text:\n{query.Text}\nPostProcessing: {query.PostProcessing}");
        var plan = Assert.IsType<AgeSqlPlan>(query.Plan);
        Assert.Equal(kind, plan.Kind);
        return plan;
    }

    private static string BuildSql(CypherQuery query)
    {
        var (cypher, columns, _) = AgeQueryRunner.NormalizeCommandForTesting(
            query.Text,
            query.ProjectionColumns ?? []);
        return AgeQueryRunner.BuildCommandText(
            "nul91",
            cypher,
            columns,
            query.PostProcessing,
            "$1",
            query.Plan);
    }

    [Node(TariffLabel)]
    public record Tariff : Node
    {
        public string Name { get; set; } = string.Empty;

        [Property(Label = "tarif_stufe")]
        public string? Tier { get; set; }

        public string? Region { get; set; }

        public long Amount { get; set; }
    }

    [Node(TariffChildLabel)]
    public record TariffChild : Tariff;

    [Node(SingleTariffLabel)]
    public record SingleTariff : Node
    {
        public string Name { get; set; } = string.Empty;

        [Property(Label = "tarif_stufe")]
        public string? Tier { get; set; }

        public long Amount { get; set; }
    }

    [Node(LongChildLabel)]
    public record LongNameWidgetChild : LongNameWidget;

    [Node(LongLabel)]
    public record LongNameWidget : Node
    {
        [Property(Label = LongProperty)]
        public string? Bucket { get; set; }

        public long Value { get; set; }
    }
}
