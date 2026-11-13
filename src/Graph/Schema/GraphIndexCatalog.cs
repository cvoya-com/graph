// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

/// <summary>
/// An immutable snapshot of the property indexes and aggregate definitions registered with a
/// <see cref="SchemaRegistry"/>.
/// </summary>
/// <remarks>
/// The catalog is intentionally small and provider-neutral. It carries the declarations, not the
/// physical structures a provider may derive from them, so a provider can decide which declarations
/// it can honor. Repeated lookups use ordinal comparison for both label and property names.
/// </remarks>
public sealed class GraphIndexCatalog
{
    private readonly Dictionary<IndexKey, PropertyIndexDefinition> indexes;
    private readonly Dictionary<AggregateKey, PropertyAggregateDefinition> aggregates;
    private readonly PropertyIndexDefinition[] orderedIndexes;
    private readonly PropertyAggregateDefinition[] orderedAggregates;

    internal GraphIndexCatalog(
        IEnumerable<PropertyIndexDefinition> indexes,
        IEnumerable<PropertyAggregateDefinition> aggregates,
        long version)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(aggregates);

        orderedIndexes = indexes
            .OrderBy(index => index.Label, StringComparer.Ordinal)
            .ThenBy(index => index.PropertyName, StringComparer.Ordinal)
            .ThenBy(index => index.Kind)
            .ToArray();
        orderedAggregates = aggregates
            .OrderBy(aggregate => aggregate.Label, StringComparer.Ordinal)
            .ThenBy(aggregate => aggregate.GroupKeyProperty, StringComparer.Ordinal)
            .ThenBy(aggregate => aggregate.ValueProperty, StringComparer.Ordinal)
            .ToArray();
        this.indexes = orderedIndexes.ToDictionary(
            index => new IndexKey(index.Label, index.PropertyName, index.Kind));
        this.aggregates = orderedAggregates.ToDictionary(
            aggregate => new AggregateKey(
                aggregate.Label,
                aggregate.GroupKeyProperty,
                aggregate.ValueProperty));
        Version = version;
    }

    /// <summary>A catalog with no declarations.</summary>
    public static GraphIndexCatalog Empty { get; } = new([], [], version: 0);

    /// <summary>Gets a monotonic version that changes whenever a declaration is added or cleared.</summary>
    public long Version { get; }

    /// <summary>Gets whether the catalog declares no indexes and no aggregates.</summary>
    public bool IsEmpty => orderedIndexes.Length == 0 && orderedAggregates.Length == 0;

    /// <summary>Gets every declared property index, ordered by label, property, and kind.</summary>
    public IReadOnlyList<PropertyIndexDefinition> PropertyIndexes => orderedIndexes;

    /// <summary>Gets every declared aggregate, ordered by label, group key, and value property.</summary>
    public IReadOnlyList<PropertyAggregateDefinition> PropertyAggregates => orderedAggregates;

    /// <summary>Finds one declared property index.</summary>
    /// <param name="label">The node label.</param>
    /// <param name="propertyName">The stored property name.</param>
    /// <param name="kind">The access pattern.</param>
    /// <param name="index">The matching definition when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the declaration exists.</returns>
    public bool TryGetIndex(
        string label,
        string propertyName,
        PropertyIndexKind kind,
        out PropertyIndexDefinition index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        return indexes.TryGetValue(new IndexKey(label, propertyName, kind), out index!);
    }

    /// <summary>Finds one declared aggregate.</summary>
    /// <param name="label">The node label.</param>
    /// <param name="groupKeyProperty">The stored group key property.</param>
    /// <param name="valueProperty">The stored aggregate value property.</param>
    /// <param name="aggregate">The matching definition when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the declaration exists.</returns>
    public bool TryGetAggregate(
        string label,
        string groupKeyProperty,
        string valueProperty,
        out PropertyAggregateDefinition aggregate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupKeyProperty);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueProperty);
        return aggregates.TryGetValue(
            new AggregateKey(label, groupKeyProperty, valueProperty),
            out aggregate!);
    }

    /// <summary>Gets every declared index for one node label.</summary>
    /// <param name="label">The node label.</param>
    /// <returns>The matching declarations, ordered by property and kind.</returns>
    public IReadOnlyList<PropertyIndexDefinition> GetIndexes(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return orderedIndexes.Where(index => string.Equals(index.Label, label, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Gets every declared aggregate for one node label.</summary>
    /// <param name="label">The node label.</param>
    /// <returns>The matching declarations, ordered by group key and value property.</returns>
    public IReadOnlyList<PropertyAggregateDefinition> GetAggregates(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return orderedAggregates
            .Where(aggregate => string.Equals(aggregate.Label, label, StringComparison.Ordinal))
            .ToArray();
    }

    private readonly record struct IndexKey(
        string Label,
        string PropertyName,
        PropertyIndexKind Kind);

    private readonly record struct AggregateKey(
        string Label,
        string GroupKeyProperty,
        string ValueProperty);
}
