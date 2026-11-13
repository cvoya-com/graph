// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Schema;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Derives provider-owned physical names from a declaration. Names are deterministic so re-running
/// maintenance addresses the same objects, and every name fits PostgreSQL's 63-byte identifier
/// limit.
/// </summary>
/// <remarks>
/// Every label uses the same scheme: a per-label companion table named <c>&lt;label&gt;__idx</c> or a
/// bounded hashed form, stable hash-based column and B-tree index names, and per-declaration
/// aggregate tables and trigger functions derived from the label, group key, and value property.
/// There is no property-name-specific special case. A label whose properties are literally named
/// <c>Status</c> or <c>Kaufpreis</c> is stored exactly like any other label.
/// </remarks>
internal static class AgePhysicalName
{
    /// <summary>The format version recorded in the provider index catalog.</summary>
    internal const int FormatVersion = 1;

    /// <summary>
    /// The literal used as the group key for a SQL NULL group. It is the JSON <c>null</c> literal,
    /// which <see cref="GroupKeySql"/> never produces for a real string: real strings are JSON-encoded
    /// with quotes and escapes, so no valid string value - including one containing U+0001 - can
    /// collide with it.
    /// </summary>
    internal const string NullGroupKeySql = "'null'";

    /// <summary>
    /// Returns the SQL expression that maps a nullable group column to a collision-free text key:
    /// the JSON encoding of the value for real strings, or the JSON <c>null</c> literal for SQL NULL.
    /// <see cref="AgeAggregateTable"/> uses it as the primary-key expression of the aggregate
    /// companion table, so a missing group key and every valid string stay distinct groups.
    /// </summary>
    internal static string GroupKeySql(string groupColumn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupColumn);
        return $"COALESCE(to_jsonb({groupColumn})::text, {NullGroupKeySql})";
    }

    /// <summary>Returns the per-label index table name.</summary>
    internal static string IndexTable(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var candidate = label + "__idx";
        return Encoding.UTF8.GetByteCount(candidate) <= 63
            ? candidate
            : "idx_" + Hash(label) + "_table";
    }

    /// <summary>Returns the text column name that mirrors one equality property.</summary>
    internal static string TextColumn(string label, string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        return "p_" + Hash(label + "\u001f" + property + "\u001ftext");
    }

    /// <summary>Returns the numeric column name that mirrors one aggregate value property.</summary>
    internal static string NumericColumn(string label, string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        return "n_" + Hash(label + "\u001f" + property + "\u001fnumeric");
    }

    /// <summary>Returns the B-tree index name for one text column.</summary>
    internal static string TextIndex(string label, string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        return "idx_" + Hash(label + "\u001f" + property + "\u001ftextidx") + "_idx";
    }

    /// <summary>Returns the aggregate table name for one group key and value property.</summary>
    internal static string AggregateTable(string label, string groupKeyProperty, string valueProperty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupKeyProperty);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueProperty);
        return Bound(label + "__agg_" + Hash(
            label + "\u001f" + groupKeyProperty + "\u001f" + valueProperty + "\u001fagg"), "agg");
    }

    /// <summary>Returns the function name that maintains one aggregate table.</summary>
    internal static string AggregateFunction(
        string label,
        string groupKeyProperty,
        string valueProperty,
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return Bound(
            AggregateTable(label, groupKeyProperty, valueProperty) + "_" + operation + "_fn",
            "agg_fn");
    }

    /// <summary>Returns the trigger name that maintains one aggregate table.</summary>
    internal static string AggregateTrigger(
        string label,
        string groupKeyProperty,
        string valueProperty,
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return Bound(
            AggregateTable(label, groupKeyProperty, valueProperty) + "_" + operation + "_trg",
            "agg_trg");
    }

    /// <summary>Returns a deterministic 16-character lower-hex hash of a provider name input.</summary>
    internal static string Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..16];
    }

    /// <summary>Truncates a name to the identifier limit, or replaces it with a hashed form.</summary>
    internal static string Bound(string candidate, string hashPrefix)
    {
        if (Encoding.UTF8.GetByteCount(candidate) <= 63)
        {
            return candidate;
        }

        return hashPrefix + "_" + Hash(candidate);
    }
}
