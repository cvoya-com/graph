// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.CompatibilityTests;

public sealed class AgeScalarGroupByTranslationTests
{
    [Fact]
    public async Task RendersKeyAndAggregatesThroughAgeSupportedWithStage()
    {
        await using var store = CreateStore();
        var query = store.Graph.Nodes<Person>()
            .Where(person => person.Age >= 18)
            .GroupBy(person => person.LastName)
            .Select(group => new
            {
                LastName = group.Key,
                Count = group.Count(),
                TotalAge = group.Sum(person => person.Age),
                AverageAge = group.Average(person => person.Age),
                Youngest = group.Min(person => person.Age),
                Oldest = group.Max(person => person.Age),
            });

        var translated = Translate(query);

        // `Person` has physical subtypes, so the grouped aggregates expand into one branch per
        // label table. `avg` is decomposed into a partial sum and a partial non-null count; the
        // wrapper merges the counts and divides.
        Assert.Contains("UNION ALL", translated.Text, StringComparison.Ordinal);
        Assert.Contains("WHERE", translated.Text);
        Assert.Contains("src.Age >= $p0\nWITH src.LastName AS __key", translated.Text);
        Assert.Contains("count(1) AS __a0", translated.Text);
        Assert.Contains("coalesce(sum(src.Age), 0) AS __a1", translated.Text);
        Assert.Contains("sum(src.Age) AS __a2", translated.Text, StringComparison.Ordinal);
        Assert.Contains(
            "count(src.Age) AS __age_union_avg_count0",
            translated.Text,
            StringComparison.Ordinal);
        Assert.Contains("min(src.Age) AS __a3", translated.Text);
        Assert.Contains("max(src.Age) AS __a4", translated.Text);
        Assert.Contains(
            "RETURN __key AS LastName, __a0 AS `Count`, __a1 AS TotalAge, __a2 AS AverageAge, " +
            "__a3 AS Youngest, __a4 AS Oldest, " +
            "__age_union_avg_count0 AS __age_union_avg_count0",
            translated.Text);
        Assert.Equal(
            [
                "LastName",
                "Count",
                "TotalAge",
                "AverageAge",
                "Youngest",
                "Oldest",
                "__age_union_avg_count0",
            ],
            translated.ProjectionColumns);
    }

    [Fact]
    public async Task RendersComputedKeyAndResultSelectorWithoutTextRewriting()
    {
        await using var store = CreateStore();
        var query = store.Graph.Nodes<Person>()
            .GroupBy(
                person => person.Age >= 40 ? "Senior" : "Junior",
                (band, people) => new { Band = band, Count = people.Count() });

        var translated = Translate(query);

        Assert.Contains("WITH CASE WHEN src.Age >= $p0 THEN $p1 ELSE $p2 END AS __key", translated.Text);
        Assert.Contains("count(1) AS __a0", translated.Text);
        Assert.EndsWith("RETURN __key AS Band, __a0 AS `Count`", translated.Text, StringComparison.Ordinal);
        Assert.Equal(["Band", "Count"], translated.ProjectionColumns);
    }

    [Fact]
    public async Task RendersLongCountThroughTheCountAggregate()
    {
        await using var store = CreateStore();
        var query = store.Graph.Nodes<Person>()
            .GroupBy(person => person.LastName)
            .Select(group => new { LastName = group.Key, Total = group.LongCount() });

        var translated = Translate(query);

        Assert.Contains("WITH src.LastName AS __key", translated.Text);
        Assert.Contains("count(1) AS __a0", translated.Text);
        Assert.EndsWith("RETURN __key AS LastName, __a0 AS Total", translated.Text, StringComparison.Ordinal);
        Assert.Equal(["LastName", "Total"], translated.ProjectionColumns);
    }

    [Fact]
    public async Task RendersKeyOnlyProjectionAsDistinctGroupingKey()
    {
        await using var store = CreateStore();
        var query = store.Graph.Nodes<Person>()
            .GroupBy(person => person.LastName)
            .Select(group => group.Key);

        var translated = Translate(query);

        Assert.Contains("WITH DISTINCT src.LastName AS __key", translated.Text);
        Assert.EndsWith("RETURN __key", translated.Text, StringComparison.Ordinal);
        Assert.Equal(["__key"], translated.ProjectionColumns);
    }

    [Fact]
    public async Task PreservesSharedUnsupportedShapeMessage()
    {
        await using var store = CreateStore();
        var query = store.Graph.Nodes<Person>()
            .GroupBy(person => person)
            .Select(group => new { Count = group.Count() });

        var exception = Assert.Throws<NotSupportedException>(() => Translate(query));

        Assert.Equal(
            "GroupBy is not supported for this query shape: the grouping key must be a scalar value; " +
            "grouping by an entity, node, or composite key is not supported.",
            exception.Message);
    }

    private static AgeGraphStore CreateStore() => new(
        "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
        "translation");

    private static Querying.Cypher.CypherQuery Translate(IQueryable query)
    {
        var visitor = new CypherQueryVisitor(query.ElementType);
        visitor.Visit(query.Expression);
        return visitor.Query;
    }
}
