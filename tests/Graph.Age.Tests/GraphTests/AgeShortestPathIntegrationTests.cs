// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph.CompatibilityTests;

/// <summary>
/// Provider integration coverage for Apache AGE 1.8 native shortest-path planning and path
/// hydration. The inherited TCK contract runs the public shortest-path surface; these tests pin the
/// AGE-specific shape: one row per native path value, direction mapping, endpoint predicates,
/// cycles, ties, cartesian source/target pairs, and segment hydration.
/// </summary>
public sealed class AgeShortestPathIntegrationTests(AgeHarness harness)
    : AgeTest(harness, StoreIsolation.FreshStore)
{
    [Fact]
    [RequiresCapability(GraphCapability.ShortestPath)]
    public async Task ShortestPath_ReturnsOneMinimumPathAndHydratesEverySegment()
    {
        var ct = TestContext.Current.CancellationToken;
        var (start, left, right, end) = await SeedDiamondAsync(ct);

        var paths = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .ShortestPath<Knows, Person>(person => person.TestKey == end.TestKey)
            .ToListAsync(ct);

        var path = Assert.Single(paths);
        Assert.Equal(start.TestKey, AsPerson(path.Start).TestKey);
        Assert.Equal(end.TestKey, AsPerson(path.End).TestKey);
        Assert.Equal(2, path.Segments.Count);
        Assert.All(path.Segments, segment =>
        {
            Assert.IsType<Person>(segment.StartNode);
            Assert.IsType<Knows>(segment.Relationship);
            Assert.IsType<Person>(segment.EndNode);
            Assert.Equal(RelationshipDirection.Outgoing, segment.Direction);
            Assert.NotEqual(AsPerson(segment.StartNode).TestKey, AsPerson(segment.EndNode).TestKey);
        });
    }

    [Fact]
    [RequiresCapability(GraphCapability.ShortestPath)]
    public async Task AllShortestPaths_ReturnsEveryTiedMinimumPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var (start, _, _, end) = await SeedDiamondAsync(ct);

        var one = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .ShortestPath<Knows, Person>(person => person.TestKey == end.TestKey)
            .ToListAsync(ct);
        var all = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .AllShortestPaths<Knows, Person>(person => person.TestKey == end.TestKey)
            .ToListAsync(ct);

        var filtered = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .AllShortestPaths<Knows, Person>(person => person.TestKey == end.TestKey)
            .Where(path => path.Segments.Count == 2)
            .ToListAsync(ct);

        Assert.Single(one);
        Assert.Equal(2, all.Count);
        Assert.Equal(2, filtered.Count);
        Assert.All(all, path => Assert.Equal(2, path.Segments.Count));
        Assert.Equal(
            all.Select(path => AsPerson(path.Segments[0].EndNode).TestKey).Order().ToArray(),
            all.Select(path => AsPerson(path.Segments[0].EndNode).TestKey).Distinct().Order().ToArray());
    }

    [Fact]
    [RequiresCapability(GraphCapability.ShortestPath)]
    public async Task ShortestPath_MapsIncomingAndAnyDirections()
    {
        var ct = TestContext.Current.CancellationToken;
        var (start, left, right, end) = await SeedDiamondAsync(ct);

        var incoming = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == end.TestKey)
            .ShortestPath<Knows, Person>(
                person => person.TestKey == start.TestKey,
                GraphTraversalDirection.Incoming)
            .ToListAsync(ct);
        var any = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == end.TestKey)
            .ShortestPath<Knows, Person>(
                person => person.TestKey == start.TestKey,
                GraphTraversalDirection.Both)
            .ToListAsync(ct);

        var incomingPath = Assert.Single(incoming);
        Assert.Equal(2, incomingPath.Segments.Count);
        Assert.All(incomingPath.Segments, segment =>
            Assert.Equal(RelationshipDirection.Incoming, segment.Direction));
        Assert.Equal(end.TestKey, AsPerson(incomingPath.Start).TestKey);
        Assert.Equal(start.TestKey, AsPerson(incomingPath.End).TestKey);

        var anyPath = Assert.Single(any);
        Assert.Equal(2, anyPath.Segments.Count);
    }

    [Fact]
    [RequiresCapability(GraphCapability.ShortestPath)]
    public async Task ShortestPath_AppliesEndpointPredicateBeforeSearch()
    {
        var ct = TestContext.Current.CancellationToken;
        var (start, left, _, end) = await SeedDiamondAsync(ct);

        var paths = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .ShortestPath<Knows, Person>(person => person.TestKey == left.TestKey)
            .ToListAsync(ct);

        var path = Assert.Single(paths);
        Assert.Single(path.Segments);
        Assert.Equal(left.TestKey, AsPerson(path.End).TestKey);
        Assert.NotEqual(end.TestKey, AsPerson(path.End).TestKey);
    }

    [Fact]
    [RequiresCapability(GraphCapability.ShortestPath)]
    public async Task ShortestPath_ExcludesSourceEndpointAndUnreachableTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var (start, _, _, _) = await SeedDiamondAsync(ct);
        var missing = new Person { FirstName = "missing" };
        await Graph.CreateNodeAsync(missing, null, ct);

        var sameNode = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .ShortestPath<Knows, Person>(person => person.TestKey == start.TestKey)
            .ToListAsync(ct);
        var noPath = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .ShortestPath<Knows, Person>(person => person.TestKey == missing.TestKey)
            .ToListAsync(ct);

        Assert.Empty(sameNode);
        Assert.Empty(noPath);
    }

    [Fact]
    [RequiresCapability(GraphCapability.ShortestPath)]
    public async Task ShortestPath_RespectsCyclesAndFindsEverySourceTargetPair()
    {
        var ct = TestContext.Current.CancellationToken;
        var (start, left, right, end) = await SeedDiamondAsync(ct);
        await Graph.ConnectAsync(end, new Knows(), start, cancellationToken: ct);

        var paths = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey || person.TestKey == left.TestKey)
            .ShortestPath<Knows, Person>(
                person => person.TestKey == end.TestKey || person.TestKey == right.TestKey)
            .ToListAsync(ct);

        Assert.Equal(4, paths.Count);
        Assert.Contains(paths, path => AsPerson(path.Start).TestKey == start.TestKey && AsPerson(path.End).TestKey == end.TestKey);
        Assert.Contains(paths, path => AsPerson(path.Start).TestKey == start.TestKey && AsPerson(path.End).TestKey == right.TestKey);
        Assert.Contains(paths, path => AsPerson(path.Start).TestKey == left.TestKey && AsPerson(path.End).TestKey == end.TestKey);
        Assert.Contains(paths, path => AsPerson(path.Start).TestKey == left.TestKey && AsPerson(path.End).TestKey == right.TestKey);
    }

    [Fact]
    [RequiresCapability(GraphCapability.ShortestPath)]
    public async Task ShortestPath_HonorsPagingAndCountTerminals()
    {
        var ct = TestContext.Current.CancellationToken;
        var (start, _, _, end) = await SeedDiamondAsync(ct);

        var page = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .AllShortestPaths<Knows, Person>(person => person.TestKey == end.TestKey)
            .Take(1)
            .ToListAsync(ct);
        var count = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .AllShortestPaths<Knows, Person>(person => person.TestKey == end.TestKey)
            .CountAsync(ct);
        var any = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .ShortestPath<Knows, Person>(person => person.TestKey == end.TestKey)
            .AnyAsync(ct);

        Assert.Single(page);
        Assert.Equal(2, count);
        Assert.True(any);
    }

    [Fact]
    [RequiresCapability(GraphCapability.ShortestPath)]
    public async Task ShortestPath_ProjectsPathEndpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var (start, _, _, end) = await SeedDiamondAsync(ct);

        var endpoints = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .ShortestPath<Knows, Person>(person => person.TestKey == end.TestKey)
            .Select(path => path.End)
            .ToListAsync(ct);

        var endpoint = Assert.Single(endpoints);
        Assert.Equal(end.TestKey, AsPerson(endpoint).TestKey);
    }

    [Fact]
    [RequiresCapability(GraphCapability.ShortestPath)]
    public async Task ShortestPath_LeavesBackendHealthyAfterNativeExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var (start, _, _, end) = await SeedDiamondAsync(ct);

        _ = await Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .ShortestPath<Knows, Person>(person => person.TestKey == end.TestKey)
            .ToListAsync(ct);

        Assert.Equal(
            4,
            await Graph.Nodes<Person>().CountAsync(ct));
    }

    private static Person AsPerson(INode node) => Assert.IsType<Person>(node);

    private async Task<(Person Start, Person Left, Person Right, Person End)> SeedDiamondAsync(
        CancellationToken cancellationToken)
    {
        var start = new Person { FirstName = "start" };
        var left = new Person { FirstName = "left" };
        var right = new Person { FirstName = "right" };
        var end = new Person { FirstName = "end" };
        foreach (var person in new[] { start, left, right, end })
        {
            await Graph.CreateNodeAsync(person, null, cancellationToken);
        }

        foreach (var (from, to) in new[]
        {
            (start, left),
            (left, end),
            (start, right),
            (right, end),
        })
        {
            await Graph.ConnectAsync(from, new Knows(), to, cancellationToken: cancellationToken);
        }

        return (start, left, right, end);
    }
}
