// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

/// <summary>
/// Covers the type-safe <see cref="SchemaRegistry.Node{TNode}"/> property-index builder while
/// preserving the advanced string-based API.
/// </summary>
[Trait("Area", "SchemaRegistry")]
[Collection("SchemaRegistry")]
public sealed class TypeSafeIndexBuilderTests
{
    [Fact]
    public void NodeBuilderResolvesTheMappedLabelAndStoredPropertyNames()
    {
        var registry = new SchemaRegistry();

        var builder = registry.Node<TypeSafeIndexContract>();
        Assert.Same(builder, builder.AddPropertyIndex(contract => contract.Tier));
        registry.Node<TypeSafeIndexContract>()
            .AddPropertyIndex(contract => contract.Tier, PropertyIndexKind.Ordering)
            .AddPropertyIndex(contract => contract.Renamed);

        var catalog = registry.Indexes;
        Assert.True(catalog.TryGetIndex(
            "Nul106TypeSafeNode", "tariff", PropertyIndexKind.Equality, out var equality));
        Assert.Equal("tariff", equality.PropertyName);
        Assert.Equal(PropertyIndexKind.Equality, equality.Kind);

        Assert.True(catalog.TryGetIndex(
            "Nul106TypeSafeNode", "tariff", PropertyIndexKind.Ordering, out var ordering));
        Assert.Equal(PropertyIndexKind.Ordering, ordering.Kind);

        Assert.True(catalog.TryGetIndex(
            "Nul106TypeSafeNode", "renamed_property", PropertyIndexKind.Equality, out var renamed));
        Assert.Equal("renamed_property", renamed.PropertyName);
        Assert.Equal(3, catalog.PropertyIndexes.Count);
    }

    [Fact]
    public void SchemaRegistryExposesOnlyTheNodePropertyIndexBuilder()
    {
        // The AGE provider materializes and plans node-property indexes only. A relationship index
        // builder would therefore be a silent no-op for AGE, so the public surface keeps only
        // Node<TNode>() and the advanced string API.
        Assert.DoesNotContain(
            typeof(SchemaRegistry).GetMethods(),
            method => string.Equals(method.Name, "Relationship", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(SchemaRegistry).Assembly.GetExportedTypes(),
            type => string.Equals(type.Name, "RelationshipIndexBuilder`1", StringComparison.Ordinal));

        var nodeBuilder = typeof(SchemaRegistry).GetMethods()
            .Single(method => string.Equals(method.Name, "Node", StringComparison.Ordinal));
        Assert.Equal("NodeIndexBuilder`1", nodeBuilder.ReturnType.Name);
    }

    [Fact]
    public void DuplicateTypeSafeRegistrationStillThrows()
    {
        var registry = new SchemaRegistry();
        registry.Node<TypeSafeIndexContract>().AddPropertyIndex(contract => contract.Tier);

        var exception = Assert.Throws<GraphException>(
            () => registry.Node<TypeSafeIndexContract>().AddPropertyIndex(contract => contract.Tier));
        Assert.Contains("already registered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvancedStringApiStillWorksAlongsideTheTypeSafeApi()
    {
        var registry = new SchemaRegistry();
        registry.Node<TypeSafeIndexContract>().AddPropertyIndex(contract => contract.Tier);
        registry.AddPropertyIndex(new PropertyIndexDefinition("Nul106TypeSafeNode", "Tier"));

        var catalog = registry.Indexes;
        Assert.True(catalog.TryGetIndex(
            "Nul106TypeSafeNode", "tariff", PropertyIndexKind.Equality, out _));
        Assert.True(catalog.TryGetIndex(
            "Nul106TypeSafeNode", "Tier", PropertyIndexKind.Equality, out _));
        Assert.Equal(2, catalog.PropertyIndexes.Count);
    }

    [Fact]
    public void NodeBuilderRejectsNestedComputedFieldStaticIndexerAndMethodExpressions()
    {
        var registry = new SchemaRegistry();
        var builder = registry.Node<TypeSafeIndexContract>();

        var nested = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyIndex(contract => contract.Address!.City));
        Assert.Contains("nested member", nested.Message, StringComparison.Ordinal);
        Assert.Contains("Address.City", nested.Message, StringComparison.Ordinal);
        Assert.Contains("entity => entity.Property", nested.Message, StringComparison.Ordinal);

        var computed = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyIndex(contract => contract.Count + contract.Other));
        Assert.Contains("single member access", computed.Message, StringComparison.Ordinal);
        Assert.Contains("entity => entity.Property", computed.Message, StringComparison.Ordinal);

        var field = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyIndex(contract => contract.PublicField));
        Assert.Contains("field", field.Message, StringComparison.Ordinal);
        Assert.Contains("PublicField", field.Message, StringComparison.Ordinal);

        var staticProperty = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyIndex(contract => TypeSafeIndexContract.StaticLabel));
        Assert.Contains("static property", staticProperty.Message, StringComparison.Ordinal);
        Assert.Contains("StaticLabel", staticProperty.Message, StringComparison.Ordinal);

        var indexer = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyIndex(contract => contract[0]));
        Assert.Contains("indexer", indexer.Message, StringComparison.Ordinal);

        var method = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyIndex(contract => contract.GetLabel()));
        Assert.Contains("GetLabel", method.Message, StringComparison.Ordinal);
        Assert.Contains("entity => entity.Property", method.Message, StringComparison.Ordinal);

        Assert.True(registry.Indexes.IsEmpty);
    }

    [Fact]
    public void NodeBuilderRejectsAnIgnoredProperty()
    {
        var registry = new SchemaRegistry();

        var exception = Assert.Throws<GraphException>(
            () => registry.Node<TypeSafeIndexContract>().AddPropertyIndex(contract => contract.Ignored));
        Assert.Contains("PropertyAttribute.Ignore", exception.Message, StringComparison.Ordinal);
        Assert.True(registry.Indexes.IsEmpty);
    }

    [Node("Nul106TypeSafeNode")]
    public sealed record TypeSafeIndexContract : Node
    {
        [Property(Label = "tariff")]
        public string? Tier { get; set; }

        [Property(Label = "renamed_property")]
        public string? Renamed { get; set; }

        public int Count { get; set; }

        public int Other { get; set; }

        public TypeSafeIndexAddress? Address { get; set; }

        [Property(Ignore = true)]
        public string Ignored => Tier ?? string.Empty;

        internal string PublicField = string.Empty;

        public static string StaticLabel { get; set; } = string.Empty;

        public string this[int index] => index == 0 ? Tier ?? string.Empty : string.Empty;

        public string GetLabel() => Tier ?? string.Empty;
    }

    public sealed class TypeSafeIndexAddress
    {
        public string City { get; set; } = string.Empty;
    }
}
