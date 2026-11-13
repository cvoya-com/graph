// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Core.Tests;

/// <summary>
/// Covers the type-safe <see cref="NodeIndexBuilder{TNode}.AddPropertyAggregate{TGroupKey, TValue}"/>
/// declaration while preserving the advanced string-based API.
/// </summary>
[Trait("Area", "SchemaRegistry")]
[Collection("SchemaRegistry")]
public sealed class TypeSafeAggregateBuilderTests
{
    private const string Label = "Nul110TypeSafeAggregate";

    [Fact]
    public void NodeBuilderChainsIndexAndAggregateDeclarations()
    {
        var registry = new SchemaRegistry();
        var builder = registry.Node<TypeSafeAggregateContract>();
        Assert.Same(builder, builder.AddPropertyIndex(contract => contract.Tier));
        Assert.Same(
            builder,
            builder.AddPropertyIndex(contract => contract.Text, PropertyIndexKind.Ordering));

        var chained = builder.AddPropertyAggregate(
            contract => contract.Tier,
            contract => contract.Amount,
            AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
            AggregateFunction.Max | AggregateFunction.Average);
        Assert.Same(builder, chained);

        var catalog = registry.Indexes;
        Assert.True(catalog.TryGetIndex(Label, "tariff", PropertyIndexKind.Equality, out _));
        Assert.True(catalog.TryGetIndex(Label, "Text", PropertyIndexKind.Ordering, out _));
        Assert.True(catalog.TryGetAggregate(Label, "tariff", "amount", out var aggregate));
        Assert.Equal(
            AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
            AggregateFunction.Max | AggregateFunction.Average,
            aggregate.Functions);
    }

    [Fact]
    public void NodeBuilderResolvesBothPropertyAttributeLabels()
    {
        var registry = new SchemaRegistry();

        registry.Node<TypeSafeAggregateContract>().AddPropertyAggregate(
            contract => contract.Tier,
            contract => contract.MappedAmount,
            AggregateFunction.Sum);

        Assert.True(registry.Indexes.TryGetAggregate(
            Label, "tariff", "mapped_amount", out var aggregate));
        Assert.Equal("tariff", aggregate.GroupKeyProperty);
        Assert.Equal("mapped_amount", aggregate.ValueProperty);
    }

    [Fact]
    public void AggregateRejectsEveryInvalidGroupKeySelectorShape()
    {
        var registry = new SchemaRegistry();
        var builder = registry.Node<TypeSafeAggregateContract>();

        var nested = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Address!.City,
                contract => contract.Amount,
                AggregateFunction.Count));
        Assert.Contains("nested member", nested.Message, StringComparison.Ordinal);
        Assert.Equal("groupKey", nested.ParamName);

        var computed = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Count + contract.Other,
                contract => contract.Amount,
                AggregateFunction.Count));
        Assert.Contains("single member access", computed.Message, StringComparison.Ordinal);
        Assert.Equal("groupKey", computed.ParamName);

        var field = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.PublicField,
                contract => contract.Amount,
                AggregateFunction.Count));
        Assert.Contains("field", field.Message, StringComparison.Ordinal);
        Assert.Equal("groupKey", field.ParamName);

        var staticProperty = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => TypeSafeAggregateContract.StaticLabel,
                contract => contract.Amount,
                AggregateFunction.Count));
        Assert.Contains("static property", staticProperty.Message, StringComparison.Ordinal);
        Assert.Equal("groupKey", staticProperty.ParamName);

        var indexer = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract[0],
                contract => contract.Amount,
                AggregateFunction.Count));
        Assert.Contains("indexer", indexer.Message, StringComparison.Ordinal);
        Assert.Equal("groupKey", indexer.ParamName);

        var method = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.GetLabel(),
                contract => contract.Amount,
                AggregateFunction.Count));
        Assert.Contains("not a single member access", method.Message, StringComparison.Ordinal);
        Assert.Equal("groupKey", method.ParamName);

        var ignored = Assert.Throws<GraphException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Ignored,
                contract => contract.Amount,
                AggregateFunction.Count));
        Assert.Contains("PropertyAttribute.Ignore", ignored.Message, StringComparison.Ordinal);

        var notStored = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Hidden,
                contract => contract.Amount,
                AggregateFunction.Count));
        Assert.Contains("not a stored property", notStored.Message, StringComparison.Ordinal);
        Assert.Equal("groupKey", notStored.ParamName);

        Assert.True(registry.Indexes.IsEmpty);
    }

    [Fact]
    public void AggregateRejectsEveryInvalidValueSelectorShape()
    {
        var registry = new SchemaRegistry();
        var builder = registry.Node<TypeSafeAggregateContract>();

        var nested = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Tier,
                contract => contract.Address!.City,
                AggregateFunction.Count));
        Assert.Contains("nested member", nested.Message, StringComparison.Ordinal);
        Assert.Equal("value", nested.ParamName);

        var computed = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Tier,
                contract => contract.Count + contract.Other,
                AggregateFunction.Count));
        Assert.Contains("single member access", computed.Message, StringComparison.Ordinal);
        Assert.Equal("value", computed.ParamName);

        var field = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Tier,
                contract => contract.PublicField,
                AggregateFunction.Count));
        Assert.Contains("field", field.Message, StringComparison.Ordinal);
        Assert.Equal("value", field.ParamName);

        var staticProperty = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Tier,
                contract => TypeSafeAggregateContract.StaticLabel,
                AggregateFunction.Count));
        Assert.Contains("static property", staticProperty.Message, StringComparison.Ordinal);
        Assert.Equal("value", staticProperty.ParamName);

        var indexer = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Tier,
                contract => contract[0],
                AggregateFunction.Count));
        Assert.Contains("indexer", indexer.Message, StringComparison.Ordinal);
        Assert.Equal("value", indexer.ParamName);

        var method = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Tier,
                contract => contract.GetLabel(),
                AggregateFunction.Count));
        Assert.Contains("not a single member access", method.Message, StringComparison.Ordinal);
        Assert.Equal("value", method.ParamName);

        var ignored = Assert.Throws<GraphException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Tier,
                contract => contract.Ignored,
                AggregateFunction.Count));
        Assert.Contains("PropertyAttribute.Ignore", ignored.Message, StringComparison.Ordinal);

        var notStored = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Tier,
                contract => contract.Hidden,
                AggregateFunction.Count));
        Assert.Contains("not a stored property", notStored.Message, StringComparison.Ordinal);
        Assert.Equal("value", notStored.ParamName);

        Assert.True(registry.Indexes.IsEmpty);
    }

    [Fact]
    public void AggregateRejectsEqualResolvedGroupAndValueNames()
    {
        var registry = new SchemaRegistry();

        var exception = Assert.Throws<ArgumentException>(
            () => registry.Node<TypeSafeAggregateContract>().AddPropertyAggregate(
                contract => contract.Tier,
                contract => contract.Tier,
                AggregateFunction.Count));

        Assert.Contains("both resolve to the stored property", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'tariff'", exception.Message, StringComparison.Ordinal);
        Assert.Equal("value", exception.ParamName);
        Assert.True(registry.Indexes.IsEmpty);
    }

    [Fact]
    public void AggregateAcceptsEverySupportedNumericClrTypeAndItsNullableForm()
    {
        var registry = new SchemaRegistry();
        var builder = registry.Node<TypeSafeAggregateContract>();

        builder
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.SbyteValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.ByteValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.ShortValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.UshortValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.IntValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.UintValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.LongValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.UlongValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.FloatValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.DoubleValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.DecimalValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.NullableSbyteValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.NullableByteValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.NullableShortValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.NullableUshortValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.NullableIntValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.NullableUintValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.NullableLongValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.NullableUlongValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.NullableFloatValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.NullableDoubleValue, AggregateFunction.Count)
            .AddPropertyAggregate(contract => contract.Tier, contract => contract.NullableDecimalValue, AggregateFunction.Count);

        Assert.Equal(22, registry.Indexes.PropertyAggregates.Count);
        foreach (var valueProperty in new[]
                 {
                     "SbyteValue", "ByteValue", "ShortValue", "UshortValue", "IntValue", "UintValue",
                     "LongValue", "UlongValue", "FloatValue", "DoubleValue", "DecimalValue",
                     "NullableSbyteValue", "NullableByteValue", "NullableShortValue", "NullableUshortValue",
                     "NullableIntValue", "NullableUintValue", "NullableLongValue", "NullableUlongValue",
                     "NullableFloatValue", "NullableDoubleValue", "NullableDecimalValue",
                 })
        {
            Assert.True(
                registry.Indexes.TryGetAggregate(Label, "tariff", valueProperty, out var aggregate),
                $"Missing numeric aggregate for '{valueProperty}'.");
            Assert.Equal(valueProperty, aggregate.ValueProperty);
        }
    }

    [Fact]
    public void AggregateRejectsNonNumericValueClrTypes()
    {
        var registry = new SchemaRegistry();
        var builder = registry.Node<TypeSafeAggregateContract>();

        foreach (var action in new Action[]
                 {
                     () => builder.AddPropertyAggregate(
                         contract => contract.Tier, contract => contract.Text, AggregateFunction.Count),
                     () => builder.AddPropertyAggregate(
                         contract => contract.Tier, contract => contract.Active, AggregateFunction.Count),
                     () => builder.AddPropertyAggregate(
                         contract => contract.Tier, contract => contract.Reference, AggregateFunction.Count),
                     () => builder.AddPropertyAggregate(
                         contract => contract.Tier, contract => contract.Created, AggregateFunction.Count),
                 })
        {
            var exception = Assert.Throws<ArgumentException>(action);
            Assert.Contains(
                "not a supported numeric aggregate type",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal("value", exception.ParamName);
        }

        Assert.True(registry.Indexes.IsEmpty);
    }

    [Fact]
    public void AggregateRejectsZeroAndUnsupportedFunctionMasks()
    {
        var registry = new SchemaRegistry();
        var builder = registry.Node<TypeSafeAggregateContract>();

        var none = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Tier,
                contract => contract.Amount,
                AggregateFunction.None));
        Assert.Contains("At least one supported aggregate function", none.Message, StringComparison.Ordinal);
        Assert.Equal("functions", none.ParamName);

        var unsupported = Assert.Throws<ArgumentException>(
            () => builder.AddPropertyAggregate(
                contract => contract.Tier,
                contract => contract.Amount,
                (AggregateFunction)1024));
        Assert.Contains("At least one supported aggregate function", unsupported.Message, StringComparison.Ordinal);
        Assert.Equal("functions", unsupported.ParamName);

        Assert.True(registry.Indexes.IsEmpty);
    }

    [Fact]
    public void RepeatedAggregateDeclarationsMergeFunctionsWithoutDuplicatingStorage()
    {
        var registry = new SchemaRegistry();
        registry.Node<TypeSafeAggregateContract>().AddPropertyAggregate(
            contract => contract.Tier,
            contract => contract.Amount,
            AggregateFunction.Count);
        registry.Node<TypeSafeAggregateContract>().AddPropertyAggregate(
            contract => contract.Tier,
            contract => contract.Amount,
            AggregateFunction.Sum | AggregateFunction.Min);
        registry.Node<TypeSafeAggregateContract>().AddPropertyAggregate(
            contract => contract.Tier,
            contract => contract.Amount,
            AggregateFunction.Count);

        var aggregate = Assert.Single(registry.Indexes.PropertyAggregates);
        Assert.Equal(
            AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min,
            aggregate.Functions);
    }

    [Node(Label)]
    public sealed record TypeSafeAggregateContract : Node
    {
        [Property(Label = "tariff")]
        public string? Tier { get; set; }

        [Property(Label = "amount")]
        public long Amount { get; set; }

        [Property(Label = "mapped_amount")]
        public long MappedAmount { get; set; }

        public sbyte SbyteValue { get; set; }

        public byte ByteValue { get; set; }

        public short ShortValue { get; set; }

        public ushort UshortValue { get; set; }

        public int IntValue { get; set; }

        public uint UintValue { get; set; }

        public long LongValue { get; set; }

        public ulong UlongValue { get; set; }

        public float FloatValue { get; set; }

        public double DoubleValue { get; set; }

        public decimal DecimalValue { get; set; }

        public sbyte? NullableSbyteValue { get; set; }

        public byte? NullableByteValue { get; set; }

        public short? NullableShortValue { get; set; }

        public ushort? NullableUshortValue { get; set; }

        public int? NullableIntValue { get; set; }

        public uint? NullableUintValue { get; set; }

        public long? NullableLongValue { get; set; }

        public ulong? NullableUlongValue { get; set; }

        public float? NullableFloatValue { get; set; }

        public double? NullableDoubleValue { get; set; }

        public decimal? NullableDecimalValue { get; set; }

        public string? Text { get; set; }

        public bool Active { get; set; }

        public Guid Reference { get; set; }

        public DateTime Created { get; set; }

        public int Count { get; set; }

        public int Other { get; set; }

        public TypeSafeAggregateAddress? Address { get; set; }

        [Property(Ignore = true)]
        public string Ignored => Tier ?? string.Empty;

        internal string PublicField = string.Empty;

        internal string Hidden { get; set; } = string.Empty;

        public static string StaticLabel { get; set; } = string.Empty;

        public string this[int index] => index == 0 ? Tier ?? string.Empty : string.Empty;

        public string GetLabel() => Tier ?? string.Empty;
    }

    public sealed class TypeSafeAggregateAddress
    {
        public string City { get; set; } = string.Empty;
    }
}
