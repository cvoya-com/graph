// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;

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
    public void GroupedCountLowersToPartialCountsAndSqlCombine()
    {
        var query = Nodes()
            .GroupBy(contract => contract.Status)
            .Select(group => new { group.Key, Count = group.Count() });

        var translated = Translate(query.Expression);
        var sql = BuildSql(translated);

        Assert.Contains("count(src) AS __a0", translated.Text, StringComparison.Ordinal);
        Assert.Contains("UNION ALL", translated.Text, StringComparison.Ordinal);
        Assert.Contains("sum((age_result.\"Count\")::text::numeric)::text::ag_catalog.agtype AS \"Count\"", sql, StringComparison.Ordinal);
        Assert.EndsWith("GROUP BY age_result.\"Key\"", sql, StringComparison.Ordinal);
    }

    private static IQueryable<Nul53Contract> Nodes() =>
        new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "nul53_translation").Graph.Nodes<Nul53Contract>();

    private static CypherQuery Translate(System.Linq.Expressions.Expression expression)
    {
        var visitor = new CypherQueryVisitor(typeof(Nul53Contract), hasComplexProperties: false);
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
            "$1");
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
