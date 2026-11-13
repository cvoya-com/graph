// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph;

/// <summary>
/// Declares that a provider may accelerate equality or ordering predicates over one scalar property
/// of one node label.
/// </summary>
/// <remarks>
/// A definition is provider-neutral metadata. Registering it never changes storage by itself; the
/// provider decides whether it can honor the declaration. A store with no registered definitions
/// keeps the provider's ordinary query and storage behavior.
/// </remarks>
public sealed record PropertyIndexDefinition
{
    /// <summary>Initializes a new index definition.</summary>
    /// <param name="label">The node label the definition applies to.</param>
    /// <param name="propertyName">The stored property name the definition applies to.</param>
    /// <param name="kind">The access pattern the definition accelerates.</param>
    /// <exception cref="ArgumentException">
    /// A name is empty or contains a NUL character, or <paramref name="kind"/> is not defined.
    /// </exception>
    public PropertyIndexDefinition(
        string label,
        string propertyName,
        PropertyIndexKind kind = PropertyIndexKind.Equality)
    {
        Label = ValidateName(label, nameof(label));
        PropertyName = ValidateName(propertyName, nameof(propertyName));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException($"The property index kind '{kind}' is not defined.", nameof(kind));
        }

        Kind = kind;
    }

    /// <summary>Gets the node label the definition applies to.</summary>
    public string Label { get; }

    /// <summary>Gets the stored property name the definition applies to.</summary>
    public string PropertyName { get; }

    /// <summary>Gets the access pattern the definition accelerates.</summary>
    public PropertyIndexKind Kind { get; }

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
