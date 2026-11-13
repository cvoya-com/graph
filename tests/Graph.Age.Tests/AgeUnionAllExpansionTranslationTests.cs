// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Querying.Cypher.Lowering;
using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.Age.Schema;
using Cvoya.Graph.Cypher;
using Cvoya.Graph.Cypher.Ast;
using Cvoya.Graph.Cypher.Ast.Expressions;

public sealed class AgeUnionAllExpansionTranslationTests
{
    public AgeUnionAllExpansionTranslationTests()
    {
        AgeTypeHierarchyRegistry.Instance.Register("Nul53Contract", "Nul53Contract_Kauf");
        AgeTypeHierarchyRegistry.Instance.Register("Nul53Contract_Kauf", "Nul53Contract_Miete");
    }

    [Fact]
    public void PagingLowersToBoundedUnionAllBranchesAndSqlPage()
    {
        var query = Nodes()
            .OrderBy(contract => contract.Name)
            .Skip(1_000)
            .Take(100);

        var translated = Translate(query.Expression);
        var sql = BuildSql(translated);

        Assert.Contains("MATCH (src:Nul53Contract)", translated.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:Nul53Contract_Kauf)", translated.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:Nul53Contract_Miete)", translated.Text, StringComparison.Ordinal);
        Assert.Equal(2, translated.Text.Split("UNION ALL", StringSplitOptions.None).Length - 1);
        Assert.Contains("src.Name AS __age_union_sort0", translated.Text, StringComparison.Ordinal);
        Assert.Contains("ORDER BY src.Name\nLIMIT 1100", translated.Text, StringComparison.Ordinal);
        Assert.EndsWith(
            "ORDER BY age_result.\"__age_union_sort0\" ASC OFFSET 1000 LIMIT 100",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SKIP 1000", translated.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescendingOrderingKeepsDirectionInBranchAndWrapper()
    {
        var query = Nodes()
            .OrderByDescending(contract => contract.Name)
            .Take(10);

        var translated = Translate(query.Expression);
        var sql = BuildSql(translated);

        Assert.Contains("ORDER BY src.Name DESC\nLIMIT 10", translated.Text, StringComparison.Ordinal);
        Assert.EndsWith(
            "ORDER BY age_result.\"__age_union_sort0\" DESC LIMIT 10",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DistinctLowersToUnionAllAndSqlDistinct()
    {
        var query = Nodes()
            .Select(contract => contract.Status)
            .Distinct();

        var translated = Translate(query.Expression);
        var sql = BuildSql(translated);

        Assert.Contains("UNION ALL", translated.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("RETURN DISTINCT", translated.Text, StringComparison.Ordinal);
        Assert.StartsWith("SELECT DISTINCT age_result.", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PushesPagingBeforeComplexPropertyCollect()
    {
        AgeTypeHierarchyRegistry.Instance.Register("Nul68Complex", "Nul68ComplexChild");

        var statement = new CypherStatement(
            [
                new MatchClause(
                    [new PathPattern([new NodePattern("src", ["Nul68Complex"])])],
                    optional: false),
                new EntityProjectionClause(
                    EntityProjectionShape.Node,
                    "src",
                    relationshipAlias: null,
                    targetAlias: null,
                    loadSourceProperties: true,
                    loadTargetProperties: false),
                new OrderByClause(
                    [new OrderByItem(new PropertyAccess(new VariableRef("src"), "Name"), descending: false)]),
                new SkipClause(new Literal(1_000)),
                new LimitClause(new Literal(100)),
            ],
            new Dictionary<string, object?>());

        var projected = new AgeEntityProjectionPass().Run(statement);
        var expanded = new AgeUnionAllExpansionPass().Run(projected);
        var rendered = new CypherRenderer(AgeDialect.Instance).Render(expanded);

        var page = rendered.Text.IndexOf("LIMIT 1100", StringComparison.Ordinal);
        var propertyMatch = rendered.Text.IndexOf("OPTIONAL MATCH", StringComparison.Ordinal);
        Assert.True(page >= 0, "the branch page must be rendered");
        Assert.True(
            page < propertyMatch,
            "the branch page must run before the complex-property match and collect");
        Assert.Equal(2, rendered.Text.Split("LIMIT 1100", StringSplitOptions.None).Length - 1);
        Assert.Contains("UNION ALL", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupedCountLowersToPartialCountsAndSqlCombine()
    {
        var query = Nodes()
            .GroupBy(contract => contract.Status)
            .Select(group => new { group.Key, Count = group.Count() });

        var translated = Translate(query.Expression);
        var sql = BuildSql(translated);

        Assert.Contains("count(1) AS __a0", translated.Text, StringComparison.Ordinal);
        Assert.Contains("UNION ALL", translated.Text, StringComparison.Ordinal);
        Assert.Contains("sum((age_result.\"Count\")::text::numeric)::text::ag_catalog.agtype AS \"Count\"", sql, StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY age_result.\"Key\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiLabelPattern_MergesRuntimeRegisteredDescendantsIntoExpansion()
    {
        // The planner supplies [Review582Base, Review582Derived] for the CLR hierarchy; a runtime
        // registration must still add its physical table to the expansion.
        AgeTypeHierarchyRegistry.Instance.Register("Review582Base", "Review582RuntimeChild");

        var query = Review582Nodes().Select(baseEntity => baseEntity.Name);
        var translated = Translate(query.Expression, typeof(Review582Base));

        Assert.Contains("MATCH (src:Review582Base)", translated.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:Review582Derived)", translated.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:Review582RuntimeChild)", translated.Text, StringComparison.Ordinal);
        Assert.Equal(2, translated.Text.Split("UNION ALL", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void GroupByWithoutKeyInProjection_KeepsContainmentFallback()
    {
        // The wrapper can only regroup by projected key columns; dropping the key must not merge
        // all branches into one group.
        var query = Nodes().GroupBy(contract => contract.Status).Select(group => group.Count());

        var translated = Translate(query.Expression);

        Assert.DoesNotContain("UNION ALL", translated.Text, StringComparison.Ordinal);
        Assert.Null(translated.PostProcessing?.Aggregation);
    }

    [Fact]
    public void GroupByWithCollidingOutputAlias_KeepsContainmentFallback()
    {
        // The output alias __key collides with the planner's grouping-key alias but only the count
        // is projected; key preservation must be judged by the projected source variable.
        var query = Nodes().GroupBy(contract => contract.Status).Select(group => new { __key = group.Count() });

        var translated = Translate(query.Expression);

        Assert.DoesNotContain("UNION ALL", translated.Text, StringComparison.Ordinal);
        Assert.Null(translated.PostProcessing?.Aggregation);
    }

    [Fact]
    public void DistinctUnionOfHierarchyQuery_DoesNotExpandInsideTheUnion()
    {
        var query = Nodes().Select(contract => contract.Name);
        var union = query.Union(query);

        var translated = Translate(union.Expression);

        Assert.Contains("UNION", translated.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("UNION ALL", translated.Text, StringComparison.Ordinal);
        Assert.Null(translated.PostProcessing);
    }

    [Fact]
    public void SetOperationBranchWithPaging_IsNotExpandedAndLiftsNothing()
    {
        var branch = Nodes().OrderBy(contract => contract.Name).Take(1).Select(contract => contract.Name);
        var combined = branch.Concat(branch);

        var translated = Translate(combined.Expression);

        Assert.Null(translated.PostProcessing);
        Assert.DoesNotContain("__age_union_sort", translated.Text, StringComparison.Ordinal);
        Assert.Equal(1, translated.Text.Split("UNION ALL", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void DistinctStatusLowersToStatusColumnPlan()
    {
        var query = Nodes()
            .Select(contract => contract.Status)
            .Distinct();

        var translated = TranslateWithStatusIndex(query.Expression);
        var sql = BuildSql(translated);

        var plan = Assert.IsType<AgeSqlPlan>(translated.Plan);
        Assert.Equal("distinct", plan.Kind);
        Assert.DoesNotContain("RETURN DISTINCT", translated.Text, StringComparison.Ordinal);
        Assert.Contains("to_jsonb(\"__group\")::text::ag_catalog.agtype", sql, StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY \"__group\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DistinctOnNonStatusPropertyLowersToUnionAllAndSqlDistinct()
    {
        var query = Nodes()
            .Select(contract => contract.Name)
            .Distinct();

        var translated = Translate(query.Expression);
        var sql = BuildSql(translated);

        Assert.Contains("UNION ALL", translated.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("RETURN DISTINCT", translated.Text, StringComparison.Ordinal);
        Assert.StartsWith("SELECT DISTINCT age_result.", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupedStatusCountLowersToStatusColumnPlan()
    {
        var query = Nodes()
            .GroupBy(contract => contract.Status)
            .Select(group => new { group.Key, Count = group.Count() });

        var translated = TranslateWithStatusIndex(query.Expression);
        var sql = BuildSql(translated);

        var plan = Assert.IsType<AgeSqlPlan>(translated.Plan);
        Assert.Equal("group", plan.Kind);
        Assert.Contains("UNION ALL", translated.Text, StringComparison.Ordinal);
        Assert.Contains("count(*)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("sum((age_result.", sql, StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY \"__group\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupedCountOnNonStatusPropertyLowersToPartialCountsAndSqlCombine()
    {
        var query = Nodes()
            .GroupBy(contract => contract.Name)
            .Select(group => new { group.Key, Count = group.Count() });

        var translated = Translate(query.Expression);
        var sql = BuildSql(translated);

        Assert.Contains("count(1) AS __a0", translated.Text, StringComparison.Ordinal);
        Assert.Contains("UNION ALL", translated.Text, StringComparison.Ordinal);
        Assert.Contains("sum((age_result.\"Count\")::text::numeric)::text::ag_catalog.agtype AS \"Count\"", sql, StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY age_result.\"Key\"", sql, StringComparison.Ordinal);
    }

    private static IQueryable<Nul53Contract> Nodes() =>
        new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "nul53_translation").Graph.Nodes<Nul53Contract>();

    private static IQueryable<Review582Base> Review582Nodes() =>
        new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "review582_translation").Graph.Nodes<Review582Base>();

    private static CypherQuery Translate(
        System.Linq.Expressions.Expression expression,
        Type? rootType = null)
    {
        var visitor = new CypherQueryVisitor(rootType ?? typeof(Nul53Contract), hasComplexProperties: false);
        visitor.Visit(expression);
        return visitor.Query;
    }

    private static CypherQuery TranslateWithStatusIndex(System.Linq.Expressions.Expression expression)
    {
        var registry = new SchemaRegistry();
        foreach (var label in new[] { "Nul53Contract", "Nul53Contract_Kauf", "Nul53Contract_Miete" })
        {
            registry.AddPropertyIndex(new PropertyIndexDefinition(label, "Status"));
        }

        var visitor = new CypherQueryVisitor(
            typeof(Nul53Contract),
            hasComplexProperties: false,
            indexCatalog: AgeIndexCatalog.From(registry));
        visitor.Visit(expression);
        return visitor.Query;
    }

    private static string BuildSql(CypherQuery query)
    {
        var (cypher, columns, _) = AgeQueryRunner.NormalizeCommandForTesting(
            query.Text,
            query.ProjectionColumns ?? []);
        return AgeQueryRunner.BuildCommandText(
            "nul53",
            cypher,
            columns,
            query.PostProcessing,
            "$1",
            query.Plan);
    }
}

[Node("Nul53Contract")]
public record Nul53Contract : Node
{
    public string Name { get; set; } = string.Empty;

    public string? Status { get; set; }
}

[Node("Nul53Contract_Kauf")]
public record Nul53Kauf : Nul53Contract;

[Node("Nul53Contract_Miete")]
public record Nul53Miete : Nul53Kauf;

[Node("Review582Base")]
public record Review582Base : Node
{
    public string Name { get; set; } = string.Empty;
}

[Node("Review582Derived")]
public record Review582Derived : Review582Base;
