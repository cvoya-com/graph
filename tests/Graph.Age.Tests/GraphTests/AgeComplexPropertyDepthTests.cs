// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests.GraphTests;

/// <summary>
/// Executes the hydration shapes whose traversal was previously bounded by the global depth:
/// depth-1, nested, collection, and an invalid cyclic value.
/// </summary>
public sealed class AgeComplexPropertyDepthTests(AgeHarness harness) : AgeTest(harness)
{
    [Fact]
    public async Task DepthOneComplexPropertyRoundTripsThroughAQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = new DepthOneOwner
        {
            Name = "depth-one",
            Address = new DepthAddress { City = "Seattle", Street = "1st" },
        };
        await Graph.CreateNodeAsync(owner, cancellationToken: cancellationToken);

        var fetched = await Graph.Nodes<DepthOneOwner>()
            .SingleAsync(cancellationToken);

        Assert.Equal("Seattle", fetched.Address?.City);
        Assert.Equal("1st", fetched.Address?.Street);
    }

    [Fact]
    public async Task NestedComplexPropertyRoundTripsThroughAQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = new NestedDepthOwner
        {
            Name = "nested",
            Office = new DepthOffice
            {
                Label = "HQ",
                Address = new DepthAddress { City = "Denver", Street = "16th" },
            },
        };
        await Graph.CreateNodeAsync(owner, cancellationToken: cancellationToken);

        var fetched = await Graph.Nodes<NestedDepthOwner>()
            .SingleAsync(cancellationToken);

        Assert.Equal("HQ", fetched.Office?.Label);
        Assert.Equal("Denver", fetched.Office?.Address?.City);
        Assert.Equal("16th", fetched.Office?.Address?.Street);
    }

    [Fact]
    public async Task ComplexPropertyCollectionRoundTripsThroughAQuery()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = new CollectionDepthOwner
        {
            Name = "collection",
            Addresses =
            [
                new DepthAddress { City = "Austin", Street = "A" },
                new DepthAddress { City = "Boston", Street = "B" },
            ],
        };
        await Graph.CreateNodeAsync(owner, cancellationToken: cancellationToken);

        var fetched = await Graph.Nodes<CollectionDepthOwner>()
            .SingleAsync(cancellationToken);

        Assert.Equal(2, fetched.Addresses.Count);
        Assert.Equal("Austin", fetched.Addresses[0].City);
        Assert.Equal("Boston", fetched.Addresses[1].City);
    }

    [Fact]
    public async Task CyclicComplexPropertyIsRejectedBeforeStorage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var value = new DepthCycleValue();
        value.Next = value;
        var owner = new CyclicDepthOwner { Name = "cycle", Value = value };

        await Assert.ThrowsAsync<GraphException>(
            () => Graph.CreateNodeAsync(owner, cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task NonSealedComplexPropertyRoundTripsThroughADerivedRuntimeType()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var owner = new NonSealedOwner
        {
            Name = "non-sealed",
            Address = new NonSealedPostalAddress
            {
                City = "Berlin",
                Street = "Unter den Linden",
                Country = new NonSealedCountry { Code = "DE" },
            },
        };
        await Graph.CreateNodeAsync(owner, cancellationToken: cancellationToken);

        var fetched = await Graph.Nodes<NonSealedOwner>()
            .SingleAsync(cancellationToken);

        var address = Assert.IsType<NonSealedPostalAddress>(fetched.Address);
        Assert.Equal("Berlin", address.City);
        Assert.Equal("Unter den Linden", address.Street);
        Assert.Equal("DE", address.Country?.Code);
    }

    [Node("Nul97DepthOneOwner")]
    public sealed record DepthOneOwner : Node
    {
        public string? Name { get; set; }

        public DepthAddress? Address { get; set; }
    }

    [Node("Nul98NonSealedOwner")]
    public sealed record NonSealedOwner : Node
    {
        public string? Name { get; set; }

        public NonSealedAddress? Address { get; set; }
    }

    public class NonSealedAddress
    {
        public string City { get; set; } = string.Empty;

        public string Street { get; set; } = string.Empty;
    }

    public sealed class NonSealedPostalAddress : NonSealedAddress
    {
        public NonSealedCountry? Country { get; set; }
    }

    public sealed class NonSealedCountry
    {
        public string Code { get; set; } = string.Empty;
    }

    [Node("Nul97NestedDepthOwner")]
    public sealed record NestedDepthOwner : Node
    {
        public string? Name { get; set; }

        public DepthOffice? Office { get; set; }
    }

    [Node("Nul97CollectionDepthOwner")]
    public sealed record CollectionDepthOwner : Node
    {
        public string? Name { get; set; }

        public List<DepthAddress> Addresses { get; set; } = [];
    }

    [Node("Nul97CyclicDepthOwner")]
    public sealed record CyclicDepthOwner : Node
    {
        public string? Name { get; set; }

        public DepthCycleValue? Value { get; set; }
    }

    public sealed class DepthAddress
    {
        public string City { get; set; } = string.Empty;

        public string Street { get; set; } = string.Empty;
    }

    public sealed class DepthOffice
    {
        public string Label { get; set; } = string.Empty;

        public DepthAddress? Address { get; set; }
    }

    public sealed class DepthCycleValue
    {
        public DepthCycleValue? Next { get; set; }
    }
}
