// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using System.Linq.Expressions;
using System.Reflection;
using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.CompatibilityTests;

public sealed class AgeQueryExpressionTranslationTests
{
    [Node("TranslationMappedPropertyNode")]
    public sealed record TranslationMappedPropertyNode : Node
    {
        [Property(Label = "notiz text")]
        public string? Note { get; set; }
    }

    [Fact]
    public async Task ComposedPredicateProjectionOrderingAndPaging_UsesAgeLoweredCypher()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");
        var scope = "scope";
        var categories = new[] { "A", "C" };
        var prefix = "a";
        var targetScore = 15;
        var tolerance = 6;
        var year = 2024;
        var bonus = 2;
        var fallback = "(missing)";

#pragma warning disable CA1862 // Exercise the provider-translated normalization pipeline.
        var query = store.Graph.Nodes<QueryExpressionCoverageNode>()
            .Where(node =>
                node.Scope == scope &&
                categories.Contains(node.Category) &&
                (node.OptionalText == null || node.Name.Trim().ToLowerInvariant().StartsWith(prefix)) &&
                !node.Tags.Contains("blocked") &&
                Math.Abs(node.Score - targetScore) <= tolerance &&
                node.OccurredAt.Year == year)
            .OrderBy(node => node.Category)
            .ThenByDescending(node => node.Score)
            .Skip(1)
            .Take(3)
            .Select(node => new
            {
                node.Name,
                AdjustedScore = node.Score + bonus,
                Display = node.OptionalText == null ? fallback : node.OptionalText.ToUpperInvariant(),
            });
#pragma warning restore CA1862

        var translated = Translate(query);

        Assert.Contains("MATCH (src:QueryExpressionCoverageNode)", translated.Text, StringComparison.Ordinal);
        Assert.Contains("src.Category IN $p1", translated.Text, StringComparison.Ordinal);
        Assert.Contains("toLower(trim(src.Name)) STARTS WITH $p2", translated.Text, StringComparison.Ordinal);
        Assert.Contains("abs(src.Score - $p4) <= $p5", translated.Text, StringComparison.Ordinal);
        Assert.Contains("ORDER BY src.Category, src.Score DESC", translated.Text, StringComparison.Ordinal);
        Assert.Contains("SKIP 1\nLIMIT 3", translated.Text, StringComparison.Ordinal);
        Assert.Contains(
            "RETURN src.Name AS Name, src.Score + $p7 AS AdjustedScore, " +
            "CASE WHEN NOT (properties(src) ? 'OptionalText') THEN $p8 ELSE toUpper(src.OptionalText) END AS Display\n" +
            "ORDER BY src.Category, src.Score DESC\nSKIP 1\nLIMIT 3",
            translated.Text,
            StringComparison.Ordinal);
        Assert.Equal(["Name", "AdjustedScore", "Display"], translated.ProjectionColumns);
    }

    [Fact]
    public async Task BaseTypeCount_TranslatesToSummedUnionAllCountBranches()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");

        var source = store.Graph.Nodes<Animal>();
        var expression = CountCall(source, nameof(Queryable.Count));

        var translated = Translate(expression, typeof(Animal));

        Assert.Contains("MATCH (src:Animal)", translated.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:Dog)", translated.Text, StringComparison.Ordinal);
        Assert.Contains("MATCH (src:PoliceDog)", translated.Text, StringComparison.Ordinal);
        Assert.Contains("RETURN count(src)", translated.Text, StringComparison.Ordinal);
        Assert.Equal(2, translated.Text.Split("UNION ALL", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("inheritance_labels", translated.Text, StringComparison.Ordinal);
        Assert.True(translated.SumUnionCountBranches);
    }

    [Fact]
    public async Task MissingPropertyFilter_UsesNegatedKeyExistence()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");

        var query = store.Graph.Nodes<QueryExpressionCoverageNode>()
            .Where(node => node.OptionalText == null);

        var translated = Translate(query);

        Assert.Contains("WHERE NOT (properties(src) ? 'OptionalText')", translated.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("src.OptionalText IS NULL", translated.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresentPropertyFilter_KeepsProviderNeutralIsNotNull()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");

        var query = store.Graph.Nodes<QueryExpressionCoverageNode>()
            .Where(node => node.OptionalText != null);

        var translated = Translate(query);

        Assert.Contains("src.OptionalText IS NOT NULL", translated.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("properties(src) ? 'OptionalText'", translated.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingMappedPropertyFilter_UsesMappedStorageName()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");

        var query = store.Graph.Nodes<TranslationMappedPropertyNode>()
            .Where(node => node.Note == null);

        var translated = Translate(query);

        Assert.Contains("WHERE NOT (properties(src) ? 'notiz text')", translated.Text, StringComparison.Ordinal);
    }

    private static MethodCallExpression CountCall(IQueryable source, string methodName)
    {
        var method = typeof(Queryable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(candidate => candidate.Name == methodName && candidate.IsGenericMethodDefinition)
            .Single(candidate => candidate.GetParameters().Length == 1)
            .MakeGenericMethod(source.ElementType);
        return Expression.Call(method, source.Expression);
    }

    private static Querying.Cypher.CypherQuery Translate(Expression expression, Type rootType)
    {
        var visitor = new CypherQueryVisitor(rootType);
        visitor.Visit(expression);
        return visitor.Query;
    }

    private static Querying.Cypher.CypherQuery Translate(IQueryable query)
    {
        var visitor = new CypherQueryVisitor(query.ElementType);
        visitor.Visit(query.Expression);
        return visitor.Query;
    }
}
