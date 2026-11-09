// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age;

using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Npgsql.Age;

/// <summary>Represents an Apache AGE graph hosted by PostgreSQL.</summary>
/// <remarks>
/// A store created from a connection string owns its <see cref="NpgsqlDataSource"/>. A store
/// created from an existing data source leaves that data source under caller ownership. AGE is
/// loaded and its search path configured whenever a provider connection is opened.
/// </remarks>
public sealed class AgeGraphStore : IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;
    private readonly bool ownsDataSource;
    private readonly SemaphoreSlim provisioningGate = new(1, 1);
    private int disposed;
    private volatile bool graphReady;

    /// <summary>Initializes a store from a PostgreSQL connection string.</summary>
    /// <param name="connectionString">The connection string, or <see langword="null"/> to use <c>AGE_CONNECTION_STRING</c>.</param>
    /// <param name="graphName">The AGE graph name, or <see langword="null"/> to use <c>AGE_GRAPH</c> or <c>cvoya_graph</c>.</param>
    /// <param name="schemaRegistry">An optional schema registry.</param>
    /// <param name="loggerFactory">An optional logger factory.</param>
    public AgeGraphStore(
        string? connectionString = null,
        string? graphName = null,
        SchemaRegistry? schemaRegistry = null,
        ILoggerFactory? loggerFactory = null)
    {
        connectionString ??= Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Apache AGE connection string must be provided through the connectionString argument or AGE_CONNECTION_STRING.");
        }

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseAge();
        dataSource = builder.Build();
        ownsDataSource = true;
        GraphName = ResolveGraphName(graphName);
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        Graph = new AgeGraph(this, GraphName, schemaRegistry ?? new SchemaRegistry(), LoggerFactory);
    }

    /// <summary>Initializes a store from an externally owned AGE-enabled data source.</summary>
    /// <param name="dataSource">A data source configured with <c>UseAge()</c>.</param>
    /// <param name="graphName">The AGE graph name.</param>
    /// <param name="schemaRegistry">An optional schema registry.</param>
    /// <param name="loggerFactory">An optional logger factory.</param>
    public AgeGraphStore(
        NpgsqlDataSource dataSource,
        string graphName,
        SchemaRegistry? schemaRegistry = null,
        ILoggerFactory? loggerFactory = null)
        : this(dataSource, graphName, schemaRegistry, loggerFactory, batchExecutionObserver: null)
    {
    }

    internal AgeGraphStore(
        NpgsqlDataSource dataSource,
        string graphName,
        SchemaRegistry? schemaRegistry,
        ILoggerFactory? loggerFactory,
        Action? batchExecutionObserver,
        Func<AgeBatchCommand, AgeBatchCommand>? batchCommandTransform = null)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ownsDataSource = false;
        GraphName = ResolveGraphName(graphName);
        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        BatchExecutionObserver = batchExecutionObserver;
        BatchCommandTransform = batchCommandTransform;
        Graph = new AgeGraph(this, GraphName, schemaRegistry ?? new SchemaRegistry(), LoggerFactory);
    }

    /// <summary>Gets the graph abstraction for this store.</summary>
    public IGraph Graph { get; }

    /// <summary>
    /// Gets or sets how the store handles physical node labels that belong to a registered label
    /// hierarchy but were never registered themselves.
    /// </summary>
    /// <remarks>
    /// The default is <see cref="AgeLabelHierarchyValidationMode.Warn"/>. Validation runs once per
    /// store on the first graph operation (the explicit graph creation or the first read/write
    /// transaction) and uses <see cref="AgeTypeHierarchyRegistry"/>'s current hierarchy snapshot.
    /// Set this property before the first operation. Use
    /// <see cref="FindUnregisteredLabelHierarchyAsync(CancellationToken)"/> to inspect an already
    /// opened store at any time.
    /// </remarks>
    public AgeLabelHierarchyValidationMode LabelHierarchyValidation { get; set; } =
        AgeLabelHierarchyValidationMode.Warn;

    /// <summary>
    /// Registers a runtime node-label hierarchy for a derived label that has no CLR type, such as
    /// a type materialized from a Nulex <c>FormConfig</c> as <see cref="Cvoya.Graph.DynamicNode"/>.
    /// </summary>
    /// <param name="parentLabel">The label of the base logical type.</param>
    /// <param name="childLabel">The label of the derived logical type.</param>
    /// <remarks>
    /// Registration is process-wide and additive. CLR <see cref="NodeAttribute"/> hierarchies are
    /// discovered from the <see cref="SchemaRegistry"/> automatically when the graph is created.
    /// Register every physical label that can store a derived instance before the first query;
    /// otherwise base-label queries cannot see its rows. The provider reports missing registrations
    /// according to <see cref="LabelHierarchyValidation"/>.
    /// </remarks>
    public static void RegisterLabelHierarchy(string parentLabel, string childLabel) =>
        AgeTypeHierarchyRegistry.Instance.Register(parentLabel, childLabel);

    /// <summary>
    /// Finds physical node labels that store rows declaring a known hierarchy label but were never
    /// registered with <see cref="RegisterLabelHierarchy(string, string)"/>.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>
    /// One finding per unregistered physical label, or an empty list when every physical label that
    /// declares a known hierarchy label is registered. Findings are ordered by physical label and
    /// registered ancestor.
    /// </returns>
    /// <remarks>
    /// This method only inspects the graph; it never changes the hierarchy registry. Pass each
    /// finding to <see cref="RegisterLabelHierarchy(string, string)"/> to make the missing rows
    /// visible to base-label queries. It is safe to call at any time and independent of
    /// <see cref="LabelHierarchyValidation"/>.
    /// </remarks>
    public async Task<IReadOnlyList<AgeLabelHierarchyFinding>> FindUnregisteredLabelHierarchyAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        var knownLabels = AgeTypeHierarchyRegistry.Instance.GetKnownLabels();
        if (knownLabels.Count == 0)
        {
            return [];
        }

        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionLease = connection.ConfigureAwait(false);
        return await AgeLabelHierarchyValidator
            .FindUnregisteredAsync(
                connection,
                transaction: null,
                GraphName,
                knownLabels,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Creates the configured AGE graph when it does not already exist.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    public async Task CreateGraphIfNotExistsAsync(CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionLease = connection.ConfigureAwait(false);
        await CreateGraphIfNotExistsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // SemaphoreSlim.Dispose cannot run concurrently with WaitAsync or Release. A caller may have
        // passed OpenConnectionAsync's disposed check without reaching the gate yet, so its current
        // count cannot prove that the gate is quiescent. AvailableWaitHandle is never read, meaning the
        // gate owns no operating-system handle and can safely be reclaimed with the store instead.

        if (ownsDataSource)
        {
            await dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal string GraphName { get; }

    internal ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// Test instrumentation invoked immediately before a graph batch crosses the Npgsql execution
    /// boundary. It deliberately stays internal so measuring round trips does not expand the public API.
    /// </summary>
    internal Action? BatchExecutionObserver { get; }

    /// <summary>
    /// Internal failure-injection seam applied to each graph batch command before execution. It is
    /// used only by provider tests that prove post-batch validation restores operation atomicity.
    /// </summary>
    internal Func<AgeBatchCommand, AgeBatchCommand>? BatchCommandTransform { get; }

    /// <summary>
    /// Test instrumentation invoked once per execution of the graph-creation sequence, including
    /// attempts that go on to fail. It deliberately stays internal so counting provisioning runs does
    /// not expand the public API.
    /// </summary>
    internal Action? ProvisioningObserver { get; set; }

    /// <summary>
    /// Test instrumentation invoked when a caller reaches the provisioning gate, before it waits on
    /// it. Paired with <see cref="ProvisioningObserver"/> it lets a test prove every racer was
    /// committed to the gate while a sequence was still in flight, rather than arriving after one
    /// finished and taking the graph-ready fast path - which would leave the test passing vacuously.
    /// </summary>
    internal Action? ProvisioningGateObserver { get; set; }

    internal async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs the shared provisioning core unconditionally. An explicit request to create the graph
    /// re-verifies the database rather than trusting what this store created earlier, because the
    /// caller may be recovering from a drop this store never observed.
    /// </summary>
    internal Task CreateGraphIfNotExistsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken) =>
        ProvisionAsync(connection, skipWhenProvisioned: false, cancellationToken);

    /// <summary>
    /// Creates the graph on first write use only. Read-only transactions use
    /// <see cref="EnsureGraphExistsAsync"/> and never enter this mutating path.
    /// </summary>
    internal Task EnsureGraphCreatedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken) =>
        graphReady
            ? Task.CompletedTask
            : ProvisionAsync(connection, skipWhenProvisioned: true, cancellationToken);

    /// <summary>Verifies that the configured graph exists without mutating database state.</summary>
    /// <remarks>
    /// A successful probe publishes <see cref="graphReady"/>, so only a store's first read-only
    /// transaction pays the catalog round-trip; later reads - and the write path's graph-creation
    /// check - trust the verified graph. An explicit
    /// <see cref="CreateGraphIfNotExistsAsync(CancellationToken)"/> still clears the flag and
    /// re-verifies the database.
    /// </remarks>
    internal async Task EnsureGraphExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (graphReady)
        {
            return;
        }

        if (!await GraphExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            throw new GraphException($"Apache AGE graph '{GraphName}' does not exist.");
        }

        await ValidateLabelHierarchyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        graphReady = true;
    }

    /// <summary>
    /// Serializes graph creation within this store and publishes <see cref="graphReady"/> only after the
    /// graph-creation sequence has succeeded.
    /// </summary>
    /// <remarks>
    /// The gate keeps concurrent first-use operations on one store from each running the sequence;
    /// <see cref="Schema.AgeProvisioningLock"/> extends that to the peers this store cannot see. Because
    /// the flag is published last, a failed or cancelled attempt leaves the graph unconfirmed and the
    /// next caller free to retry.
    /// </remarks>
    private async Task ProvisionAsync(
        NpgsqlConnection connection,
        bool skipWhenProvisioned,
        CancellationToken cancellationToken)
    {
        ProvisioningGateObserver?.Invoke();

        await provisioningGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The second check: callers that queued behind the winning first-use initialization
            // inherit its result instead of each repeating it.
            if (skipWhenProvisioned && graphReady)
            {
                return;
            }

            // An explicit call re-verifies a graph that may have been dropped since this store first
            // created it. Clear the cached success before that attempt so a failure or cancellation
            // cannot leave later implicit first use trusting stale state.
            graphReady = false;
            await ProvisionCoreAsync(connection, cancellationToken).ConfigureAwait(false);
            graphReady = true;
        }
        finally
        {
            provisioningGate.Release();
        }
    }

    /// <summary>
    /// The one idempotent opening sequence for writes: probe for the graph and create only the graph
    /// itself when absent. Logical label/type tables are created by the authorized write that first
    /// needs them; the provider owns no managed index artifacts.
    /// </summary>
    /// <remarks>
    /// It runs in a single transaction holding <see cref="Schema.AgeProvisioningLock"/>, so peers racing
    /// the same graph name run it one at a time and a failure part-way rolls the graph back instead of
    /// leaving a half-created graph behind. Nothing is interpreted as an "already exists" race here:
    /// permission, connectivity, syntax, timeout, and cancellation failures all propagate as themselves.
    /// </remarks>
    private async Task ProvisionCoreAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        ProvisioningObserver?.Invoke();

        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionLease = transaction.ConfigureAwait(false);
        await Schema.AgeProvisioningLock
            .AcquireAsync(connection, transaction, GraphName, cancellationToken)
            .ConfigureAwait(false);

        if (!await GraphExistsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            await CreateGraphAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        }

        await ValidateLabelHierarchyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies <see cref="LabelHierarchyValidation"/> once for the graph's first operation.
    /// </summary>
    /// <remarks>
    /// The validation runs against the same transaction as graph verification, so a failure in
    /// <see cref="AgeLabelHierarchyValidationMode.Fail"/> mode prevents the operation (and, on the
    /// provisioning path, the graph creation) from committing. A warning-mode inspection failure is
    /// logged and does not turn an otherwise healthy graph operation into an error.
    /// </remarks>
    private async Task ValidateLabelHierarchyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var mode = LabelHierarchyValidation;
        if (mode == AgeLabelHierarchyValidationMode.Off)
        {
            return;
        }

        var knownLabels = AgeTypeHierarchyRegistry.Instance.GetKnownLabels();
        if (knownLabels.Count == 0)
        {
            return;
        }

        IReadOnlyList<AgeLabelHierarchyFinding> findings;
        try
        {
            findings = await AgeLabelHierarchyValidator
                .FindUnregisteredAsync(
                    connection,
                    transaction,
                    GraphName,
                    knownLabels,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (mode == AgeLabelHierarchyValidationMode.Warn &&
            exception is not OperationCanceledException)
        {
            LoggerFactory
                .CreateLogger<AgeGraphStore>()
                .LogWarningLabelHierarchyValidationFailed(exception);
            return;
        }

        if (findings.Count == 0)
        {
            return;
        }

        var logger = LoggerFactory.CreateLogger<AgeGraphStore>();
        foreach (var finding in findings)
        {
            logger.LogWarningUnregisteredLabelHierarchy(finding.PhysicalLabel, finding.RegisteredAncestor);
        }

        if (mode == AgeLabelHierarchyValidationMode.Fail)
        {
            var details = string.Join(
                Environment.NewLine,
                findings.Select(finding =>
                    $"  - '{finding.PhysicalLabel}' stores rows that declare registered ancestor '{finding.RegisteredAncestor}'."));
            throw new GraphException(
                $"""
                The AGE graph '{GraphName}' contains {findings.Count} physical node label(s) that are not registered in the runtime label hierarchy:
                {details}
                Base-label queries silently omit their rows. Register each label with AgeGraphStore.RegisterLabelHierarchy(parentLabel, childLabel) before the first query, or set LabelHierarchyValidation to Warn or Off.
                """);
        }
    }

    private async Task<bool> GraphExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = @name)";
        command.Parameters.AddWithValue("name", GraphName);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private async Task CreateGraphAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = "SELECT ag_catalog.create_graph(@name)";
        command.Parameters.AddWithValue("name", GraphName);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveGraphName(string? graphName)
    {
        graphName ??= Environment.GetEnvironmentVariable("AGE_GRAPH") ?? "cvoya_graph";
        return AgeSqlIdentifier.Validate(graphName, "graph name");
    }

    private static async Task ConfigureConnectionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
