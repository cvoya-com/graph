// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.CompatibilityTests;

public sealed class AgeSetOperationTranslationTests
{
    [Fact]
    public async Task RecursivelyLowersTypedUnionChain()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");
        var query = store.Graph.Nodes<Person>()
            .Union(store.Graph.Nodes<Person>())
            .Union(store.Graph.Nodes<Person>());

        var translated = Translate(query);

        Assert.Equal(2, translated.Text.Split('\n').Count(line =>
            string.Equals(line, "UNION", StringComparison.Ordinal)));
        Assert.Equal(9, translated.Text.Split('\n').Count(line =>
            line.StartsWith("RETURN { Node:", StringComparison.Ordinal)));
        Assert.Equal(6, translated.Text.Split('\n').Count(line =>
            string.Equals(line, "UNION ALL", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PreservesNestedConcatParameterNamespacesAndTemporalLowering()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");
        var firstCutoff = DateTime.UnixEpoch;
        var secondCutoff = firstCutoff.AddDays(1);
        var thirdCutoff = firstCutoff.AddDays(2);
        var query = store.Graph.Nodes<Person>()
            .Where(person => person.DateOfBirth >= firstCutoff.AddDays(1))
            .Select(person => person.FirstName)
            .Concat(store.Graph.Nodes<Person>()
                .Where(person => person.DateOfBirth >= secondCutoff.AddDays(1))
                .Select(person => person.FirstName)
                .Concat(store.Graph.Nodes<Person>()
                    .Where(person => person.DateOfBirth >= thirdCutoff.AddDays(1))
                    .Select(person => person.FirstName)));

        var translated = Translate(query);

        Assert.Equal(8, translated.Text.Split('\n').Count(line =>
            string.Equals(line, "UNION ALL", StringComparison.Ordinal)));
        Assert.Equal(3, translated.Parameters.Keys.Count(name =>
            name.StartsWith("age_temporal_", StringComparison.Ordinal)));
        Assert.Equal(translated.Parameters.Count, translated.Parameters.Keys.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("duration(", translated.Text, StringComparison.Ordinal);
        Assert.Equal(9, translated.Text.Split('\n').Count(line =>
            string.Equals(line, "RETURN src.FirstName AS age_column_0", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RecursivelyLowersLabelsAndCorrelatedCountsInEveryBranch()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");
        var left = store.Graph.Nodes<Person>()
            .OfLabel(nameof(Manager))
            .Select(person => new
            {
                person.FirstName,
                Relationships = person.CountRelationships<Knows>(GraphTraversalDirection.Both),
            });
        var right = store.Graph.Nodes<Person>()
            .Where(person => person.Age >= 21)
            .Select(person => new
            {
                person.FirstName,
                Relationships = person.CountRelationships<Knows>(GraphTraversalDirection.Both),
            });

        var translated = Translate(left.Union(right));

        Assert.DoesNotContain("COUNT {", translated.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("EXISTS {", translated.Text, StringComparison.Ordinal);
        Assert.Equal(6, translated.Text.Split('\n').Count(line =>
            line.StartsWith("OPTIONAL MATCH", StringComparison.Ordinal) &&
            line.Contains("__age_count0", StringComparison.Ordinal)));
        Assert.Contains("'Manager' IN labels(src)", translated.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecursivelyLowersOrderedComplexEntityBranches()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");
        var left = store.Graph.Nodes<PersonWithComplexProperty>()
            .Where(person => person.Age >= 21)
            .OrderBy(person => person.FirstName)
            .Take(1);
        var right = store.Graph.Nodes<PersonWithComplexProperty>()
            .Where(person => person.Age < 21)
            .OrderByDescending(person => person.FirstName)
            .Take(1);

        var translated = Translate(left.Concat(right));

        Assert.Equal(2, translated.Text.Split('\n').Count(line =>
            line.StartsWith("OPTIONAL MATCH (src)-[rels", StringComparison.Ordinal)));
        Assert.Equal(2, translated.Text.Split('\n').Count(line =>
            line.StartsWith("ORDER BY src.FirstName", StringComparison.Ordinal)));
        Assert.Equal(2, translated.Text.Split('\n').Count(line =>
            string.Equals(line, "LIMIT 1", StringComparison.Ordinal)));
        Assert.Equal(2, translated.Text.Split('\n').Count(line =>
            line.StartsWith("RETURN { Node:", StringComparison.Ordinal)));
    }

    private static Cvoya.Graph.Age.Querying.Cypher.CypherQuery Translate(IQueryable query)
    {
        var visitor = new CypherQueryVisitor(query.ElementType);
        visitor.Visit(query.Expression);
        return visitor.Query;
    }
}
