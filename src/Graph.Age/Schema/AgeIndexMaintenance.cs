// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Schema;

using Npgsql;

/// <summary>
/// Materializes and removes the provider-owned structures declared by a <see cref="GraphIndexCatalog"/>
/// for one AGE graph.
/// </summary>
/// <remarks>
/// Every statement is idempotent so the operation can be rerun after a partial failure or after new
/// labels were created. The whole pass runs in one transaction holding the graph provisioning lock,
/// which serializes it with <c>create_vlabel</c> from a concurrent writer. A new label created by the
/// provider while the catalog is provisioned receives its structures inside the creating
/// transaction, before the first row is written.
/// <para>
/// A documented catalog table (<c>__cvoya_index</c>) records every physical object and its format
/// version. <see cref="EnsureAsync"/> fails closed when a row declares a newer version than this
/// provider understands, so a downgrade cannot silently read a future layout.
/// </para>
/// </remarks>
internal static class AgeIndexMaintenance
{
    internal const string CatalogTableName = "__cvoya_index";

    internal const string IndexKind = "index";
    internal const string AggregateKind = "aggregate";

    /// <summary>Adds or refreshes every declared structure and backfills existing rows.</summary>
    internal static async Task<GraphIndexMaintenanceReport> EnsureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        AgeIndexCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphName);
        ArgumentNullException.ThrowIfNull(catalog);

        var labels = await DiscoverVertexLabelsAsync(connection, transaction, graphName, cancellationToken)
            .ConfigureAwait(false);
        await EnsureCatalogTableAsync(connection, transaction, graphName, cancellationToken)
            .ConfigureAwait(false);
        await ValidateFormatVersionAsync(connection, transaction, graphName, cancellationToken)
            .ConfigureAwait(false);

        var definitions = catalog.DeclarationCount;
        var structures = 0;
        var rows = 0L;
        var createdTables = new List<string>();
        foreach (var label in labels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = catalog.GetProperties(label);
            var valueColumns = catalog.GetValueColumns(label);
            var aggregates = catalog.GetAggregates(label);
            if (properties.Count == 0 && valueColumns.Count == 0 && aggregates.Count == 0)
            {
                continue;
            }

            var tableName = catalog.GetTableName(label);
            if (string.IsNullOrEmpty(tableName))
            {
                continue;
            }

            var hadTable = await TableExistsAsync(
                    connection, transaction, graphName, tableName, cancellationToken)
                .ConfigureAwait(false);
            var missingIndexes = 0;
            foreach (var property in properties)
            {
                if (!await IndexExistsAsync(
                        connection, transaction, graphName, tableName, property.IndexName!, cancellationToken)
                    .ConfigureAwait(false))
                {
                    missingIndexes++;
                }
            }

            await EnsureTableAsync(
                    connection, transaction, graphName, label, tableName, properties, valueColumns, cancellationToken)
                .ConfigureAwait(false);
            if (!hadTable)
            {
                structures++;
                createdTables.Add(tableName);
            }

            structures += missingIndexes;

            rows += await ExecuteAsync(
                    connection,
                    transaction,
                    AgeIndexTable.BackfillSql(graphName, label, properties, valueColumns, tableName),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var aggregate in aggregates)
            {
                var hadAggregateTable = await TableExistsAsync(
                        connection, transaction, graphName, aggregate.TableName, cancellationToken)
                    .ConfigureAwait(false);
                await DropAggregateFunctionsAsync(connection, transaction, graphName, aggregate, cancellationToken)
                    .ConfigureAwait(false);
                await EnsureAggregateAsync(connection, transaction, graphName, aggregate, cancellationToken)
                    .ConfigureAwait(false);
                if (!hadAggregateTable)
                {
                    structures++;
                    createdTables.Add(aggregate.TableName);
                }
            }

            await ExecuteAsync(
                    connection,
                    transaction,
                    $"ANALYZE {AgeIndexTable.QualifiedTable(graphName, label, tableName)}",
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var aggregate in aggregates)
            {
                await ExecuteAsync(
                        connection,
                        transaction,
                        $"ANALYZE {AgeAggregateTable.QualifiedTable(graphName, aggregate)}",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var property in properties)
            {
                await RecordAsync(
                        connection,
                        transaction,
                        graphName,
                        IndexKind,
                        label,
                        property.Property,
                        auxProperty: string.Empty,
                        tableName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var valueColumn in valueColumns)
            {
                await RecordAsync(
                        connection,
                        transaction,
                        graphName,
                        "column",
                        label,
                        valueColumn.Property,
                        auxProperty: string.Empty,
                        tableName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var aggregate in aggregates)
            {
                await RecordAsync(
                        connection,
                        transaction,
                        graphName,
                        AggregateKind,
                        label,
                        aggregate.GroupKeyProperty,
                        aggregate.ValueProperty,
                        aggregate.TableName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new GraphIndexMaintenanceReport(graphName, definitions, structures, StructuresRemoved: 0, rows)
        {
            CreatedTableNames = createdTables,
        };
    }

    /// <summary>Removes every declared structure and the provider catalog.</summary>
    internal static async Task<GraphIndexMaintenanceReport> DropAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        AgeIndexCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphName);
        ArgumentNullException.ThrowIfNull(catalog);

        var definitions = 0;
        var removed = 0;
        var removedTables = new List<string>();
        var countedLabels = new HashSet<string>(StringComparer.Ordinal);

        // Fail closed before removing structures this provider does not understand.
        if (await TableExistsAsync(connection, transaction, graphName, CatalogTableName, cancellationToken)
            .ConfigureAwait(false))
        {
            await ValidateFormatVersionAsync(connection, transaction, graphName, cancellationToken)
                .ConfigureAwait(false);
        }

        // Drop what the documented catalog recorded, so a definition removed from the registry
        // (or a definition whose physical name changed) does not leave an orphan behind.
        var recorded = await ReadCatalogRowsAsync(connection, transaction, graphName, cancellationToken)
            .ConfigureAwait(false);
        var recordedTables = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in recorded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (countedLabels.Add(row.Label))
            {
                // Report user declarations, not the derived group-key index rows the catalog also
                // stores so an aggregate-only label's companion table can be dropped.
                definitions += catalog.GetDeclarationCount(row.Label);
            }

            if (string.Equals(row.Kind, AggregateKind, StringComparison.Ordinal))
            {
                var aggregate = new AgeAggregateIndex(
                    row.Label,
                    row.Property,
                    row.AuxProperty,
                    AggregateFunction.None,
                    row.TableName,
                    AgePhysicalName.IndexTable(row.Label),
                    AgePhysicalName.TextColumn(row.Label, row.Property),
                    AgePhysicalName.NumericColumn(row.Label, row.AuxProperty));
                await DropAggregateFunctionsAsync(connection, transaction, graphName, aggregate, cancellationToken)
                    .ConfigureAwait(false);
                await EnsureNotAgeLabelAsync(
                        connection, transaction, graphName, aggregate.TableName, cancellationToken)
                    .ConfigureAwait(false);
                if (await TableExistsAsync(
                        connection, transaction, graphName, aggregate.TableName, cancellationToken)
                    .ConfigureAwait(false))
                {
                    removed++;
                    removedTables.Add(aggregate.TableName);
                }

                await ExecuteAsync(
                        connection,
                        transaction,
                        AgeAggregateTable.DropTableSql(graphName, aggregate),
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!recordedTables.TryGetValue(row.Label, out var tables))
            {
                tables = new HashSet<string>(StringComparer.Ordinal);
                recordedTables[row.Label] = tables;
            }

            tables.Add(row.TableName);
        }

        foreach (var (label, tableNames) in recordedTables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var tableName in tableNames)
            {
                await EnsureNotAgeLabelAsync(connection, transaction, graphName, tableName, cancellationToken)
                    .ConfigureAwait(false);
                if (await TableExistsAsync(
                        connection, transaction, graphName, tableName, cancellationToken)
                    .ConfigureAwait(false))
                {
                    removed++;
                    removedTables.Add(tableName);
                }

                await ExecuteAsync(
                        connection,
                        transaction,
                        AgeIndexTable.DropTableSql(graphName, label, tableName),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Generalised fallback for a graph provisioned before the catalog table existed. Only the
        // current __idx family is probed; the catalog is authoritative for aggregates.
        var labels = await DiscoverVertexLabelsAsync(connection, transaction, graphName, cancellationToken)
            .ConfigureAwait(false);
        foreach (var label in labels)
        {
            if (recordedTables.ContainsKey(label))
            {
                continue;
            }

            var declarations = catalog.GetDeclarationCount(label);
            if (countedLabels.Add(label))
            {
                // Count user declarations for every inspected label, including an ordering-only
                // declaration that materializes no structure.
                definitions += declarations;
            }

            var properties = catalog.GetProperties(label);
            var valueColumns = catalog.GetValueColumns(label);
            var aggregates = catalog.GetAggregates(label);
            if (properties.Count == 0 && valueColumns.Count == 0 && aggregates.Count == 0)
            {
                var preCatalogTable = AgePhysicalName.IndexTable(label);
                if (!await TableExistsAsync(
                        connection, transaction, graphName, preCatalogTable, cancellationToken)
                    .ConfigureAwait(false))
                {
                    continue;
                }

                await EnsureNotAgeLabelAsync(
                        connection, transaction, graphName, preCatalogTable, cancellationToken)
                    .ConfigureAwait(false);
                await ExecuteAsync(
                        connection,
                        transaction,
                        AgeIndexTable.DropTableSql(graphName, label, preCatalogTable),
                        cancellationToken)
                    .ConfigureAwait(false);
                removed++;
                removedTables.Add(preCatalogTable);
                continue;
            }

            foreach (var aggregate in aggregates)
            {
                await EnsureNotAgeLabelAsync(
                        connection, transaction, graphName, aggregate.TableName, cancellationToken)
                    .ConfigureAwait(false);
                await DropAggregateFunctionsAsync(connection, transaction, graphName, aggregate, cancellationToken)
                    .ConfigureAwait(false);
                if (await TableExistsAsync(
                        connection, transaction, graphName, aggregate.TableName, cancellationToken)
                    .ConfigureAwait(false))
                {
                    removed++;
                    removedTables.Add(aggregate.TableName);
                }

                await ExecuteAsync(
                        connection,
                        transaction,
                        AgeAggregateTable.DropTableSql(graphName, aggregate),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var tableName = catalog.GetTableName(label);
            if (string.IsNullOrEmpty(tableName))
            {
                tableName = AgePhysicalName.IndexTable(label);
            }

            await EnsureNotAgeLabelAsync(connection, transaction, graphName, tableName, cancellationToken)
                .ConfigureAwait(false);
            if (await TableExistsAsync(
                    connection, transaction, graphName, tableName, cancellationToken)
                .ConfigureAwait(false))
            {
                removed++;
                removedTables.Add(tableName);
            }

            await ExecuteAsync(
                    connection,
                    transaction,
                    AgeIndexTable.DropTableSql(graphName, label, tableName),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await EnsureNotAgeLabelAsync(connection, transaction, graphName, CatalogTableName, cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                $"DROP TABLE IF EXISTS {QualifiedCatalogTable(graphName)}",
                cancellationToken)
            .ConfigureAwait(false);
        return new GraphIndexMaintenanceReport(graphName, definitions, StructuresCreated: 0, removed, RowsBackfilled: 0)
        {
            RemovedTableNames = removedTables,
        };
    }

    /// <summary>Adds the structures of one newly created label inside an already locked transaction.</summary>
    internal static async Task EnsureLabelAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        string label,
        AgeIndexCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphName);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(catalog);

        var properties = catalog.GetProperties(label);
        var valueColumns = catalog.GetValueColumns(label);
        var aggregates = catalog.GetAggregates(label);
        if (properties.Count == 0 && valueColumns.Count == 0 && aggregates.Count == 0)
        {
            return;
        }

        await EnsureCatalogTableAsync(connection, transaction, graphName, cancellationToken).ConfigureAwait(false);
        await ValidateFormatVersionAsync(connection, transaction, graphName, cancellationToken)
            .ConfigureAwait(false);
        var tableName = catalog.GetTableName(label);
        if (string.IsNullOrEmpty(tableName))
        {
            return;
        }

        await EnsureTableAsync(
                connection, transaction, graphName, label, tableName, properties, valueColumns, cancellationToken)
            .ConfigureAwait(false);
        foreach (var aggregate in aggregates)
        {
            await EnsureAggregateAsync(connection, transaction, graphName, aggregate, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var property in properties)
        {
            await RecordAsync(
                    connection,
                    transaction,
                    graphName,
                    IndexKind,
                    label,
                    property.Property,
                    auxProperty: string.Empty,
                    tableName,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var valueColumn in valueColumns)
        {
            await RecordAsync(
                    connection,
                    transaction,
                    graphName,
                    "column",
                    label,
                    valueColumn.Property,
                    auxProperty: string.Empty,
                    tableName,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var aggregate in aggregates)
        {
            await RecordAsync(
                    connection,
                    transaction,
                    graphName,
                    AggregateKind,
                    label,
                    aggregate.GroupKeyProperty,
                    aggregate.ValueProperty,
                    aggregate.TableName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task EnsureTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        string label,
        string tableName,
        IReadOnlyList<AgeIndexedProperty> properties,
        IReadOnlyList<(string Property, string ColumnName)> valueColumns,
        CancellationToken cancellationToken)
    {
        await EnsureNotAgeLabelAsync(connection, transaction, graphName, tableName, cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                AgeIndexTable.CreateTableSql(graphName, label, properties, valueColumns, tableName),
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var statement in AgeIndexTable.AddColumnSql(
            graphName, label, properties, valueColumns, tableName))
        {
            await ExecuteAsync(connection, transaction, statement, cancellationToken).ConfigureAwait(false);
        }

        foreach (var property in properties)
        {
            await ExecuteAsync(
                    connection,
                    transaction,
                    AgeIndexTable.CreateIndexSql(graphName, label, property, tableName),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task EnsureAggregateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        AgeAggregateIndex aggregate,
        CancellationToken cancellationToken)
    {
        await EnsureNotAgeLabelAsync(
                connection, transaction, graphName, aggregate.TableName, cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                AgeAggregateTable.CreateTableSql(graphName, aggregate),
                cancellationToken)
            .ConfigureAwait(false);
        // Lock every group before the rebuild reads the per-row companion table, so a concurrent
        // writer holding one of these locks is observed with its committed rows instead of being
        // overwritten with a stale snapshot by the backfill's EXCLUDED assignments.
        await ExecuteAsync(
                connection,
                transaction,
                AgeAggregateTable.LockGroupsSql(graphName, aggregate),
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                AgeAggregateTable.ClearSql(graphName, aggregate),
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                AgeAggregateTable.PopulateSql(graphName, aggregate),
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                AgeAggregateTable.CreateInsertFunctionSql(graphName, aggregate),
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                AgeAggregateTable.CreateUpdateFunctionSql(graphName, aggregate),
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                AgeAggregateTable.CreateDeleteFunctionSql(graphName, aggregate),
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var statement in AgeAggregateTable.CreateTriggerSql(graphName, aggregate))
        {
            await ExecuteAsync(connection, transaction, statement, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DropAggregateFunctionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        AgeAggregateIndex aggregate,
        CancellationToken cancellationToken)
    {
        foreach (var statement in AgeAggregateTable.DropFunctionSql(graphName, aggregate))
        {
            await ExecuteAsync(connection, transaction, statement, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects a provider-owned table name that is already an AGE vertex label. A user label with
    /// the same name must never be altered or dropped as if it were a provider companion.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Command text is a compile-time constant; graph and table names are bound parameters.")]
    private static async Task EnsureNotAgeLabelAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        string tableName,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var lease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM ag_catalog.ag_label AS label
                JOIN ag_catalog.ag_graph AS graph ON graph.graphid = label.graph
                WHERE graph.name = @graphName
                  AND label.name = @tableName)
            """;
        command.Parameters.AddWithValue("graphName", graphName);
        command.Parameters.AddWithValue("tableName", tableName);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
        {
            throw new GraphException(
                $"The AGE graph '{graphName}' already has a label named '{tableName}'. " +
                "Provider index objects use reserved companion names; rename the label or the declaration.");
        }
    }

    private static async Task EnsureCatalogTableAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        CancellationToken cancellationToken)
    {
        await EnsureNotAgeLabelAsync(connection, transaction, graphName, CatalogTableName, cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(
                connection,
                transaction,
                $"""
                CREATE TABLE IF NOT EXISTS {QualifiedCatalogTable(graphName)} (
                    kind text NOT NULL,
                    label text NOT NULL,
                    property text NOT NULL,
                    aux_property text NOT NULL DEFAULT '',
                    format_version integer NOT NULL,
                    table_name text NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (kind, label, property, aux_property))
                """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Command text is composed from a quoted catalog identifier and compile-time SQL only.")]
    private static async Task ValidateFormatVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var lease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT COALESCE(max(format_version), 0) FROM {QualifiedCatalogTable(graphName)}";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var version = value is int current ? current : 0;
        if (version > AgePhysicalName.FormatVersion)
        {
            throw new GraphException(
                $"The AGE graph '{graphName}' index catalog uses format version {version}, " +
                $"but this provider understands version {AgePhysicalName.FormatVersion} or lower.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Command text is composed from a quoted catalog identifier; every value is a bound parameter.")]
    private static async Task RecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        string kind,
        string label,
        string property,
        string auxProperty,
        string tableName,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var lease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {QualifiedCatalogTable(graphName)}
                (kind, label, property, aux_property, format_version, table_name)
            VALUES (@kind, @label, @property, @auxProperty, @formatVersion, @tableName)
            ON CONFLICT (kind, label, property, aux_property) DO UPDATE SET
                format_version = EXCLUDED.format_version,
                table_name = EXCLUDED.table_name
            """;
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("label", label);
        command.Parameters.AddWithValue("property", property);
        command.Parameters.AddWithValue("auxProperty", auxProperty);
        command.Parameters.AddWithValue("formatVersion", AgePhysicalName.FormatVersion);
        command.Parameters.AddWithValue("tableName", tableName);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Command text is composed from a quoted catalog identifier and compile-time SQL only.")]
    private static async Task<List<CatalogRow>> ReadCatalogRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(
                connection, transaction, graphName, CatalogTableName, cancellationToken)
            .ConfigureAwait(false))
        {
            return [];
        }

        await EnsureNotAgeLabelAsync(connection, transaction, graphName, CatalogTableName, cancellationToken)
            .ConfigureAwait(false);

        var command = connection.CreateCommand();
        await using var lease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT kind, label, property, aux_property, table_name
            FROM {QualifiedCatalogTable(graphName)}
            ORDER BY kind, label, property, aux_property
            """;
        var rows = new List<CatalogRow>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerLease = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new CatalogRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private sealed record CatalogRow(
        string Kind,
        string Label,
        string Property,
        string AuxProperty,
        string TableName);

    private static async Task<List<string>> DiscoverVertexLabelsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var lease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT label.name
            FROM ag_catalog.ag_label AS label
            JOIN ag_catalog.ag_graph AS graph ON graph.graphid = label.graph
            WHERE graph.name = @graphName
              AND label.kind = 'v'
              AND label.name !~ '^_ag_'
            ORDER BY label.name
            """;
        command.Parameters.AddWithValue("graphName", graphName);
        var labels = new List<string>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerLease = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            labels.Add(reader.GetString(0));
        }

        return labels;
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        string tableName,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var lease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = @graphName
                  AND table_name = @tableName)
            """;
        command.Parameters.AddWithValue("graphName", graphName);
        command.Parameters.AddWithValue("tableName", tableName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<bool> IndexExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string graphName,
        string tableName,
        string indexName,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var lease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_indexes
                WHERE schemaname = @graphName
                  AND tablename = @tableName
                  AND indexname = @indexName)
            """;
        command.Parameters.AddWithValue("graphName", graphName);
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("indexName", indexName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static string QualifiedCatalogTable(string graphName) =>
        $"{AgeIndexTable.QuoteIdentifier(graphName, "graph name")}." +
        $"{AgeIndexTable.QuoteIdentifier(CatalogTableName, "catalog table name")}";

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Command text is composed from quoted catalog identifiers and compile-time DDL only.")]
    private static async Task<long> ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var lease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
