// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

[Trait("Area", "SchemaRegistry")]
[Collection("SchemaRegistry")]
public sealed class GraphIndexCatalogTests
{
    [Fact]
    public void EmptyCatalogHasNoDeclarations()
    {
        var catalog = GraphIndexCatalog.Empty;

        Assert.True(catalog.IsEmpty);
        Assert.Empty(catalog.PropertyIndexes);
        Assert.Empty(catalog.PropertyAggregates);
        Assert.Equal(0, catalog.Version);
    }

    [Fact]
    public void DefinitionsAreResolvedAndVersioned()
    {
        var registry = new SchemaRegistry();
        var firstVersion = registry.Indexes.Version;

        registry.AddPropertyIndex(new PropertyIndexDefinition("Contract", "Status"));
        registry.AddPropertyIndex(new PropertyIndexDefinition("Contract", "Name", PropertyIndexKind.Ordering));
        registry.AddPropertyAggregate(new PropertyAggregateDefinition(
            "Contract",
            "Status",
            "Kaufpreis",
            AggregateFunction.Count | AggregateFunction.Sum));

        var catalog = registry.Indexes;
        Assert.False(catalog.IsEmpty);
        Assert.True(catalog.Version > firstVersion);
        Assert.True(catalog.TryGetIndex("Contract", "Status", PropertyIndexKind.Equality, out var equality));
        Assert.Equal("Status", equality.PropertyName);
        Assert.True(catalog.TryGetIndex("Contract", "Name", PropertyIndexKind.Ordering, out var ordering));
        Assert.Equal(PropertyIndexKind.Ordering, ordering.Kind);
        Assert.False(catalog.TryGetIndex("Contract", "Name", PropertyIndexKind.Equality, out _));
        Assert.True(catalog.TryGetAggregate("Contract", "Status", "Kaufpreis", out var aggregate));
        Assert.Equal(AggregateFunction.Count | AggregateFunction.Sum, aggregate.Functions);
        Assert.Equal(2, catalog.PropertyIndexes.Count);
        Assert.Single(catalog.PropertyAggregates);
    }

    [Fact]
    public void DuplicateIndexIsRejected()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition("Contract", "Status"));

        Assert.Throws<GraphException>(() =>
            registry.AddPropertyIndex(new PropertyIndexDefinition("Contract", "Status")));
    }

    [Fact]
    public void AggregateFunctionsAreAdditive()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyAggregate(new PropertyAggregateDefinition(
            "Contract",
            "Status",
            "Kaufpreis",
            AggregateFunction.Count));
        registry.AddPropertyAggregate(new PropertyAggregateDefinition(
            "Contract",
            "Status",
            "Kaufpreis",
            AggregateFunction.Sum));

        Assert.True(registry.Indexes.TryGetAggregate(
            "Contract", "Status", "Kaufpreis", out var aggregate));
        Assert.Equal(AggregateFunction.Count | AggregateFunction.Sum, aggregate.Functions);
    }

    [Fact]
    public async Task ClearResetsDeclarationsAndBumpsVersion()
    {
        var registry = new SchemaRegistry();
        registry.AddPropertyIndex(new PropertyIndexDefinition("Contract", "Status"));
        var version = registry.Indexes.Version;

        await registry.ClearAsync(TestContext.Current.CancellationToken);

        Assert.True(registry.Indexes.IsEmpty);
        Assert.True(registry.Indexes.Version > version);
    }

    [Fact]
    public void IndexDefinitionValidatesItsInputs()
    {
        Assert.Throws<ArgumentException>(() => new PropertyIndexDefinition("", "Status"));
        Assert.Throws<ArgumentException>(() => new PropertyIndexDefinition("Contract", ""));
        Assert.Throws<ArgumentException>(() => new PropertyIndexDefinition("Contract", "Status", (PropertyIndexKind)99));
    }

    [Fact]
    public void AggregateDefinitionValidatesItsInputs()
    {
        Assert.Throws<ArgumentException>(() => new PropertyAggregateDefinition("Contract", "Status", "Status", AggregateFunction.Count));
        Assert.Throws<ArgumentException>(() => new PropertyAggregateDefinition("Contract", "Status", "Kaufpreis", AggregateFunction.None));
        Assert.Throws<ArgumentException>(() => new PropertyAggregateDefinition("Contract", "Status", "Kaufpreis", (AggregateFunction)1024));
    }
}
