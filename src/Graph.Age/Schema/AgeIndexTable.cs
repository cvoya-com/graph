// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Schema;

using System.Text;

/// <summary>
/// Describes the provider-owned per-label companion table that stores the text and numeric columns
/// declared by a <see cref="GraphIndexCatalog"/>.
/// </summary>
/// <remarks>
/// The companion table exists instead of extra columns on the AGE label table. AGE builds a vertex
/// value from <c>SELECT *</c> during variable-length relationship traversal; an extra column on any
/// label table makes that path fail with <c>Invalid number of attributes</c>. Keeping every derived
/// column in a separate table preserves the label table's two-column row shape, so L19 and every
/// other VLE/complex-property query keep working.
/// <para>
/// Consistency is owned by the provider write path: create, update, and delete synchronize the
/// companion row in the same transaction. <see cref="AgeIndexMaintenance"/> backfills existing rows.
/// Direct SQL writes that bypass the provider require a re-run of the maintenance operation.
/// </para>
/// </remarks>
internal static class AgeIndexTable
{
    /// <summary>
    /// Opaque token replaced with the quoted graph schema by the command builder. The token is
    /// generated once per process so a user-supplied literal or identifier cannot contain it.
    /// </summary>
    internal static readonly string GraphToken = "{" + Guid.NewGuid().ToString("N") + "}";

    /// <summary>
    /// Opaque token replaced with the agtype parameter placeholder by the command builder. The token
    /// is generated once per process so a user-supplied literal cannot contain it.
    /// </summary>
    internal static readonly string ParameterToken = "{" + Guid.NewGuid().ToString("N") + "}";

    internal const string IdColumn = "id";

    /// <summary>Quotes a PostgreSQL identifier, doubling embedded quotes.</summary>
    internal static string QuoteIdentifier(string value, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"The {description} contains a NUL character.", nameof(value));
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Quotes a PostgreSQL string literal, doubling embedded quotes.</summary>
    internal static string QuoteLiteral(string value, string description)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"The {description} contains a NUL character.", nameof(value));
        }

        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Renders a JSON string as an agtype literal.</summary>
    internal static string AgtypeStringLiteral(string value) =>
        QuoteLiteral(JsonStringLiteral(value), "agtype string") + "::text::ag_catalog.agtype";

    /// <summary>Returns the companion table name for one physical label table.</summary>
    internal static string TableName(string label) => AgePhysicalName.IndexTable(label);

    /// <summary>Returns the qualified physical label table name.</summary>
    internal static string QualifiedLabelTable(string graphName, string label) =>
        $"{QuoteIdentifier(graphName, "graph name")}.{QuoteIdentifier(label, "label name")}";

    /// <summary>Returns the qualified companion table name.</summary>
    internal static string QualifiedTable(string graphNameOrToken, string label, string? tableName = null) =>
        $"{QuoteIdentifier(graphNameOrToken, "graph name")}." +
        $"{QuoteIdentifier(tableName ?? TableName(label), "index table name")}";

    /// <summary>Builds the idempotent companion table statement for one label.</summary>
    internal static string CreateTableSql(
        string graphName,
        string label,
        IReadOnlyList<AgeIndexedProperty> properties,
        IReadOnlyList<(string Property, string ColumnName)> valueColumns,
        string? tableName = null)
    {
        var columns = new List<string>
        {
            $"{QuoteIdentifier(IdColumn, "column name")} ag_catalog.graphid PRIMARY KEY",
        };
        columns.AddRange(properties.Select(property =>
            $"{QuoteIdentifier(property.ColumnName, "column name")} text"));
        columns.AddRange(valueColumns.Select(column =>
            $"{QuoteIdentifier(column.ColumnName, "column name")} numeric"));
        return $"CREATE TABLE IF NOT EXISTS {QualifiedTable(graphName, label, tableName)} (" +
            string.Join(", ", columns) + ")";
    }

    /// <summary>Builds the idempotent statements that add newly declared columns to an existing table.</summary>
    internal static IEnumerable<string> AddColumnSql(
        string graphName,
        string label,
        IReadOnlyList<AgeIndexedProperty> properties,
        IReadOnlyList<(string Property, string ColumnName)> valueColumns,
        string? tableName = null)
    {
        foreach (var property in properties)
        {
            yield return $"ALTER TABLE {QualifiedTable(graphName, label, tableName)} " +
                $"ADD COLUMN IF NOT EXISTS {QuoteIdentifier(property.ColumnName, "column name")} text";
        }

        foreach (var column in valueColumns)
        {
            yield return $"ALTER TABLE {QualifiedTable(graphName, label, tableName)} " +
                $"ADD COLUMN IF NOT EXISTS {QuoteIdentifier(column.ColumnName, "column name")} numeric";
        }
    }

    /// <summary>Builds the idempotent status B-tree statement for one text column.</summary>
    internal static string CreateIndexSql(
        string graphName,
        string label,
        AgeIndexedProperty property,
        string? tableName = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.IndexName is null)
        {
            throw new ArgumentException("The property has no index name.", nameof(property));
        }

        return $"CREATE INDEX IF NOT EXISTS {QuoteIdentifier(property.IndexName, "index name")} " +
            $"ON {QualifiedTable(graphName, label, tableName)} ({QuoteIdentifier(property.ColumnName, "column name")})";
    }

    /// <summary>Builds the idempotent companion table removal statement for one label.</summary>
    internal static string DropTableSql(string graphName, string label, string? tableName = null) =>
        $"DROP TABLE IF EXISTS {QualifiedTable(graphName, label, tableName)} CASCADE";

    /// <summary>Builds the backfill statement that copies every label row into its companion table.</summary>
    internal static string BackfillSql(
        string graphName,
        string label,
        IReadOnlyList<AgeIndexedProperty> properties,
        IReadOnlyList<(string Property, string ColumnName)> valueColumns,
        string? tableName = null)
    {
        var columns = new List<string> { QuoteIdentifier(IdColumn, "column name") };
        columns.AddRange(properties.Select(property => QuoteIdentifier(property.ColumnName, "column name")));
        columns.AddRange(valueColumns.Select(column => QuoteIdentifier(column.ColumnName, "column name")));

        var values = new List<string> { "id" };
        values.AddRange(properties.Select(property => TextValueSql(property.Property)));
        values.AddRange(valueColumns.Select(column => NumericValueSql(column.Property)));

        var updates = new List<string>();
        updates.AddRange(properties.Select(property =>
            $"{QuoteIdentifier(property.ColumnName, "column name")} = EXCLUDED." +
            $"{QuoteIdentifier(property.ColumnName, "column name")}"));
        updates.AddRange(valueColumns.Select(column =>
            $"{QuoteIdentifier(column.ColumnName, "column name")} = EXCLUDED." +
            $"{QuoteIdentifier(column.ColumnName, "column name")}"));

        return $"INSERT INTO {QualifiedTable(graphName, label, tableName)} " +
            $"({string.Join(", ", columns)}) " +
            $"SELECT {string.Join(", ", values)} " +
            $"FROM {QualifiedLabelTable(graphName, label)} " +
            $"ON CONFLICT ({QuoteIdentifier(IdColumn, "column name")}) DO UPDATE SET " +
            string.Join(", ", updates);
    }

    /// <summary>SQL that extracts a text property from an AGE properties value.</summary>
    internal static string TextValueSql(string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        return $"(properties::text::jsonb) ->> {QuoteLiteral(property, "property name")}";
    }

    /// <summary>SQL that extracts a numeric property from an AGE properties value.</summary>
    internal static string NumericValueSql(string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        var key = QuoteLiteral(property, "property name");
        return "CASE WHEN jsonb_typeof((properties::text::jsonb) -> " + key + ") = 'number' " +
            "THEN ((properties::text::jsonb) ->> " + key + ")::numeric ELSE NULL END";
    }

    private static string JsonStringLiteral(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u").Append(
                            ((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
