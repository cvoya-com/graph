// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Cypher.Execution;

using System.Collections;
using System.Data.Common;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher.Lowering;
using Cvoya.Graph.Age.Schema;
using Cvoya.Graph.Age.Serialization;
using Cvoya.Graph.Cypher.Ast;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Npgsql.Age.Types;
using NpgsqlTypes;

internal sealed partial class AgeQueryRunner
{
    private readonly string graphName;
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction transaction;
    private readonly ILogger<AgeQueryRunner> logger;
    private readonly Action? batchExecutionObserver;
    private readonly Func<AgeBatchCommand, AgeBatchCommand>? batchCommandTransform;
    private readonly bool indexesProvisioned;
    private readonly AgeIndexCatalog indexCatalog;
    private readonly AgeIndexPresenceCache indexPresenceCache;
    private readonly long provisionedCatalogVersion;

    public AgeQueryRunner(
        string graphName,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ILoggerFactory? loggerFactory,
        Action? batchExecutionObserver = null,
        Func<AgeBatchCommand, AgeBatchCommand>? batchCommandTransform = null,
        bool indexesProvisioned = false,
        AgeIndexCatalog? indexCatalog = null,
        AgeIndexPresenceCache? indexPresenceCache = null,
        long provisionedCatalogVersion = 0)
    {
        this.graphName = AgeSqlIdentifier.Validate(graphName, "graph name");
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        this.transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        this.batchExecutionObserver = batchExecutionObserver;
        this.batchCommandTransform = batchCommandTransform;
        this.indexesProvisioned = indexesProvisioned;
        this.indexCatalog = indexCatalog ?? AgeIndexCatalog.Empty;
        this.indexPresenceCache = indexPresenceCache ?? new AgeIndexPresenceCache();
        this.provisionedCatalogVersion = provisionedCatalogVersion;
        logger = loggerFactory?.CreateLogger<AgeQueryRunner>() ?? NullLogger<AgeQueryRunner>.Instance;
    }

    public Task<AgeResultCursor> RunAsync(
        string cypher,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var dictionary = ToParameterDictionary(parameters);
        return RunAsync(cypher, dictionary, InferProjectionColumns(cypher), cancellationToken);
    }

    /// <summary>The AGE graph name, which is also the Postgres schema holding this graph's label tables.</summary>
    public string GraphName => graphName;

    /// <summary>
    /// Discovers or creates one native AGE vertex/edge label inside the current write transaction.
    /// The graph-scoped advisory lock serializes the catalog check-and-create sequence across stores.
    /// </summary>
    /// <remarks>
    /// Lock ordering: a write path that also takes uniqueness locks must acquire them <em>before</em>
    /// calling this method. Creating a missing label holds the graph-wide provisioning lock until the
    /// transaction ends, so acquiring the two lock kinds in opposite orders on two paths would let a
    /// first-use label creation deadlock against a peer already holding a uniqueness lock.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Command text selects between two compile-time constants; graph and label names are bound parameters.")]
    internal async Task EnsureLabelAsync(
        string label,
        bool relationship,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        cancellationToken.ThrowIfCancellationRequested();

        // The common path must not take the graph-wide provisioning lock: callers can hold
        // unrelated uniqueness locks for the lifetime of their transaction, and serializing every
        // ordinary write here would make independent writes block one another. A missing-label
        // result is only a hint; lock and probe again before creating to close the race.
        if (await LabelExistsAsync(label, relationship, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await AgeProvisioningLock
            .AcquireAsync(connection, transaction, graphName, cancellationToken)
            .ConfigureAwait(false);

        if (await LabelExistsAsync(label, relationship, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var createCommand = connection.CreateCommand();
        await using var createLease = createCommand.ConfigureAwait(false);
        createCommand.Transaction = transaction;
        createCommand.CommandText = relationship
            ? "SELECT ag_catalog.create_elabel(@graphName, @labelName)"
            : "SELECT ag_catalog.create_vlabel(@graphName, @labelName)";
        createCommand.Parameters.Add(new NpgsqlParameter("graphName", graphName)
        {
            NpgsqlDbType = NpgsqlDbType.Unknown,
        });
        createCommand.Parameters.Add(new NpgsqlParameter("labelName", label)
        {
            NpgsqlDbType = NpgsqlDbType.Unknown,
        });
        await createCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (!relationship && indexesProvisioned && !indexCatalog.IsEmpty)
        {
            await AgeIndexMaintenance
                .EnsureLabelAsync(connection, transaction, graphName, label, indexCatalog, cancellationToken)
                .ConfigureAwait(false);
            indexPresenceCache.Clear();
        }
    }

    private async Task<bool> LabelExistsAsync(
        string label,
        bool relationship,
        CancellationToken cancellationToken)
    {
        var existsCommand = connection.CreateCommand();
        await using var existsLease = existsCommand.ConfigureAwait(false);
        existsCommand.Transaction = transaction;
        existsCommand.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM ag_catalog.ag_label AS label
                JOIN ag_catalog.ag_graph AS graph ON graph.graphid = label.graph
                WHERE graph.name = @graphName
                  AND label.name = @labelName
                  AND label.kind = @labelKind)
            """;
        existsCommand.Parameters.AddWithValue("graphName", graphName);
        existsCommand.Parameters.AddWithValue("labelName", label);
        existsCommand.Parameters.AddWithValue("labelKind", relationship ? "e" : "v");
        return await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>
    /// Discovers concrete native and externally managed label tables for one entity kind from AGE's
    /// catalog. Provider-reserved universal tables are excluded from root discovery.
    /// </summary>
    internal async Task<List<AgeFullTextSearch.GraphLabelTable>> DiscoverFullTextTablesAsync(
        bool relationship,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT label.name
            FROM ag_catalog.ag_label AS label
            JOIN ag_catalog.ag_graph AS graph ON graph.graphid = label.graph
            WHERE graph.name = @graphName
              AND label.kind = @labelKind
              AND label.name <> @reservedNodeTable
              AND label.name <> @reservedRelationshipTable
            ORDER BY label.name
            """;
        command.Parameters.AddWithValue("graphName", graphName);
        command.Parameters.AddWithValue("labelKind", relationship ? "e" : "v");
        command.Parameters.AddWithValue("reservedNodeTable", SerializationBridge.ComplexNodeLabel);
        command.Parameters.AddWithValue(
            "reservedRelationshipTable",
            SerializationBridge.ComplexRelationshipType);
        await using var commandLease = command.ConfigureAwait(false);

        try
        {
            var tables = new List<AgeFullTextSearch.GraphLabelTable>();
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerLease = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(new AgeFullTextSearch.GraphLabelTable(reader.GetString(0)));
            }

            return tables;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsQueryExecutionFailure(exception))
        {
            throw WrapQueryExecutionFailure(exception);
        }
    }

    /// <summary>Executes one combined phase-one search and reads its private graphid context.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "AgeFullTextSearch composes SQL only from catalog-vetted identifiers, " +
            "compile-time constants, and bound search text.")]
    internal async Task<List<AgeFullTextSearch.FullTextMatch>> QueryFullTextMatchesAsync(
        string sql,
        string query,
        AgeFullTextSearch.FullTextTarget expectedTarget,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("query", query));
        await using var commandLease = command.ConfigureAwait(false);

        try
        {
            var matches = new List<AgeFullTextSearch.FullTextMatch>();
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerLease = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var target = reader.GetString(1) switch
                {
                    "Node" => AgeFullTextSearch.FullTextTarget.Nodes,
                    "Relationship" => AgeFullTextSearch.FullTextTarget.Relationships,
                    var value => throw new GraphException($"AGE full-text search returned unknown entity kind '{value}'."),
                };
                if (target != expectedTarget)
                {
                    throw new GraphException("AGE full-text search returned an entity kind outside its requested scope.");
                }

                matches.Add(new AgeFullTextSearch.FullTextMatch(reader.GetInt64(0), target));
            }

            return matches;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsQueryExecutionFailure(exception))
        {
            throw WrapQueryExecutionFailure(exception);
        }
    }

    /// <summary>
    /// Runs a plain (non-<c>cypher()</c>) SQL statement on this runner's connection and transaction,
    /// optionally binding one text value to a <c>@query</c> parameter, and returns the string values
    /// of the first result column. It executes on the SAME transaction as the caller's Cypher, so it
    /// observes that transaction's uncommitted writes. Phase-one full-text search has its own typed
    /// seam (<see cref="QueryFullTextMatchesAsync"/>); this general one remains for diagnostics and
    /// catalog assertions such as <c>EXPLAIN</c> plans and lock inspection.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The caller composes the statement from validated SQL identifiers (AgeSqlIdentifier) " +
            "and constant text; the only user-controlled value is the search text, which travels as the " +
            "@query bind parameter.")]
    internal async Task<List<string>> QueryScalarStringsAsync(
        string sql,
        string? queryParameter,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        cancellationToken.ThrowIfCancellationRequested();

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (queryParameter is not null)
        {
            command.Parameters.Add(new NpgsqlParameter("query", queryParameter));
        }

        await using var commandLease = command.ConfigureAwait(false);

        try
        {
            var values = new List<string>();
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerLease = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    values.Add(reader.GetString(0));
                }
            }

            return values;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsQueryExecutionFailure(exception))
        {
            throw WrapQueryExecutionFailure(exception);
        }
    }

    /// <summary>
    /// Takes transaction-scoped PostgreSQL advisory locks on <paramref name="lockKeys"/>, blocking
    /// until every key is held. The locks release automatically when this runner's transaction
    /// commits or rolls back, so they span the uniqueness probe and the mutation that follows it
    /// without any explicit unlock path (see <see cref="Entities.AgeUniquenessLockKey"/>).
    /// </summary>
    /// <remarks>
    /// Keys are deduplicated and acquired in ascending order so two transactions that need an
    /// overlapping set can never take them in opposite orders and deadlock. The statements are sent
    /// as one multi-statement command: PostgreSQL runs them sequentially in text order, which keeps
    /// the ordering guarantee at the cost of a single round trip.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The statement text is constant apart from generated parameter placeholders; " +
            "every lock key travels as a bind parameter.")]
    internal async Task AcquireUniquenessLocksAsync(
        IReadOnlyCollection<long> lockKeys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lockKeys);
        if (lockKeys.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var ordered = lockKeys.Distinct().Order().ToArray();
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var statements = new StringBuilder();
        for (var index = 0; index < ordered.Length; index++)
        {
            statements.Append("SELECT pg_advisory_xact_lock(@lock").Append(index).Append(");");
            command.Parameters.Add(new NpgsqlParameter<long>($"lock{index}", ordered[index]));
        }

        command.CommandText = statements.ToString();
        await using var commandLease = command.ConfigureAwait(false);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsQueryExecutionFailure(exception))
        {
            throw WrapQueryExecutionFailure(exception);
        }
    }

    /// <summary>
    /// Locks the selected AGE graph rows until the current transaction completes, without issuing
    /// a property update that could trigger external audit or synchronization behavior.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The graph schema is validated and quoted; the physical table name is a compile-time constant.")]
    internal async Task AcquireElementLocksAsync(
        IReadOnlyCollection<long> nativeIds,
        bool relationship,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nativeIds);
        if (nativeIds.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var orderedIds = nativeIds.Distinct().Order().ToArray();
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildElementLockSql(graphName, relationship);
        command.Parameters.Add(new NpgsqlParameter<long[]>("nativeIds", orderedIds)
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
        });
        await using var commandLease = command.ConfigureAwait(false);

        try
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerLease = reader.ConfigureAwait(false);
            var lockedCount = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lockedCount++;
            }

            if (orderedIds.Length != nativeIds.Count || lockedCount != orderedIds.Length)
            {
                throw new GraphException(
                    "The AGE mutation target set changed before every frozen target could be locked.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsQueryExecutionFailure(exception))
        {
            throw WrapQueryExecutionFailure(exception);
        }
    }

    internal static string BuildElementLockSql(string graphName, bool relationship)
    {
        var table = relationship ? "_ag_label_edge" : "_ag_label_vertex";
        return $"""
            SELECT id
            FROM {AgeSqlIdentifier.Quote(graphName, "graph name")}.{AgeSqlIdentifier.Quote(table, "table name")}
            WHERE id::text::bigint = ANY(@nativeIds)
            ORDER BY id::text::bigint
            FOR UPDATE
            """;
    }

    public Task<AgeResultCursor> RunAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        CancellationToken cancellationToken) =>
        RunAsync(cypher, parameters, projectionColumns, postProcessing: null, plan: null, cancellationToken);

    public async Task<AgeResultCursor> RunAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        CypherPostProcessing? postProcessing,
        CancellationToken cancellationToken) =>
        await RunAsync(cypher, parameters, projectionColumns, postProcessing, plan: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<AgeResultCursor> RunAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        CypherPostProcessing? postProcessing,
        AgeExecutionPlan? plan,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(projectionColumns);
        cancellationToken.ThrowIfCancellationRequested();

        var (command, distinct) = await PrepareCommandAsync(
                cypher,
                parameters,
                projectionColumns,
                postProcessing,
                plan,
                streaming: false,
                cancellationToken)
            .ConfigureAwait(false);
        await using var commandLease = command.ConfigureAwait(false);

        try
        {
            var records = new List<AgeRecord>();
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerLease = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(await ReadRecordAsync(reader, cancellationToken).ConfigureAwait(false));
            }

            if (distinct)
            {
                // The key embeds the value's runtime type so 1 (integer) and "1" (string) stay
                // distinct rows.
                records = records.DistinctBy(GetDistinctKey, StringComparer.Ordinal).ToList();
            }

            logger.LogDebugAgeQueryRunner87(records.Count);
            return new AgeResultCursor(records);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsQueryExecutionFailure(exception))
        {
            throw WrapQueryExecutionFailure(exception);
        }
    }

    /// <summary>
    /// Executes a sequence of AGE Cypher commands through one Npgsql batch execution and buffers
    /// each command's result set in command order. No result is exposed until the whole batch has
    /// crossed the single execution boundary.
    /// </summary>
    internal async Task<IReadOnlyList<AgeBatchResult>> RunBatchAsync(
        IReadOnlyList<AgeBatchCommand> commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            throw new ArgumentException("At least one AGE batch command is required.", nameof(commands));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (batchCommandTransform is not null)
        {
            commands = commands.Select(batchCommandTransform).ToArray();
        }

        foreach (var command in commands)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(command.Cypher);
            ArgumentNullException.ThrowIfNull(command.Parameters);
            ArgumentNullException.ThrowIfNull(command.ProjectionColumns);
        }

        var duplicateName = commands
            .GroupBy(command => command.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateName is not null)
        {
            throw new ArgumentException($"AGE batch command name '{duplicateName}' is duplicated.", nameof(commands));
        }

        using var batch = new NpgsqlBatch(connection, transaction);
        var distinctFlags = new bool[commands.Count];

        for (var commandIndex = 0; commandIndex < commands.Count; commandIndex++)
        {
            var command = commands[commandIndex];
            var (cypher, effectiveColumns, distinct) = NormalizeCommand(command.Cypher, command.ProjectionColumns);
            distinctFlags[commandIndex] = distinct;
            var definition = CreateCommandDefinition(cypher, command.Parameters, effectiveColumns);
            var batchCommand = new NpgsqlBatchCommand(definition.CommandText);
            batchCommand.Parameters.Add(definition.AgtypeParameter);
            batch.BatchCommands.Add(batchCommand);
        }

        try
        {
            batchExecutionObserver?.Invoke();
            var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerLease = reader.ConfigureAwait(false);
            var results = new List<AgeBatchResult>(commands.Count);

            for (var commandIndex = 0; commandIndex < commands.Count; commandIndex++)
            {
                var records = new List<AgeRecord>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    records.Add(await ReadRecordAsync(reader, cancellationToken).ConfigureAwait(false));
                }

                if (distinctFlags[commandIndex])
                {
                    records = records.DistinctBy(GetDistinctKey, StringComparer.Ordinal).ToList();
                }

                results.Add(new AgeBatchResult(commands[commandIndex].Name, records));

                if (commandIndex < commands.Count - 1 &&
                    !await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new GraphException(
                        $"AGE batch ended after {commandIndex + 1} result sets; {commands.Count} were expected.");
                }
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsQueryExecutionFailure(exception))
        {
            throw WrapQueryExecutionFailure(exception);
        }
    }

    public Task<AgeResultCursor> RunStreamingAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        CancellationToken cancellationToken) =>
        RunStreamingAsync(cypher, parameters, projectionColumns, postProcessing: null, plan: null, cancellationToken);

    public async Task<AgeResultCursor> RunStreamingAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        CypherPostProcessing? postProcessing,
        CancellationToken cancellationToken) =>
        await RunStreamingAsync(cypher, parameters, projectionColumns, postProcessing, plan: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<AgeResultCursor> RunStreamingAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        CypherPostProcessing? postProcessing,
        AgeExecutionPlan? plan,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(projectionColumns);
        cancellationToken.ThrowIfCancellationRequested();

        var (command, distinct) = await PrepareCommandAsync(
                cypher,
                parameters,
                projectionColumns,
                postProcessing,
                plan,
                streaming: true,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return new AgeResultCursor(
                new AgeReaderRecordSource(command, reader, WrapQueryExecutionFailure),
                distinct);
        }
        catch (OperationCanceledException)
        {
            await command.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (IsQueryExecutionFailure(exception))
        {
            await command.DisposeAsync().ConfigureAwait(false);
            throw WrapQueryExecutionFailure(exception);
        }
        catch
        {
            await command.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(NpgsqlCommand Command, bool Distinct)> PrepareCommandAsync(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        CypherPostProcessing? postProcessing,
        AgeExecutionPlan? plan,
        bool streaming,
        CancellationToken cancellationToken)
    {
        var executionCypher = cypher;
        var effectiveParameters = parameters;
        var effectivePostProcessing = postProcessing;
        var effectivePlan = plan;
        if (plan is AgePageSelectionPlan pagePlan)
        {
            if (await HasTablesAsync(pagePlan.Labels, cancellationToken).ConfigureAwait(false))
            {
                var pageIds = await ReadPageIdsAsync(pagePlan, cancellationToken).ConfigureAwait(false);
                executionCypher = InjectPageIds(cypher, pagePlan.Alias);
                effectiveParameters = new Dictionary<string, object?>(parameters, StringComparer.Ordinal)
                {
                    [PageIdsParameterName] = pageIds,
                };
                if (pagePlan.ClauseLevelPaging)
                {
                    // The id predicate already selects the page, so the original SKIP/LIMIT would
                    // page the page a second time. ORDER BY stays so the selected page is returned
                    // in the requested order.
                    executionCypher = RemoveClauseLevelPaging(executionCypher);
                }

                effectivePostProcessing = postProcessing is null
                    ? null
                    : postProcessing with { Skip = null, Limit = null };
            }
            else
            {
                // A missing physical label is a valid empty result on the AGE path; a direct
                // SQL plan must not turn it into a catalog error.
                effectivePlan = null;
            }
        }

        if (plan is AgeSqlPlan sqlPlan)
        {
            var available = sqlPlan.Kind == "containment"
                ? await HasTablesAsync(sqlPlan.RequiredTableNames, cancellationToken).ConfigureAwait(false)
                : await HasIndexStructuresAsync(sqlPlan.RequiredTableNames, cancellationToken).ConfigureAwait(false);
            if (!available)
            {
                effectivePlan = null;
            }
        }

        var (normalizedCypher, effectiveColumns, distinct) = NormalizeCommand(executionCypher, projectionColumns);
        cypher = normalizedCypher;
        var command = CreateCommand(cypher, effectiveParameters, effectiveColumns, effectivePostProcessing, effectivePlan);
        if (streaming)
        {
            logger.LogDebugAgeQueryRunner159(effectiveParameters.Count, effectiveColumns.Count, cypher);
        }
        else
        {
            logger.LogDebugAgeQueryRunner167(effectiveParameters.Count, effectiveColumns.Count, cypher);
        }
        return (command, distinct);
    }

    /// <summary>
    /// Reads the final page ids for a complex-property paging plan with one indexed query over the
    /// label tables. When every label has a declared equality companion for the sort property, the
    /// query orders by the companion text column and its B-tree instead of the unindexed AGE
    /// accessor; otherwise it keeps the accessor ordering. The query reads at most one branch limit
    /// per label instead of loading every candidate complex property first.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "SQL is composed from provider-quoted identifiers and parameter placeholders; all values are bound parameters.")]
    private async Task<List<long>> ReadPageIdsAsync(
        AgePageSelectionPlan plan,
        CancellationToken cancellationToken)
    {
        var branchLimit = plan.Skip + plan.Limit;
        var sortAlias = AgeIndexTable.QuoteIdentifier("__page_sort", "column alias");
        var direction = plan.Descending ? "DESC" : "ASC";
        var sortExpression =
            "ag_catalog.agtype_access_operator(VARIADIC ARRAY[properties, " +
            AgeIndexTable.AgtypeStringLiteral(plan.SortProperty) + "])";
        var union = new StringBuilder();
        var labels = plan.Labels.Distinct(StringComparer.Ordinal).ToArray();
        var sources = plan.Sources
            .Where(source => labels.Contains(source.Label, StringComparer.Ordinal))
            .GroupBy(source => source.Label, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var useCompanion = sources.Count == labels.Length &&
            await PageSourcesAvailableAsync(sources.Values.ToArray(), cancellationToken).ConfigureAwait(false);
        var indexAlias = AgeIndexTable.QuoteIdentifier("__page_index", "table alias");
        var idColumn = AgeIndexTable.QuoteIdentifier(AgeIndexTable.IdColumn, "column name");
        for (var index = 0; index < labels.Length; index++)
        {
            if (index > 0)
            {
                union.AppendLine().AppendLine("UNION ALL");
            }

            var label = labels[index];
            union.Append("(SELECT ");
            if (useCompanion)
            {
                // The companion table already carries the vertex id next to the sort column, so
                // read it directly. Joining the label table would force a full label scan and
                // turn an index-ordered LIMIT into a sort of every candidate row.
                var source = sources[label];
                union.Append(indexAlias).Append('.').Append(idColumn)
                    .Append(", ").Append(indexAlias).Append('.')
                    .Append(AgeIndexTable.QuoteIdentifier(source.ColumnName, "column name"))
                    .Append(" AS ").Append(sortAlias)
                    .Append(" FROM ")
                    .Append(AgeIndexTable.QualifiedTable(graphName, label, source.TableName))
                    .Append(" AS ").Append(indexAlias);
            }
            else
            {
                union.Append(idColumn).Append(", ").Append(sortExpression)
                    .Append(" AS ").Append(sortAlias)
                    .Append(" FROM ").Append(AgeIndexTable.QualifiedLabelTable(graphName, label));
            }

            union.Append(" ORDER BY ").Append(sortAlias).Append(' ').Append(direction)
                .Append(" LIMIT ").Append(branchLimit.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(')');
        }

        var sql = new StringBuilder()
            .Append("SELECT ").Append(idColumn).Append("::text::bigint FROM (")
            .AppendLine()
            .AppendLine(union.ToString())
            .Append(") AS ").Append(AgeIndexTable.QuoteIdentifier("__page", "table alias"))
            .AppendLine()
            .Append("ORDER BY ").Append(sortAlias).Append(' ').Append(direction)
            .Append(" OFFSET ").Append(plan.Skip.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(" LIMIT ").Append(plan.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToString();

        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = sql;
        var ids = new List<long>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerLease = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>
    /// Probes that every companion table and text column the paging plan wants to read exists. A
    /// missing or stale companion keeps the correct accessor ordering instead of turning the page
    /// query into a missing-relation or missing-column error.
    /// </summary>
    private async Task<bool> PageSourcesAvailableAsync(
        AgePageSource[] sources,
        CancellationToken cancellationToken)
    {
        if (sources.Length == 0)
        {
            return false;
        }

        var tables = sources
            .Select(source => source.TableName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = @graphName
              AND table_name = ANY(@tables)
            """;
        command.Parameters.AddWithValue("graphName", graphName);
        command.Parameters.Add(new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = tables,
        });
        var present = new HashSet<(string Table, string Column)>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerLease = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            present.Add((reader.GetString(0), reader.GetString(1)));
        }

        return sources.All(source => present.Contains((source.TableName, source.ColumnName)));
    }

    private const string PageIdsParameterName = "__cvoya_page_ids";
    private const string PageIdVariable = "__cvoya_page_id";

    /// <summary>
    /// Rewrites every root match of a page plan to join the exact final-page id list. The ids are
    /// passed as one agtype list parameter and unwound, so each root match is an id equality lookup.
    /// </summary>
    /// <remarks>
    /// A literal <c>IN [id, ...]</c> list is not portable: AGE 1.8 returns agtype from <c>id()</c>
    /// and rejects an untyped numeric list with <c>XX000: not a common type</c>, while a cast list
    /// still fails or crashes for more than one element. An OR chain of equalities is portable but
    /// makes the planner scan the label table (a 30k L19 page regresses from ~16 ms to ~120 ms).
    /// Unwinding a parameter list keeps the native id equality and works on both AGE versions.
    /// </remarks>
    internal static string InjectPageIds(string cypher, string alias)
    {
        var unwind = $"UNWIND ${PageIdsParameterName} AS {PageIdVariable}";
        var predicate = $"id({alias}) = {PageIdVariable}";
        var matchPrefix = "MATCH (" + alias + ":";
        var builder = new StringBuilder(cypher.Length + (unwind.Length + predicate.Length) * 2);
        foreach (var line in cypher.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            // Match the rendered root MATCH by its alias prefix so an escaped or non-ASCII label
            // (which is still a valid root pattern) receives the page ids as well.
            if (trimmed.StartsWith(matchPrefix, StringComparison.Ordinal) &&
                trimmed.EndsWith(')'))
            {
                builder.AppendLine(unwind);
                builder.AppendLine(line);
                builder.Append("WHERE ").Append(predicate).AppendLine();
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Removes the trailing <c>SKIP</c>/<c>LIMIT</c> clauses of a single-label query after the page
    /// id predicate has already selected the page. The trailing <c>ORDER BY</c> is kept so the
    /// injected page is returned in the requested order.
    /// </summary>
    private static string RemoveClauseLevelPaging(string cypher)
    {
        var lines = cypher.Split('\n').ToList();
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("SKIP ", StringComparison.Ordinal) ||
                trimmed.StartsWith("LIMIT ", StringComparison.Ordinal))
            {
                lines.RemoveAt(index);
                continue;
            }

            break;
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Normalizes one Cypher command the same way for every execution path: infers projection
    /// columns when none are supplied, strips <c>RETURN DISTINCT</c> (callers dedupe client-side
    /// using the returned flag), and rewrites non-identifier projection aliases.
    /// </summary>
    private static (string Cypher, IReadOnlyList<string> Columns, bool Distinct) NormalizeCommand(
        string cypher,
        IReadOnlyList<string> projectionColumns)
    {
        if (projectionColumns.Count == 0)
        {
            projectionColumns = InferProjectionColumns(cypher);
        }

        var distinct = ReturnDistinctRegex().IsMatch(cypher);
        cypher = ReturnDistinctRegex().Replace(cypher, "RETURN");
        var normalizedProjection = NormalizeProjectionAliases(cypher, projectionColumns);
        var effectiveColumns = normalizedProjection.Columns.Count == 0 ? ["result"] : normalizedProjection.Columns;
        return (normalizedProjection.Cypher, effectiveColumns, distinct);
    }

    /// <summary>
    /// True for the exceptions the reader/command execution and <see cref="ParseTextAgtype"/> raise
    /// on a broken connection, a malformed server response, or an unexpected column shape - the
    /// failures this correlation-id boundary is meant to catch. Anything else (e.g. a bug
    /// elsewhere) should propagate rather than be silently wrapped.
    /// </summary>
    internal static bool IsQueryExecutionFailure(Exception exception) =>
        exception is NpgsqlException or JsonException or FormatException or InvalidCastException;

    internal static string GetDistinctKey(AgeRecord record) => string.Join(
        '\u001f',
        record.Values.Select(pair => $"{pair.Key}\u001e{pair.Value?.GetType().Name}\u001e{pair.Value}"));

    private GraphException WrapQueryExecutionFailure(Exception exception)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        logger.LogErrorAgeQueryRunner192(exception, correlationId);
        return new GraphException($"AGE query execution failed. Correlation ID: {correlationId}.", exception);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "graphName and projection columns are validated by AgeSqlIdentifier, the dollar-quote tag is collision-proof, and values travel in the agtype parameter.")]
    private NpgsqlCommand CreateCommand(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        CypherPostProcessing? postProcessing = null,
        AgeExecutionPlan? plan = null)
    {
        var definition = CreateCommandDefinition(cypher, parameters, projectionColumns, postProcessing, plan);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = definition.CommandText;
        command.Parameters.Add(definition.AgtypeParameter);
        return command;
    }

    private (string CommandText, NpgsqlParameter AgtypeParameter) CreateCommandDefinition(
        string cypher,
        IReadOnlyDictionary<string, object?> parameters,
        IReadOnlyList<string> projectionColumns,
        CypherPostProcessing? postProcessing = null,
        AgeExecutionPlan? plan = null)
    {
        var serializedParameters = JsonSerializer.Serialize(
            parameters.ToDictionary(pair => pair.Key, pair => NormalizeParameter(pair.Value), StringComparer.Ordinal));
        var commandText = BuildCommandText(
            graphName,
            cypher,
            projectionColumns,
            postProcessing,
            parameterPlaceholder: "@agtypeParams",
            plan);
        var parameter = new NpgsqlParameter
        {
            ParameterName = "agtypeParams",
            Value = new Agtype(serializedParameters),
            DataTypeName = "ag_catalog.agtype",
        };
        return (commandText, parameter);
    }

    /// <summary>
    /// Gates a plan or write on the catalog snapshot this store actually provisioned. A declaration
    /// added after maintenance must not make an existing table look complete.
    /// </summary>
    private async Task<bool> HasIndexStructuresAsync(
        IReadOnlyList<string> tableNames,
        CancellationToken cancellationToken)
    {
        if (indexCatalog.Version != provisionedCatalogVersion)
        {
            return false;
        }

        return await HasTablesAsync(tableNames, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Probes whether every required physical relation exists.</summary>
    private async Task<bool> HasTablesAsync(
        IReadOnlyList<string> tableNames,
        CancellationToken cancellationToken)
    {
        if (tableNames.Count == 0)
        {
            return false;
        }

        var distinct = tableNames.Distinct(StringComparer.Ordinal).ToArray();
        if (indexPresenceCache.TryGet(graphName, indexCatalog.Version, distinct, out var cached))
        {
            return cached;
        }

        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = @graphName
              AND table_name = ANY(@tables)
            """;
        command.Parameters.AddWithValue("graphName", graphName);
        command.Parameters.Add(new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = distinct,
        });
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var present = count is long value && value == distinct.Length;
        indexPresenceCache.Set(graphName, indexCatalog.Version, distinct, present);
        return present;
    }

    /// <summary>
    /// Executes one companion-table write and turns the documented atomic drop window into
    /// actionable retry guidance. Only the tables this label actually targets are matched.
    /// </summary>
    private async Task ExecuteIndexWriteAsync(
        NpgsqlCommand command,
        string label,
        string tableName,
        CancellationToken cancellationToken)
    {
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (IsMissingDeclaredStructure(exception, label, tableName))
        {
            throw new GraphException(
                $"The declared index structures for label '{label}' were dropped by another instance " +
                "within the presence-cache TTL. Retry the write, or run EnsureIndexesAsync to recreate them.",
                exception);
        }
    }

    /// <summary>
    /// Matches a 42P01 error against the companion table this label writes, plus its aggregate
    /// tables because the insert trigger touches them in the same statement.
    /// </summary>
    private bool IsMissingDeclaredStructure(
        PostgresException exception,
        string label,
        string tableName)
    {
        if (!string.Equals(exception.SqlState, PostgresErrorCodes.UndefinedTable, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(exception.SchemaName) &&
            !string.Equals(exception.SchemaName, graphName, StringComparison.Ordinal))
        {
            return false;
        }

        var missing = exception.TableName;
        if (string.IsNullOrEmpty(missing))
        {
            var match = Regex.Match(
                exception.MessageText,
                "relation \"(?<name>[^\"]+)\" does not exist",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return false;
            }

            missing = match.Groups["name"].Value;
        }

        var separator = missing.LastIndexOf('.');
        if (separator >= 0)
        {
            missing = missing[(separator + 1)..];
        }

        missing = missing.Trim('"');
        if (string.Equals(missing, tableName, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var aggregate in indexCatalog.GetAggregates(label))
        {
            if (string.Equals(missing, aggregate.TableName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Writes every declared index column for a newly created node.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "SQL is composed from provider-quoted identifiers and parameter placeholders; all values are bound parameters.")]
    internal async Task WriteIndexAsync(
        string label,
        long graphId,
        IReadOnlyDictionary<string, object?> properties,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(properties);

        var textColumns = indexCatalog.GetProperties(label);
        var valueColumns = indexCatalog.GetValueColumns(label);
        if (textColumns.Count == 0 && valueColumns.Count == 0)
        {
            return;
        }

        var tableName = indexCatalog.GetTableName(label);
        if (string.IsNullOrEmpty(tableName) ||
            !await HasIndexStructuresAsync([tableName], cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var columns = new List<string> { AgeIndexTable.QuoteIdentifier(AgeIndexTable.IdColumn, "column name") };
        var values = new List<string> { "@id::text::ag_catalog.graphid" };
        var updates = new List<string>();
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        var parameterIndex = 0;
        foreach (var column in textColumns)
        {
            var name = AgeIndexTable.QuoteIdentifier(column.ColumnName, "column name");
            columns.Add(name);
            var parameter = "p" + parameterIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameterIndex++;
            values.Add("@" + parameter);
            updates.Add($"{name} = EXCLUDED.{name}");
            command.Parameters.AddWithValue(
                parameter,
                properties.TryGetValue(column.Property, out var propertyValue) && propertyValue is not null
                    ? FormatIndexText(propertyValue)
                    : DBNull.Value);
        }

        foreach (var column in valueColumns)
        {
            var name = AgeIndexTable.QuoteIdentifier(column.ColumnName, "column name");
            columns.Add(name);
            var parameter = "p" + parameterIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameterIndex++;
            values.Add("@" + parameter);
            updates.Add($"{name} = EXCLUDED.{name}");
            command.Parameters.AddWithValue(
                parameter,
                properties.TryGetValue(column.Property, out var propertyValue)
                    ? NormalizeNumeric(propertyValue)
                    : DBNull.Value);
        }

        command.Parameters.AddWithValue("id", graphId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.CommandText =
            $"INSERT INTO {AgeIndexTable.QualifiedTable(graphName, label, tableName)} " +
            $"({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)}) " +
            $"ON CONFLICT ({AgeIndexTable.QuoteIdentifier(AgeIndexTable.IdColumn, "column name")}) DO UPDATE SET " +
            string.Join(", ", updates);
        await ExecuteIndexWriteAsync(command, label, tableName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Refreshes every declared index column for nodes changed by an update.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "SQL is composed from provider-quoted identifiers and parameter placeholders; all values are bound parameters.")]
    internal async Task SyncIndexAsync(
        IReadOnlyList<long> graphIds,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken)
    {
        if (graphIds.Count == 0 || labels.Count == 0)
        {
            return;
        }

        foreach (var label in labels)
        {
            var textColumns = indexCatalog.GetProperties(label);
            var valueColumns = indexCatalog.GetValueColumns(label);
            if (textColumns.Count == 0 && valueColumns.Count == 0)
            {
                continue;
            }

            var tableName = indexCatalog.GetTableName(label);
            if (string.IsNullOrEmpty(tableName) ||
                !await HasIndexStructuresAsync([tableName], cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var columns = new List<string> { AgeIndexTable.QuoteIdentifier(AgeIndexTable.IdColumn, "column name") };
            var values = new List<string> { "id" };
            var updates = new List<string>();
            foreach (var column in textColumns)
            {
                var name = AgeIndexTable.QuoteIdentifier(column.ColumnName, "column name");
                columns.Add(name);
                values.Add(AgeIndexTable.TextValueSql(column.Property));
                updates.Add($"{name} = EXCLUDED.{name}");
            }

            foreach (var column in valueColumns)
            {
                var name = AgeIndexTable.QuoteIdentifier(column.ColumnName, "column name");
                columns.Add(name);
                values.Add(AgeIndexTable.NumericValueSql(column.Property));
                updates.Add($"{name} = EXCLUDED.{name}");
            }

            var command = connection.CreateCommand();
            await using var commandLease = command.ConfigureAwait(false);
            command.Transaction = transaction;
            command.CommandText =
                $"INSERT INTO {AgeIndexTable.QualifiedTable(graphName, label, tableName)} " +
                $"({string.Join(", ", columns)}) " +
                $"SELECT {string.Join(", ", values)} FROM {AgeIndexTable.QualifiedLabelTable(graphName, label)} " +
                "WHERE id::text::bigint = ANY(@ids) " +
                $"ON CONFLICT ({AgeIndexTable.QuoteIdentifier(AgeIndexTable.IdColumn, "column name")}) " +
                "DO UPDATE SET " + string.Join(", ", updates);
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = graphIds.ToArray(),
            });
            await ExecuteIndexWriteAsync(command, label, tableName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Removes every declared index row for deleted nodes.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "SQL is composed from provider-quoted identifiers and parameter placeholders; all values are bound parameters.")]
    internal async Task DeleteIndexAsync(
        IReadOnlyList<long> graphIds,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken)
    {
        if (graphIds.Count == 0 || labels.Count == 0)
        {
            return;
        }

        foreach (var label in labels)
        {
            var tableName = indexCatalog.GetTableName(label);
            if (string.IsNullOrEmpty(tableName) ||
                !await HasIndexStructuresAsync([tableName], cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var command = connection.CreateCommand();
            await using var commandLease = command.ConfigureAwait(false);
            command.Transaction = transaction;
            command.CommandText =
                $"DELETE FROM {AgeIndexTable.QualifiedTable(graphName, label, tableName)} " +
                "WHERE id::text::bigint = ANY(@ids)";
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = graphIds.ToArray(),
            });
            await ExecuteIndexWriteAsync(command, label, tableName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Renders a create-path value the same way the backfill's <c>jsonb ->></c> expression does, so
    /// a row's companion text stays stable across create, update, and re-ensure.
    /// </summary>
    private static object FormatIndexText(object value)
    {
        var json = JsonSerializer.Serialize(value, value.GetType());
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.String => document.RootElement.GetString() ?? string.Empty,
            JsonValueKind.Null => DBNull.Value,
            _ => document.RootElement.GetRawText(),
        };
    }

    private static object NormalizeNumeric(object? value) => value switch
    {
        null => DBNull.Value,
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal or float or double => value,
        _ => DBNull.Value,
    };

    /// <summary>
    /// Builds the SQL that executes one lowered Cypher statement and applies the row shaping the
    /// AGE Cypher grammar cannot express after a union.
    /// </summary>
    /// <remarks>
    /// Exposed to the test assembly so the query-shape tests can assert the exact statement sent to
    /// PostgreSQL without opening a connection.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "graphName, columns, and generated sort columns are validated identifiers; paging values are integers produced by the planner.")]
    internal static string BuildCommandText(
        string graphName,
        string cypher,
        IReadOnlyList<string> projectionColumns,
        CypherPostProcessing? postProcessing,
        string parameterPlaceholder = "@agtypeParams",
        AgeExecutionPlan? plan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphName);
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        ArgumentNullException.ThrowIfNull(projectionColumns);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterPlaceholder);

        if (plan is AgeSqlPlan sqlPlan)
        {
            var planGraphName = AgeSqlIdentifier.Validate(graphName, "graph name");
            return sqlPlan.Sql
                .Replace(
                    AgeIndexTable.GraphToken,
                    AgeSqlIdentifier.Quote(planGraphName, "graph name"),
                    StringComparison.Ordinal)
                .Replace(AgeIndexTable.ParameterToken, parameterPlaceholder, StringComparison.Ordinal);
        }

        if (projectionColumns.Count == 0)
        {
            throw new GraphException("AGE query execution requires at least one projection column.");
        }

        if (postProcessing is not null &&
            (postProcessing.ResultColumnCount < 0 ||
             postProcessing.ResultColumnCount > projectionColumns.Count))
        {
            throw new GraphException(
                "AGE post-processing result column count is outside the rendered projection.");
        }

        if (postProcessing is not null &&
            (postProcessing.Sort.Any(sort =>
                sort.ColumnIndex < 0 || sort.ColumnIndex >= projectionColumns.Count) ||
             postProcessing.Aggregation is { } aggregatePlan &&
             (aggregatePlan.GroupColumnIndexes.Any(index =>
                  index < 0 || index >= postProcessing.ResultColumnCount) ||
              aggregatePlan.Aggregates.Any(aggregate =>
                  aggregate.ColumnIndex < 0 ||
                  aggregate.ColumnIndex >= postProcessing.ResultColumnCount ||
                  (aggregate.AuxiliaryColumnIndex is { } auxiliary &&
                   (auxiliary < 0 || auxiliary >= projectionColumns.Count))))))
        {
            throw new GraphException(
                "AGE post-processing column index is outside the rendered projection.");
        }

        var quoteTag = ChooseDollarQuoteTag(cypher);
        var validatedGraphName = AgeSqlIdentifier.Validate(graphName, "graph name");
        var columnDefinitions = string.Join(", ", projectionColumns.Select(column =>
            $"{AgeSqlIdentifier.Quote(column, "projection column")} agtype"));
        var source =
            $"FROM ag_catalog.cypher('{validatedGraphName}', " +
            $"{quoteTag}{cypher}{quoteTag}, {parameterPlaceholder}) AS age_result ({columnDefinitions})";

        if (postProcessing is null)
        {
            var allColumns = string.Join(", ", projectionColumns.Select(column =>
                $"age_result.{AgeSqlIdentifier.Quote(column, "projection column")}"));
            return $"SELECT {allColumns} {source}";
        }

        var resultCount = postProcessing.ResultColumnCount;
        var selectList = new List<string>(resultCount);
        if (postProcessing.Aggregation is { } aggregation)
        {
            for (var index = 0; index < resultCount; index++)
            {
                var quoted = AgeSqlIdentifier.Quote(projectionColumns[index], "projection column");
                var combine = aggregation.Aggregates.FirstOrDefault(aggregate =>
                    aggregate.ColumnIndex == index);
                selectList.Add(combine is null
                    ? $"age_result.{quoted}"
                    : $"{RenderAggregateCombine(combine, quoted, projectionColumns)} AS {quoted}");
            }
        }
        else
        {
            for (var index = 0; index < resultCount; index++)
            {
                selectList.Add(
                    $"age_result.{AgeSqlIdentifier.Quote(projectionColumns[index], "projection column")}");
            }
        }

        var commandText = postProcessing.Distinct
            ? $"SELECT DISTINCT {string.Join(", ", selectList)} {source}"
            : $"SELECT {string.Join(", ", selectList)} {source}";

        if (postProcessing.Aggregation is { } grouping)
        {
            if (grouping.GroupColumnIndexes.Count > 0)
            {
                commandText += " GROUP BY " + string.Join(", ", grouping.GroupColumnIndexes.Select(index =>
                    $"age_result.{AgeSqlIdentifier.Quote(projectionColumns[index], "projection column")}"));
            }
        }
        else if (postProcessing.Sort.Count > 0)
        {
            commandText += " ORDER BY " + string.Join(", ", postProcessing.Sort.Select(sort =>
            {
                var quoted = AgeSqlIdentifier.Quote(projectionColumns[sort.ColumnIndex], "projection column");
                return $"age_result.{quoted}{(sort.Descending ? " DESC" : " ASC")}";
            }));
        }

        if (postProcessing.Skip is { } skip)
        {
            commandText += $" OFFSET {skip}";
        }

        if (postProcessing.Limit is { } limit)
        {
            commandText += $" LIMIT {limit}";
        }

        return commandText;
    }

    /// <summary>
    /// Builds the SQL expression that merges one partial aggregate column across union branches.
    /// </summary>
    /// <remarks>
    /// The merge is type-aware where AGE's agtype comparison is required:
    /// <list type="bullet">
    /// <item><description><c>sum</c>/<c>count</c> are numeric and add.</description></item>
    /// <item><description><c>avg</c> combines a partial sum with a partial non-null count.</description></item>
    /// <item><description><c>min</c>/<c>max</c> use the AGE agtype comparison aggregates, so numbers,
    /// strings, booleans, and temporals keep their agtype ordering.</description></item>
    /// </list>
    /// </remarks>
    private static string RenderAggregateCombine(
        CypherPostAggregate aggregate,
        string quotedColumn,
        IReadOnlyList<string> projectionColumns)
    {
        switch (aggregate.Function.ToLowerInvariant())
        {
            case "sum":
                return $"sum((age_result.{quotedColumn})::text::numeric)::text::ag_catalog.agtype";

            case "avg":
                if (aggregate.AuxiliaryColumnIndex is not { } countIndex ||
                    countIndex < 0 || countIndex >= projectionColumns.Count)
                {
                    throw new GraphException(
                        "AGE average post-processing requires a partial-count column.");
                }

                var quotedCount = AgeSqlIdentifier.Quote(
                    projectionColumns[countIndex],
                    "projection column");
                return "(" +
                    $"sum((age_result.{quotedColumn})::text::numeric) / " +
                    $"NULLIF(sum((age_result.{quotedCount})::text::numeric), 0)" +
                    ")::text::ag_catalog.agtype";

            case "min":
                return $"ag_catalog.age_min(age_result.{quotedColumn})";

            case "max":
                return $"ag_catalog.age_max(age_result.{quotedColumn})";

            default:
                throw new GraphException(
                    $"AGE post-processing does not support aggregate merge '{aggregate.Function}'.");
        }
    }

    /// <summary>Normalizes a command for query-shape tests.</summary>
    internal static (string Cypher, IReadOnlyList<string> Columns, bool Distinct) NormalizeCommandForTesting(
        string cypher,
        IReadOnlyList<string> projectionColumns) =>
        NormalizeCommand(cypher, projectionColumns);

    internal static async Task<AgeRecord> ReadRecordAsync(
        DbDataReader reader,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            // Agtype.ToString() is the raw annotated agtype text (the same wire format the previous
            // ::text cast produced for containers), so ParseTextAgtype sees every value kind,
            // including bare vertices/edges the text cast could not serialize.
            values.Add(
                reader.GetName(index),
                await reader.IsDBNullAsync(index, cancellationToken).ConfigureAwait(false)
                    ? null
                    : ParseTextAgtype(
                        (await reader.GetFieldValueAsync<Agtype>(index, cancellationToken).ConfigureAwait(false))
                            .ToString()));
        }

        return new AgeRecord(values);
    }

    internal static (string Cypher, IReadOnlyList<string> Columns) NormalizeProjectionAliases(
        string cypher,
        IReadOnlyList<string> projectionColumns)
    {
        if (projectionColumns.Count == 0 || projectionColumns.All(IsSqlIdentifier))
        {
            return (cypher, projectionColumns);
        }

        var matches = ReturnRegex().Matches(MaskQuotedContent(cypher));
        if (matches.Count == 0)
        {
            return (cypher, projectionColumns);
        }

        var match = matches[^1];
        var returnBody = match.Groups[1];
        var capturedBody = cypher.Substring(returnBody.Index, returnBody.Length);
        var items = SplitTopLevel(capturedBody).Select(item => item.Trim()).ToArray();
        if (items.Length != projectionColumns.Count)
        {
            throw new GraphException("AGE projection metadata does not match the rendered RETURN shape.");
        }

        var normalizedColumns = projectionColumns.ToArray();
        for (var index = 0; index < normalizedColumns.Length; index++)
        {
            if (IsSqlIdentifier(normalizedColumns[index]))
            {
                continue;
            }

            normalizedColumns[index] = $"age_column_{index}";
            items[index] = $"{items[index].Trim()} AS {normalizedColumns[index]}";
        }

        // Preserve the captured body's trailing whitespace: the RETURN body runs up to the word
        // boundary before ORDER BY / SKIP / LIMIT, so it includes the separating whitespace. Rebuilt
        // items are trimmed, and dropping that whitespace would fuse the last alias into the
        // following clause keyword (for example "age_column_0ORDER BY").
        var trailingWhitespace = capturedBody[capturedBody.TrimEnd().Length..];
        var replacement = string.Join(", ", items) + trailingWhitespace;
        cypher = $"{cypher[..returnBody.Index]}{replacement}{cypher[(returnBody.Index + returnBody.Length)..]}";
        return (cypher, normalizedColumns);
    }

    private static bool IsSqlIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || (!char.IsAsciiLetter(value[0]) && value[0] != '_')) return false;
        return value.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    internal static string ChooseDollarQuoteTag(string cypher)
    {
        for (var suffix = 0; ; suffix++)
        {
            var candidate = $"$cvoya_age_{suffix}$";
            if (!cypher.Contains(candidate, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
    }

    private static object? NormalizeParameter(object? value)
    {
        return value switch
        {
            null => null,
            DateTime dateTime => dateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            DateOnly date => date.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan duration => duration.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            Point point => new Dictionary<string, object?>
            {
                [nameof(Point.Latitude)] = point.Latitude,
                [nameof(Point.Longitude)] = point.Longitude,
                [nameof(Point.Height)] = point.Height,
            },
            IDictionary dictionary => NormalizeDictionary(dictionary),
            IEnumerable sequence when value is not string and not byte[] =>
                sequence.Cast<object?>().Select(NormalizeParameter).ToArray(),
            _ => value,
        };
    }

    internal static object? ParseTextAgtype(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('"'))
        {
            return JsonSerializer.Deserialize<string>(trimmed);
        }

        if ((trimmed.StartsWith('{') || trimmed.StartsWith('[')) &&
            !trimmed.Contains("::vertex", StringComparison.Ordinal) &&
            !trimmed.Contains("::edge", StringComparison.Ordinal) &&
            !trimmed.Contains("::path", StringComparison.Ordinal))
        {
            return JsonDocument.Parse(trimmed).RootElement.Clone();
        }

        if (trimmed.StartsWith('{') ||
            trimmed.StartsWith('[') ||
            trimmed.EndsWith("::numeric", StringComparison.Ordinal) ||
            trimmed is "true" or "false" or "null" or "NaN" or "Infinity" or "-Infinity" ||
            decimal.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return new Agtype(trimmed);
        }

        return trimmed;
    }

    private static Dictionary<string, object?> NormalizeDictionary(IDictionary dictionary)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var enumerator = dictionary.GetEnumerator();
        while (enumerator.MoveNext())
        {
            result.Add(
                Convert.ToString(enumerator.Key, System.Globalization.CultureInfo.InvariantCulture)!,
                NormalizeParameter(enumerator.Value));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, object?> ToParameterDictionary(object? parameters)
    {
        if (parameters is null)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        if (parameters is IReadOnlyDictionary<string, object?> readOnly)
        {
            return readOnly;
        }

        if (parameters is IDictionary<string, object?> dictionary)
        {
            return new Dictionary<string, object?>(dictionary, StringComparer.Ordinal);
        }

        return parameters.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(property => property.Name, property => property.GetValue(parameters), StringComparer.Ordinal);
    }

    internal static string[] InferProjectionColumns(string cypher)
    {
        var matches = ReturnRegex().Matches(MaskQuotedContent(cypher));
        if (matches.Count == 0)
        {
            return [];
        }

        var returnBody = matches[^1].Groups[1];
        var returnText = cypher.Substring(returnBody.Index, returnBody.Length);
        return SplitTopLevel(returnText).Select(item =>
        {
            var alias = AliasRegex().Match(MaskQuotedContent(item));
            if (alias.Success)
            {
                var aliasGroup = alias.Groups[1];
                return UnescapeIdentifier(item.Substring(aliasGroup.Index, aliasGroup.Length));
            }

            var expression = item.Trim();
            var dot = MaskQuotedContent(expression).LastIndexOf('.');
            return UnescapeIdentifier(dot >= 0 ? expression[(dot + 1)..] : expression);
        }).ToArray();
    }

    private static IEnumerable<string> SplitTopLevel(string value)
    {
        var masked = MaskQuotedContent(value);
        var start = 0;
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            depth += masked[index] switch
            {
                '(' or '[' or '{' => 1,
                ')' or ']' or '}' => -1,
                _ => 0,
            };
            if (masked[index] == ',' && depth == 0)
            {
                yield return value[start..index];
                start = index + 1;
            }
        }

        yield return value[start..];
    }

    /// <summary>
    /// Produces a length-preserving copy whose quoted contents cannot be mistaken for RETURN
    /// boundaries or projection punctuation. Backtick identifiers retain their delimiters so alias
    /// recognition still works; doubled backticks and backslash-escaped string characters stay
    /// inside their quoted value rather than toggling the quoted state.
    /// </summary>
    internal static string MaskQuotedContent(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var masked = value.ToCharArray();
        var delimiter = '\0';
        for (var index = 0; index < masked.Length; index++)
        {
            if (delimiter == '\0')
            {
                if (masked[index] is '`' or '\'' or '"')
                {
                    delimiter = masked[index];
                }

                continue;
            }

            if (delimiter == '`' && masked[index] == '`')
            {
                if (index + 1 < masked.Length && masked[index + 1] == '`')
                {
                    masked[index] = '_';
                    masked[++index] = '_';
                }
                else
                {
                    delimiter = '\0';
                }

                continue;
            }

            if (delimiter != '`' && masked[index] == '\\' && index + 1 < masked.Length)
            {
                masked[index] = '_';
                masked[++index] = '_';
                continue;
            }

            if (masked[index] == delimiter)
            {
                delimiter = '\0';
                continue;
            }

            masked[index] = '_';
        }

        return new string(masked);
    }

    private static string UnescapeIdentifier(string identifier)
    {
        var trimmed = identifier.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '`' && trimmed[^1] == '`'
            ? trimmed[1..^1].Replace("``", "`", StringComparison.Ordinal)
            : trimmed;
    }

    [GeneratedRegex(@"\bRETURN\s+(.+?)(?=\b(?:ORDER\s+BY|SKIP|LIMIT)\b|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ReturnRegex();

    [GeneratedRegex(@"\bAS\s+(`[^`]+`|[A-Za-z_][A-Za-z0-9_]*)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AliasRegex();

    [GeneratedRegex(@"\bRETURN\s+DISTINCT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReturnDistinctRegex();

}

internal sealed record AgeBatchCommand(
    string Name,
    string Cypher,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string> ProjectionColumns);

internal sealed record AgeBatchResult(string Name, IReadOnlyList<AgeRecord> Records);

internal sealed record AgeRecord(IReadOnlyDictionary<string, object?> Values)
{
    public IReadOnlyList<string> Keys { get; } = [.. Values.Keys];

    public object? this[string key] => Values[key];
}

internal interface IAgeRecordSource : IAsyncDisposable
{
    AgeRecord Current { get; }

    ValueTask<bool> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class AgeReaderRecordSource(
    DbCommand command,
    DbDataReader reader,
    Func<Exception, GraphException> wrapQueryExecutionFailure) : IAgeRecordSource
{
    public AgeRecord Current { get; private set; } = null!;

    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            Current = await AgeQueryRunner.ReadRecordAsync(reader, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (AgeQueryRunner.IsQueryExecutionFailure(exception))
        {
            throw wrapQueryExecutionFailure(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await command.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class AgeResultCursor : IAsyncDisposable
{
    private readonly IReadOnlyList<AgeRecord>? records;
    private readonly IAgeRecordSource? source;
    private readonly HashSet<string>? distinctKeys;
    private int currentIndex = -1;
    private AgeRecord? current;
    private bool disposed;

    public AgeResultCursor(IReadOnlyList<AgeRecord> records)
    {
        this.records = records ?? throw new ArgumentNullException(nameof(records));
    }

    public AgeResultCursor(IAgeRecordSource source, bool distinct)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        distinctKeys = distinct ? new HashSet<string>(StringComparer.Ordinal) : null;
    }

    public AgeRecord Current => current
        ?? throw new InvalidOperationException("The cursor is not positioned on a record.");

    public int Count => records?.Count
        ?? throw new InvalidOperationException("A streaming AGE cursor does not have a buffered count.");

    public async Task ConsumeAsync(CancellationToken cancellationToken = default)
    {
        if (records is not null)
        {
            return;
        }

        try
        {
            while (await FetchAsync(cancellationToken).ConfigureAwait(false))
            {
                // Drain the remaining records so the source is fully consumed.
            }
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<List<AgeRecord>> ToListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (records is not null)
        {
            return records.ToList();
        }

        var result = new List<AgeRecord>();
        try
        {
            while (await FetchAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(Current);
            }

            return result;
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<AgeRecord> SingleAsync(CancellationToken cancellationToken = default)
    {
        var result = await ToListAsync(cancellationToken).ConfigureAwait(false);
        return result.Count == 1
            ? result[0]
            : throw new InvalidOperationException($"Expected one AGE record but received {result.Count}.");
    }

    public async Task<AgeRecord> FirstAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (records is not null)
        {
            return records.Count > 0
                ? records[0]
                : throw new InvalidOperationException("The AGE result contains no records.");
        }

        try
        {
            return await FetchAsync(cancellationToken).ConfigureAwait(false)
                ? Current
                : throw new InvalidOperationException("The AGE result contains no records.");
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> FetchAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (records is not null)
        {
            currentIndex++;
            if (currentIndex >= records.Count)
            {
                current = null;
                return false;
            }

            current = records[currentIndex];
            return true;
        }

        while (await source!.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = source.Current;
            if (distinctKeys is null || distinctKeys.Add(AgeQueryRunner.GetDistinctKey(record)))
            {
                current = record;
                return true;
            }
        }

        current = null;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (source is not null)
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
