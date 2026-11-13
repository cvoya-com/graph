// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Schema;

using Cvoya.Graph.Age.Serialization;

/// <summary>
/// The AGE provider's resolved view of a <see cref="GraphIndexCatalog"/>: it adds the physical
/// table and column names and the aggregate structures derived from the declarations.
/// </summary>
/// <remarks>
/// The catalog is immutable and cheap to construct. A provider creates one per store snapshot; the
/// version is part of the presence-cache key so a newly registered declaration cannot be served a
/// stale availability answer.
/// <para>
/// Declarations carry logical labels. AGE stores a non-native label under an encoded physical name,
/// so the resolved catalog keys every structure by the physical label the AGE tables and the
/// rendered Cypher use. Native labels are unchanged.
/// </para>
/// </remarks>
internal sealed class AgeIndexCatalog
{
    private readonly Dictionary<(string Label, string Property), AgeIndexedProperty> textProperties;
    private readonly HashSet<(string Label, string Property)> orderingProperties;
    private readonly Dictionary<(string Label, string Group, string Value), AgeAggregateIndex> aggregates;
    private readonly Dictionary<string, int> declarationCounts;
    private readonly AgeIndexedProperty[] propertyList;
    private readonly AgeAggregateIndex[] aggregateList;
    private readonly HashSet<string> labels;

    private AgeIndexCatalog(
        Dictionary<(string, string), AgeIndexedProperty> textProperties,
        HashSet<(string, string)> orderingProperties,
        Dictionary<(string, string, string), AgeAggregateIndex> aggregates,
        Dictionary<string, int> declarationCounts,
        long version,
        int declarationCount)
    {
        this.textProperties = textProperties;
        this.orderingProperties = orderingProperties;
        this.aggregates = aggregates;
        this.declarationCounts = declarationCounts;
        labels = textProperties.Keys.Select(key => key.Item1)
            .Concat(orderingProperties.Select(key => key.Item1))
            .Concat(aggregates.Keys.Select(key => key.Item1))
            .ToHashSet(StringComparer.Ordinal);
        propertyList = textProperties.Values
            .OrderBy(property => property.Label, StringComparer.Ordinal)
            .ThenBy(property => property.Property, StringComparer.Ordinal)
            .ToArray();
        aggregateList = aggregates.Values
            .OrderBy(aggregate => aggregate.Label, StringComparer.Ordinal)
            .ThenBy(aggregate => aggregate.GroupKeyProperty, StringComparer.Ordinal)
            .ThenBy(aggregate => aggregate.ValueProperty, StringComparer.Ordinal)
            .ToArray();
        Version = version;
        DeclarationCount = declarationCount;
    }

    /// <summary>An empty catalog.</summary>
    public static AgeIndexCatalog Empty { get; } = new(
        [],
        [],
        [],
        new Dictionary<string, int>(StringComparer.Ordinal),
        version: 0,
        declarationCount: 0);

    /// <summary>Gets the number of user declarations this catalog was built from.</summary>
    public int DeclarationCount { get; }

    /// <summary>Gets the declaration snapshot version this catalog was built from.</summary>
    public long Version { get; }

    /// <summary>Gets whether no declaration applies to the AGE provider.</summary>
    public bool IsEmpty => propertyList.Length == 0 && aggregateList.Length == 0 && orderingProperties.Count == 0;

    /// <summary>Gets every physical text equality column, ordered by label and property.</summary>
    public IReadOnlyList<AgeIndexedProperty> Properties => propertyList;

    /// <summary>Gets every aggregate structure, ordered by label, group key, and value property.</summary>
    public IReadOnlyList<AgeAggregateIndex> Aggregates => aggregateList;

    /// <summary>Builds the AGE view from a provider-neutral catalog snapshot.</summary>
    /// <param name="registry">The registry whose declarations are resolved.</param>
    /// <returns>A resolved catalog.</returns>
    public static AgeIndexCatalog From(SchemaRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var catalog = registry.Indexes;
        var text = new Dictionary<(string, string), AgeIndexedProperty>();
        var ordering = new HashSet<(string, string)>();
        var resolvedAggregates = new Dictionary<(string, string, string), AgeAggregateIndex>();
        var declarationCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var index in catalog.PropertyIndexes)
        {
            var label = SerializationBridge.GetRootStorageName(index.Label, relationship: false);
            declarationCounts[label] = declarationCounts.GetValueOrDefault(label) + 1;
            if (index.Kind == PropertyIndexKind.Ordering)
            {
                ordering.Add((label, index.PropertyName));
                continue;
            }

            AddTextProperty(text, label, index.PropertyName);
        }

        foreach (var aggregate in catalog.PropertyAggregates)
        {
            var label = SerializationBridge.GetRootStorageName(aggregate.Label, relationship: false);
            declarationCounts[label] = declarationCounts.GetValueOrDefault(label) + 1;

            // The group key must be readable as text from the per-row index table, and the value
            // must be readable as numeric. Both columns live on the same per-label table so the
            // aggregate trigger can see them in one row.
            AddTextProperty(text, label, aggregate.GroupKeyProperty);
            var valueColumn = AgePhysicalName.NumericColumn(label, aggregate.ValueProperty);
            var key = (label, aggregate.GroupKeyProperty, aggregate.ValueProperty);
            resolvedAggregates[key] = new AgeAggregateIndex(
                label,
                aggregate.GroupKeyProperty,
                aggregate.ValueProperty,
                aggregate.Functions,
                AgePhysicalName.AggregateTable(
                    label,
                    aggregate.GroupKeyProperty,
                    aggregate.ValueProperty),
                AgePhysicalName.IndexTable(label),
                AgePhysicalName.TextColumn(label, aggregate.GroupKeyProperty),
                valueColumn);
        }

        ValidatePhysicalNames(text, resolvedAggregates);
        return new AgeIndexCatalog(
            text,
            ordering,
            resolvedAggregates,
            declarationCounts,
            catalog.Version,
            catalog.PropertyIndexes.Count + catalog.PropertyAggregates.Count);
    }

    /// <summary>
    /// Rejects a hash collision before it can make two declarations share one physical column or
    /// table. The provider records names by declaration, so a collision is a configuration error
    /// rather than something to resolve at query time.
    /// </summary>
    private static void ValidatePhysicalNames(
        Dictionary<(string, string), AgeIndexedProperty> text,
        Dictionary<(string, string, string), AgeAggregateIndex> aggregates)
    {
        var columns = new Dictionary<(string Label, string Column), string>();
        var tables = new Dictionary<(string Label, string Table), string>();
        foreach (var property in text.Values)
        {
            AddPhysicalName(columns, (property.Label, property.ColumnName), "property '" + property.Property + "'");
            AddPhysicalName(tables, (property.Label, property.TableName), "index table");
        }

        foreach (var aggregate in aggregates.Values)
        {
            AddPhysicalName(
                columns,
                (aggregate.Label, aggregate.ValueColumnName),
                "aggregate value '" + aggregate.ValueProperty + "'");
            AddPhysicalName(
                tables,
                (aggregate.Label, aggregate.TableName),
                "aggregate '" + aggregate.GroupKeyProperty + "/" + aggregate.ValueProperty + "'");
        }
    }

    private static void AddPhysicalName<TKey>(
        Dictionary<TKey, string> names,
        TKey key,
        string owner)
        where TKey : notnull
    {
        if (names.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing, owner, StringComparison.Ordinal))
            {
                throw new GraphException(
                    $"The AGE index declarations for '{key}' collide: '{existing}' and '{owner}' " +
                    "resolve to the same physical object. Rename the property or register a different declaration.");
            }

            return;
        }

        names[key] = owner;
    }

    /// <summary>
    /// Resolves a logical label to the physical label this catalog keys structures by.
    /// </summary>
    /// <remarks>
    /// Native labels are unchanged. A non-native logical label hashes to the encoded AGE storage
    /// name; an already encoded name resolves to itself because it is a plain ASCII identifier.
    /// Callers that build SQL must use the result for table names as well as catalog lookups.
    /// </remarks>
    /// <param name="label">The logical or physical label.</param>
    /// <returns>The physical label used by AGE tables and rendered Cypher.</returns>
    public string ResolveLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return labels.Contains(label)
            ? label
            : SerializationBridge.GetRootStorageName(label, relationship: false);
    }

    /// <summary>Finds a declared equality or ordering property.</summary>
    /// <param name="label">The node label, logical or physical.</param>
    /// <param name="property">The stored property name.</param>
    /// <param name="kind">The access pattern.</param>
    /// <param name="index">The resolved descriptor. Ordering descriptors have no physical column.</param>
    /// <returns><see langword="true"/> when the declaration exists.</returns>
    public bool TryGetIndex(
        string label,
        string property,
        PropertyIndexKind kind,
        out AgeIndexedProperty index)
    {
        if (TryGetIndexCore(label, property, kind, out index))
        {
            return true;
        }

        var physicalLabel = ResolveLabel(label);
        return !string.Equals(physicalLabel, label, StringComparison.Ordinal) &&
            TryGetIndexCore(physicalLabel, property, kind, out index);
    }

    private bool TryGetIndexCore(
        string label,
        string property,
        PropertyIndexKind kind,
        out AgeIndexedProperty index)
    {
        if (kind == PropertyIndexKind.Equality &&
            textProperties.TryGetValue((label, property), out index!))
        {
            return true;
        }

        if (kind == PropertyIndexKind.Ordering && orderingProperties.Contains((label, property)))
        {
            index = new AgeIndexedProperty(
                label,
                property,
                AgePhysicalName.IndexTable(label),
                ColumnName: string.Empty,
                IndexName: null);
            return true;
        }

        index = null!;
        return false;
    }

    /// <summary>Finds one aggregate declaration.</summary>
    /// <param name="label">The node label, logical or physical.</param>
    /// <param name="groupKeyProperty">The declared group-key property.</param>
    /// <param name="valueProperty">The declared numeric value property.</param>
    /// <param name="aggregate">The resolved aggregate descriptor.</param>
    /// <returns><see langword="true"/> when the declaration exists.</returns>
    public bool TryGetAggregate(
        string label,
        string groupKeyProperty,
        string valueProperty,
        out AgeAggregateIndex aggregate)
    {
        if (aggregates.TryGetValue((label, groupKeyProperty, valueProperty), out aggregate!))
        {
            return true;
        }

        var physicalLabel = ResolveLabel(label);
        return !string.Equals(physicalLabel, label, StringComparison.Ordinal) &&
            aggregates.TryGetValue((physicalLabel, groupKeyProperty, valueProperty), out aggregate!);
    }

    /// <summary>Gets the per-label index table name, or an empty string when none is declared.</summary>
    public string GetTableName(string label)
    {
        var property = propertyList.FirstOrDefault(
            candidate => string.Equals(candidate.Label, label, StringComparison.Ordinal));
        return property?.TableName ?? string.Empty;
    }

    /// <summary>Gets the physical text columns for one label.</summary>
    public IReadOnlyList<AgeIndexedProperty> GetProperties(string label) =>
        propertyList.Where(property => string.Equals(property.Label, label, StringComparison.Ordinal)).ToArray();

    /// <summary>
    /// Gets the number of user declarations registered for one label. Derived group-key text
    /// properties are not declarations and are therefore not counted.
    /// </summary>
    public int GetDeclarationCount(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return declarationCounts.GetValueOrDefault(label);
    }

    /// <summary>Gets the aggregates for one label.</summary>
    public IReadOnlyList<AgeAggregateIndex> GetAggregates(string label) =>
        aggregateList.Where(aggregate => string.Equals(aggregate.Label, label, StringComparison.Ordinal)).ToArray();

    /// <summary>Gets the distinct numeric value columns for one label.</summary>
    public IReadOnlyList<(string Property, string ColumnName)> GetValueColumns(string label) =>
        aggregateList
            .Where(aggregate => string.Equals(aggregate.Label, label, StringComparison.Ordinal))
            .Select(aggregate => (aggregate.ValueProperty, aggregate.ValueColumnName))
            .Distinct()
            .ToArray();

    private static void AddTextProperty(
        Dictionary<(string, string), AgeIndexedProperty> destination,
        string label,
        string property)
    {
        var key = (label, property);
        if (destination.ContainsKey(key))
        {
            return;
        }

        destination[key] = new AgeIndexedProperty(
            label,
            property,
            AgePhysicalName.IndexTable(label),
            AgePhysicalName.TextColumn(label, property),
            AgePhysicalName.TextIndex(label, property));
    }
}
