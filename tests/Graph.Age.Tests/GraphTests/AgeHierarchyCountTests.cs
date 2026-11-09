// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

using Cvoya.Graph.CompatibilityTests;

/// <summary>
/// AGE-specific end-to-end coverage for server-side counting across a node-type hierarchy.
/// </summary>
public sealed class AgeHierarchyCountTests(AgeHarness harness)
    : AgeTest(harness, StoreIsolation.FreshStore)
{
    [Fact]
    public async Task SynchronousCountAndLongCountCoverTheWholeHierarchy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await Graph.CreateNodeAsync(new Animal { Name = "base" }, null, cancellationToken);
        await Graph.CreateNodeAsync(new Dog { Name = "dog" }, null, cancellationToken);
        await Graph.CreateNodeAsync(new PoliceDog { Name = "police" }, null, cancellationToken);

        var query = Graph.Nodes<Animal>();

        // Queryable.Count/LongCount must translate to a server-side count, not materialize the
        // hierarchy and count client-side.
        Assert.Equal(3, query.Count());
        Assert.Equal(3L, query.LongCount());
        Assert.Equal(2, query.Count(animal => animal.Name != "base"));
    }
}
