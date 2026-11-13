// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

/// <summary>
/// Declares that a provider may maintain per-group aggregates over one numeric property of one node
/// label, grouped by another property of the same label.
/// </summary>
/// <remarks>
/// Like <see cref="PropertyIndexDefinition"/>, this is provider-neutral metadata. Registering an
/// aggregate never changes query results: a provider either honors the declaration with a derived
/// structure or keeps its ordinary aggregation path. The group key and value properties must be
/// distinct stored property names; the value property must hold a numeric scalar.
/// </remarks>
public sealed record PropertyAggregateDefinition
{
    /// <summary>Initializes a new aggregate definition.</summary>
    /// <param name="label">The node label the definition applies to.</param>
    /// <param name="groupKeyProperty">The stored property that forms the group key.</param>
    /// <param name="valueProperty">The stored numeric property that is aggregated.</param>
    /// <param name="functions">The aggregate functions the provider may maintain.</param>
    /// <exception cref="ArgumentException">
    /// A name is empty or contains a NUL character, the two property names are equal, or no supported
    /// function is requested.
    /// </exception>
    public PropertyAggregateDefinition(
        string label,
        string groupKeyProperty,
        string valueProperty,
        AggregateFunction functions)
    {
        Label = ValidateName(label, nameof(label));
        GroupKeyProperty = ValidateName(groupKeyProperty, nameof(groupKeyProperty));
        ValueProperty = ValidateName(valueProperty, nameof(valueProperty));
        if (string.Equals(GroupKeyProperty, ValueProperty, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The group key property and the aggregate value property must be different.",
                nameof(valueProperty));
        }

        const AggregateFunction supported =
            AggregateFunction.Count |
            AggregateFunction.Sum |
            AggregateFunction.Min |
            AggregateFunction.Max |
            AggregateFunction.Average;
        if ((functions & ~supported) != 0 || (functions & supported) == 0)
        {
            throw new ArgumentException(
                "At least one supported aggregate function is required.",
                nameof(functions));
        }

        Functions = functions;
    }

    /// <summary>Gets the node label the definition applies to.</summary>
    public string Label { get; }

    /// <summary>Gets the stored property that forms the group key.</summary>
    public string GroupKeyProperty { get; }

    /// <summary>Gets the stored numeric property that is aggregated.</summary>
    public string ValueProperty { get; }

    /// <summary>Gets the aggregate functions the provider may maintain.</summary>
    public AggregateFunction Functions { get; }

    private static string ValidateName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The value contains a NUL character.", parameterName);
        }

        return value;
    }
}
