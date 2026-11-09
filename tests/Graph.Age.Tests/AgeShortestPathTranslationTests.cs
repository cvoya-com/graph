// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Visitors.Core;
using Cvoya.Graph.CompatibilityTests;

public sealed class AgeShortestPathTranslationTests
{
    private static readonly AgeDialect Age17 = AgeDialect.ForVersion(new Version(1, 7, 0));
    private static readonly AgeDialect Age18 = AgeDialect.ForVersion(new Version(1, 8, 0));

    [Fact]
    public void Age17DoesNotDeclareShortestPath()
    {
        Assert.False(Age17.Capabilities.Has(GraphCapability.ShortestPath));
        Assert.False(Age17.SupportsNativeShortestPath);
    }

    [Fact]
    public void Age18DeclaresShortestPath()
    {
        Assert.True(Age18.Capabilities.Has(GraphCapability.ShortestPath));
        Assert.True(Age18.SupportsNativeShortestPath);
        Assert.True(Age18.Planning.Capabilities.Has(GraphCapability.ShortestPath));
    }

    [Fact]
    public async Task Age17DeclinesShortestPathAtTranslationTime()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");
        var query = store.Graph.Nodes<Person>()
            .ShortestPath<Knows, Person>(person => person.FirstName == "target");

        var exception = Assert.Throws<GraphQueryTranslationException>(() =>
        {
            var visitor = new CypherQueryVisitor(query.ElementType, Age17, "translation");
            visitor.Visit(query.Expression);
        });

        Assert.Contains(nameof(GraphCapability.ShortestPath), exception.Message, StringComparison.Ordinal);
        Assert.Contains("Apache AGE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Age18PlansNativeShortestPathWithoutCypherPatternSyntax()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");
        var query = store.Graph.Nodes<Person>()
            .Where(person => person.TestKey == "start")
            .ShortestPath<Knows, Person>(person => person.TestKey == "target");

        var visitor = new CypherQueryVisitor(query.ElementType, Age18, "translation");
        visitor.Visit(query.Expression);

        Assert.True(visitor.Query.IsNativeGraphPath);
        Assert.Contains("ag_catalog.age_shortest_path(", visitor.Query.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("shortestPath(", visitor.Query.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("allShortestPaths(", visitor.Query.Text, StringComparison.Ordinal);
        Assert.Contains("src <> tgt", visitor.Query.Text, StringComparison.Ordinal);
        Assert.Contains("tgt.TestKey", visitor.Query.Text, StringComparison.Ordinal);
        Assert.Contains("'out'", visitor.Query.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Age18PlansAllShortestPathsWithNativeAllFunction()
    {
        await using var store = new AgeGraphStore(
            "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
            "translation");
        var query = store.Graph.Nodes<Person>()
            .Where(person => person.TestKey == "start")
            .AllShortestPaths<Knows, Person>(
                person => person.TestKey == "target",
                GraphTraversalDirection.Incoming);

        var visitor = new CypherQueryVisitor(query.ElementType, Age18, "translation");
        visitor.Visit(query.Expression);

        Assert.True(visitor.Query.IsNativeGraphPath);
        Assert.Contains("ag_catalog.age_all_shortest_paths(", visitor.Query.Text, StringComparison.Ordinal);
        Assert.Contains("'in'", visitor.Query.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.7.0", false)]
    [InlineData("1.8.0", true)]
    [InlineData("1.8.0-rc1", true)]
    [InlineData("1.10.2", true)]
    [InlineData("2.0", true)]
    [InlineData("not-a-version", false)]
    public void ParsesServerVersionAndSelectsDialect(string extversion, bool nativeShortestPath)
    {
        var version = AgeDialect.ParseServerVersion(extversion);

        Assert.Equal(nativeShortestPath, AgeDialect.ForVersion(version).SupportsNativeShortestPath);
    }
}
