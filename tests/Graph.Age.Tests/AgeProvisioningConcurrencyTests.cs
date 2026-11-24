// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using System.Globalization;
using Cvoya.Graph.Age.Schema;
using Cvoya.Graph.Age.Serialization;
using Cvoya.Graph.CompatibilityTests;
using Npgsql;
using Npgsql.Age;

/// <summary>
/// Covers #376: first-use graph provisioning is a check-then-create sequence, so concurrent first
/// operations on one store - and independent stores racing the same graph name - have to converge on
/// one complete provisioning result instead of colliding inside <c>ag_catalog.create_graph</c>.
/// </summary>
/// <remarks>
/// The contention is staged deterministically rather than raced. A control connection holds the
/// provisioning advisory lock, the racers are started and then observed *waiting* on that exact key in
/// <c>pg_locks</c>, and only then is the lock released. Nothing depends on which task happens to be
/// scheduled first, so there are no retry loops and no timing tolerances - only bounded waits for
/// states the lock guarantees will be reached.
/// </remarks>
public sealed class AgeProvisioningConcurrencyTests(AgeGraphCleanupFixture graphCleanup)
{
    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5456;Username=postgres;Password=postgres;Database=postgres";

    private static readonly TimeSpan BlockedWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] RetiredManagedIndexNames =
        ["cvoya_node_fulltext_gin", "cvoya_rel_fulltext_gin"];

    private const int ConcurrentFirstUseCount = 8;
    private const int IndependentStoreCount = 4;

    [Fact]
    public async Task RecreateManagedIndexesAsync_OnUnprovisionedStore_DoesNotProvisionGraph()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var graphName = graphCleanup.CreateGraphName("cvoya_managed_index_noop");
        await using var dataSource = CreateDataSource();
        await using var store = new AgeGraphStore(dataSource, graphName);

        try
        {
            Assert.Equal(0, await GraphCountAsync(dataSource, graphName, cancellationToken));
            Assert.False(store.Graph.SchemaRegistry.IsInitialized);

            await store.Graph.RecreateManagedIndexesAsync(cancellationToken);

            Assert.Equal(0, await GraphCountAsync(dataSource, graphName, cancellationToken));
            Assert.False(store.Graph.SchemaRegistry.IsInitialized);
        }
        finally
        {
            if (await GraphCountAsync(dataSource, graphName, CancellationToken.None) != 0)
            {
                graphCleanup.MarkGraphCreated(graphName);
            }
        }
    }

    [Fact]
    public async Task ConcurrentFirstUse_OnOneStore_RunsOneProvisioningSequence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var graphName = graphCleanup.CreateGraphName("cvoya_provisioning");
        await using var dataSource = CreateDataSource();
        await using var store = new AgeGraphStore(dataSource, graphName);

        var provisioningRuns = 0;
        store.ProvisioningObserver = () => Interlocked.Increment(ref provisioningRuns);

        // Counting arrivals at the gate itself, not at the start of the task: a racer that signalled
        // early and was then descheduled could reach a store that had already finished provisioning,
        // take the fast path, and leave the run-count assertion below passing vacuously.
        var atGate = 0;
        var allAtGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ProvisioningGateObserver = () =>
        {
            if (Interlocked.Increment(ref atGate) == ConcurrentFirstUseCount)
            {
                allAtGate.SetResult();
            }
        };

        // The control lock parks the first attempt inside PostgreSQL, so every other first-use
        // operation is guaranteed to arrive while provisioning is still in flight - the exact window
        // the store-local gate exists to close.
        var holder = await ProvisioningLockHolder.AcquireAsync(dataSource, graphName, cancellationToken);
        await using var holderLease = holder.ConfigureAwait(false);

        var firstUse = Enumerable.Range(0, ConcurrentFirstUseCount)
            .Select(index => Task.Run(
                () => store.Graph.CreateNodeAsync(
                    new Person
                    {
                        FirstName = "Provisioning",
                        LastName = index.ToString(CultureInfo.InvariantCulture),
                    },
                    null,
                    cancellationToken),
                cancellationToken))
            .ToArray();

        // All eight are past the provisioned fast path and committed to the gate, and one of them is
        // provably blocked on the provisioning lock, so none can proceed until the lock is released.
        await allAtGate.Task.WaitAsync(cancellationToken);
        await WaitUntilWaitersAsync(dataSource, graphName, 1, cancellationToken);

        await holder.ReleaseAsync(cancellationToken);
        await Task.WhenAll(firstUse);
        graphCleanup.MarkGraphCreated(graphName);

        // Without the gate each of those eight would have run the sequence itself: every one of them
        // reached the gate while the winner was still parked, so none could see a provisioned store.
        Assert.Equal(ConcurrentFirstUseCount, atGate);
        Assert.Equal(1, provisioningRuns);
        await AssertGraphIsUsableAsync(dataSource, store, graphName, cancellationToken);
    }

    [Fact]
    public async Task IndependentStores_RacingTheSameFreshGraph_AllConverge()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var graphName = graphCleanup.CreateGraphName("cvoya_provisioning");
        await using var dataSource = CreateDataSource();

        // Separate store instances have separate gates, so nothing in process can coordinate them -
        // exactly the shape a store-local semaphore cannot fix on its own.
        var stores = Enumerable.Range(0, IndependentStoreCount)
            .Select(_ => new AgeGraphStore(dataSource, graphName))
            .ToArray();

        try
        {
            var holder = await ProvisioningLockHolder.AcquireAsync(dataSource, graphName, cancellationToken);
            await using var holderLease = holder.ConfigureAwait(false);

            var racers = stores
                .Select(store => Task.Run(
                    () => store.CreateGraphIfNotExistsAsync(cancellationToken),
                    cancellationToken))
                .ToArray();

            // All four reach PostgreSQL and queue on the one provisioning key: that is the race the
            // issue describes, staged rather than hoped for.
            await WaitUntilWaitersAsync(dataSource, graphName, stores.Length, cancellationToken);

            await holder.ReleaseAsync(cancellationToken);

            // None may fail: the peer that creates the graph wins, and the rest must positively
            // confirm it exists rather than colliding inside ag_catalog.create_graph.
            await Task.WhenAll(racers);
            graphCleanup.MarkGraphCreated(graphName);

            Assert.Equal(1, await GraphCountAsync(dataSource, graphName, cancellationToken));
            await AssertGraphIsUsableAsync(dataSource, stores[0], graphName, cancellationToken);
        }
        finally
        {
            foreach (var store in stores)
            {
                await store.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task FailedProvisioning_PropagatesTheDatabaseErrorAndStaysRetryable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var graphName = graphCleanup.CreateGraphName("cvoya_provisioning");
        await using var dataSource = CreateDataSource();
        await using var store = new AgeGraphStore(dataSource, graphName);

        // Begin with a successful provisioning run so the store has cached a true state, then remove
        // the graph behind its back. The explicit API is the supported recovery path for that case.
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);
        await DropGraphAsync(dataSource, graphName, cancellationToken);

        // A schema squatting on the graph's name makes ag_catalog.create_graph fail for a reason that
        // has nothing to do with a create race, so the error has to surface unchanged.
        await ExecuteAsync(dataSource, $"CREATE SCHEMA \"{graphName}\"", cancellationToken);

        var failure = await Assert.ThrowsAsync<PostgresException>(
            () => store.CreateGraphIfNotExistsAsync(cancellationToken));
        Assert.Equal(PostgresErrorCodes.DuplicateSchema, failure.SqlState);

        // The rolled-back attempt left nothing half-provisioned behind.
        Assert.Equal(0, await GraphCountAsync(dataSource, graphName, cancellationToken));

        await ExecuteAsync(dataSource, $"DROP SCHEMA \"{graphName}\"", cancellationToken);

        // Implicit first use still provisions: the failed recovery cleared the previous cached success
        // rather than leaving the store falsely convinced that the dropped graph still exists.
        await AssertGraphIsUsableAsync(dataSource, store, graphName, cancellationToken);
    }

    [Fact]
    public async Task CancelledProvisioning_DoesNotPublishStateAndStaysRetryable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var graphName = graphCleanup.CreateGraphName("cvoya_provisioning");
        await using var dataSource = CreateDataSource();
        await using var store = new AgeGraphStore(dataSource, graphName);

        // Exercise cancellation from a previously successful state: this is the path where preserving
        // the old cached value would make the later implicit retry skip provisioning.
        await store.CreateGraphIfNotExistsAsync(cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);
        await DropGraphAsync(dataSource, graphName, cancellationToken);

        var holder = await ProvisioningLockHolder.AcquireAsync(dataSource, graphName, cancellationToken);
        await using var holderLease = holder.ConfigureAwait(false);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancelled = Task.Run(
            () => store.CreateGraphIfNotExistsAsync(cancellation.Token),
            cancellationToken);

        // Cancel only once the attempt is provably parked in PostgreSQL, so the cancellation lands
        // mid-provisioning rather than before it started.
        await WaitUntilWaitersAsync(dataSource, graphName, 1, cancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        Assert.Equal(0, await GraphCountAsync(dataSource, graphName, cancellationToken));

        await holder.ReleaseAsync(cancellationToken);
        await AssertGraphIsUsableAsync(dataSource, store, graphName, cancellationToken);
    }

    [Fact]
    public async Task DisposalDuringProvisioning_LetsTheAttemptFinishAndRejectsLaterUse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var graphName = graphCleanup.CreateGraphName("cvoya_provisioning");
        await using var dataSource = CreateDataSource();
        var store = new AgeGraphStore(dataSource, graphName);

        var holder = await ProvisioningLockHolder.AcquireAsync(dataSource, graphName, cancellationToken);
        await using var holderLease = holder.ConfigureAwait(false);

        var provisioning = Task.Run(
            () => store.CreateGraphIfNotExistsAsync(cancellationToken),
            cancellationToken);
        await WaitUntilWaitersAsync(dataSource, graphName, 1, cancellationToken);

        // Disposal lands while the gate is held. Tearing the gate down here would fault the attempt
        // with an ObjectDisposedException instead of letting it finish.
        await store.DisposeAsync();
        await holder.ReleaseAsync(cancellationToken);

        await provisioning;
        graphCleanup.MarkGraphCreated(graphName);
        Assert.Equal(1, await GraphCountAsync(dataSource, graphName, cancellationToken));

        // Disposal is still idempotent, and still rejects everything that starts after it.
        await store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => store.CreateGraphIfNotExistsAsync(cancellationToken));
    }

    [Fact]
    public async Task ReadOnlyFirstUse_OfMissingGraph_ThrowsWithoutCreatingIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var graphName = graphCleanup.CreateGraphName("cvoya_provisioning");
        await using var dataSource = CreateDataSource();
        await using var store = new AgeGraphStore(dataSource, graphName);

        // Reads never provision: the probe inside the read-only transaction reports the missing
        // graph as an actionable error and must not create the graph as a side effect.
        var missing = await Assert.ThrowsAnyAsync<GraphException>(
            () => store.Graph.Nodes<Person>().ToListAsync(cancellationToken));
        var messages = string.Join(" | ", ExceptionChain(missing).Select(error => error.Message));
        Assert.Contains($"'{graphName}' does not exist", messages, StringComparison.Ordinal);
        Assert.Equal(0, await GraphCountAsync(dataSource, graphName, cancellationToken));

        // The first write creates the graph and publishes the store's verified-graph state, so
        // later read-only transactions skip the catalog probe and just work.
        await store.Graph.CreateNodeAsync(
            new Person { FirstName = "Now", LastName = "Present" },
            null,
            cancellationToken);
        graphCleanup.MarkGraphCreated(graphName);

        Assert.Single(await store.Graph.Nodes<Person>().ToListAsync(cancellationToken));
    }

    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    /// <summary>
    /// Proves the store honours the whole usable-store contract, not just the presence of a row in
    /// <c>ag_catalog.ag_graph</c>: CRUD and query work, the one logical label required by the write
    /// exists, and ordinary first use did not create legacy or managed full-text infrastructure.
    /// </summary>
    private static async Task AssertGraphIsUsableAsync(
        NpgsqlDataSource dataSource,
        AgeGraphStore store,
        string graphName,
        CancellationToken cancellationToken)
    {
        var person = new Person { FirstName = "Provisioned", LastName = "Store" };
        await store.Graph.CreateNodeAsync(person, null, cancellationToken);

        var loaded = await store.Graph.Nodes<Person>()
            .Where(candidate => candidate.TestKey == person.TestKey)
            .SingleAsync(cancellationToken);
        Assert.Equal("Provisioned", loaded.FirstName);

        var queried = await store.Graph.Nodes<Person>()
            .Where(candidate => candidate.LastName == "Store")
            .ToListAsync(cancellationToken);
        Assert.Contains(queried, candidate => candidate.TestKey == person.TestKey);

        Assert.Equal(
            ["Person"],
            await ScalarStringsAsync(
                dataSource,
                """
                SELECT label.name
                FROM ag_catalog.ag_label AS label
                JOIN ag_catalog.ag_graph AS graph ON graph.graphid = label.graph
                WHERE graph.name = @name AND label.name = ANY(@labels)
                ORDER BY label.name
                """,
                command =>
                {
                    command.Parameters.AddWithValue("name", graphName);
                    command.Parameters.AddWithValue(
                        "labels",
                        new[] { "Person", SerializationBridge.ComplexNodeLabel, SerializationBridge.ComplexRelationshipType });
                },
                cancellationToken));

        Assert.Empty(
            await ScalarStringsAsync(
                dataSource,
                """
                SELECT indexname FROM pg_indexes
                WHERE schemaname = @name AND indexname = ANY(@indexes)
                ORDER BY indexname
                """,
                command =>
                {
                    command.Parameters.AddWithValue("name", graphName);
                    command.Parameters.AddWithValue(
                        "indexes",
                        RetiredManagedIndexNames);
                },
                cancellationToken));
    }

    /// <summary>
    /// Blocks until at least <paramref name="expected"/> backends are waiting on the provisioning lock
    /// for <paramref name="graphName"/>. Matching the exact key keeps unrelated advisory waits
    /// elsewhere in the database - other test classes share the container - from satisfying the wait.
    /// </summary>
    private static async Task WaitUntilWaitersAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        int expected,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + BlockedWaitTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ProvisioningWaitersAsync(dataSource, graphName, cancellationToken) >= expected)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        Assert.Fail(
            $"Fewer than {expected} backends ever waited on the provisioning lock for '{graphName}', so " +
            "the check-then-create sequence is not serialized against competing connections.");
    }

    /// <summary>
    /// Counts the backends blocked on the provisioning lock for <paramref name="graphName"/>. The two
    /// 32-bit keys land in <c>pg_locks</c> as <c>classid</c>/<c>objid</c> with <c>objsubid = 2</c>, and
    /// they are unsigned there, so a negative key has to be compared through its unsigned value.
    /// </summary>
    private static async Task<int> ProvisioningWaitersAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionLease = connection.ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = """
            SELECT count(*) FROM pg_locks
            WHERE locktype = 'advisory' AND objsubid = 2 AND NOT granted
              AND classid::bigint = @class AND objid::bigint = @object
            """;
        command.Parameters.AddWithValue("class", (long)(uint)AgeProvisioningLock.ClassId);
        command.Parameters.AddWithValue("object", (long)(uint)AgeProvisioningLock.ObjectIdFor(graphName));
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<int> GraphCountAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionLease = connection.ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = "SELECT count(*) FROM ag_catalog.ag_graph WHERE name = @name";
        command.Parameters.AddWithValue("name", graphName);
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task DropGraphAsync(
        NpgsqlDataSource dataSource,
        string graphName,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionLease = connection.ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = "SELECT ag_catalog.drop_graph(@name, true)";
        command.Parameters.AddWithValue("name", graphName);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test-only helper; every caller passes a constant statement or a generated graph name.")]
    private static async Task ExecuteAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionLease = connection.ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test-only helper; every caller passes a constant statement and binds data as parameters.")]
    private static async Task<List<string>> ScalarStringsAsync(
        NpgsqlDataSource dataSource,
        string sql,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionLease = connection.ConfigureAwait(false);
        var command = connection.CreateCommand();
        await using var commandLease = command.ConfigureAwait(false);
        command.CommandText = sql;
        bind(command);

        var values = new List<string>();
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerLease = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseAge();
        return builder.Build();
    }

    /// <summary>
    /// A control connection holding the provisioning lock for one graph, so provisioning attempts can
    /// be parked deterministically and released on demand. It takes the lock through the production
    /// derivation rather than a recomputed key, so the test cannot drift from what the store locks.
    /// </summary>
    private sealed class ProvisioningLockHolder : IAsyncDisposable
    {
        private readonly NpgsqlConnection connection;
        private NpgsqlTransaction? transaction;

        private ProvisioningLockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            this.connection = connection;
            this.transaction = transaction;
        }

        internal static async Task<ProvisioningLockHolder> AcquireAsync(
            NpgsqlDataSource dataSource,
            string graphName,
            CancellationToken cancellationToken)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await AgeProvisioningLock
                    .AcquireAsync(connection, transaction, graphName, cancellationToken)
                    .ConfigureAwait(false);
                return new ProvisioningLockHolder(connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>Rolls the holding transaction back, which is what releases the lock.</summary>
        internal async Task ReleaseAsync(CancellationToken cancellationToken)
        {
            if (transaction is null)
            {
                return;
            }

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            transaction = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
                transaction = null;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
