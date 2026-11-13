// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Core;

using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

/// <summary>Owns one PostgreSQL connection and transaction for an AGE operation scope.</summary>
internal sealed class AgeGraphTransaction : IGraphTransaction
{
    private readonly AgeGraphContext context;
    private readonly ILogger<AgeGraphTransaction> logger;
    private NpgsqlConnection? connection;
    private NpgsqlTransaction? transaction;
    private AgeQueryRunner? runner;
    private bool committed;
    private bool rolledBack;

    public AgeGraphTransaction(AgeGraphContext context, bool isReadOnly = false)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        IsReadOnly = isReadOnly;
        logger = context.LoggerFactory?.CreateLogger<AgeGraphTransaction>()
            ?? NullLogger<AgeGraphTransaction>.Instance;
    }

    public bool IsActive => transaction is not null && !committed && !rolledBack;

    /// <summary>
    /// Gets whether this transaction was created by the given graph context. Ownership is
    /// reference identity: a transaction from a different store or graph instance is foreign even
    /// when connection settings match.
    /// </summary>
    internal bool BelongsTo(AgeGraphContext candidate) => ReferenceEquals(context, candidate);

    internal bool IsReadOnly { get; }

    internal NpgsqlConnection Connection => connection
        ?? throw new GraphException($"The transaction has not started yet. Call {nameof(BeginTransactionAsync)} first.");

    internal NpgsqlTransaction DbTransaction => transaction
        ?? throw new GraphException($"The transaction has not started yet. Call {nameof(BeginTransactionAsync)} first.");

    internal AgeQueryRunner Runner => runner
        ?? throw new GraphException($"The transaction has not started yet. Call {nameof(BeginTransactionAsync)} first.");

    public async Task CommitAsync()
    {
        EnsureActive();
        await transaction!.CommitAsync().ConfigureAwait(false);
        committed = true;
    }

    public async Task RollbackAsync()
    {
        EnsureActive();
        await transaction!.RollbackAsync().ConfigureAwait(false);
        rolledBack = true;
    }

    public async ValueTask DisposeAsync()
    {
        Exception? rollbackError = null;
        if (IsActive)
        {
            try
            {
                await transaction!.RollbackAsync().ConfigureAwait(false);
                rolledBack = true;
            }
            catch (NpgsqlException exception)
            {
                rollbackError = exception;
                logger.LogWarningAgeGraphTransaction70(exception);
            }
        }

        if (transaction is not null)
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            transaction = null;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            connection = null;
        }

        if (rollbackError is not null)
        {
            throw new GraphException("Failed to roll back an uncommitted AGE transaction.", rollbackError);
        }
    }

    internal async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (connection is not null)
        {
            throw new GraphException("Transaction has already started.");
        }

        connection = await context.Store.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsReadOnly)
            {
                await context.Store.EnsureGraphCreatedAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (IsReadOnly)
            {
                var readOnly = connection.CreateCommand();
                await using var readOnlyLease = readOnly.ConfigureAwait(false);
                readOnly.Transaction = transaction;
                readOnly.CommandText = "SET TRANSACTION READ ONLY";
                await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await context.Store
                    .EnsureGraphExistsAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);
            }

            runner = new AgeQueryRunner(
                context.GraphName,
                connection,
                transaction,
                context.LoggerFactory,
                context.Store.BatchExecutionObserver,
                context.Store.BatchCommandTransform,
                context.Store.IndexesProvisioned,
                context.Store.IndexCatalog,
                context.Store.IndexPresenceCache,
                context.Store.ProvisionedCatalogVersion);
        }
        catch
        {
            runner = null;
            if (transaction is not null)
            {
                try
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
                catch (NpgsqlException disposeException)
                {
                    logger.LogWarningAgeGraphTransaction127(disposeException);
                }

                transaction = null;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            connection = null;
            throw;
        }
    }

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new GraphException("Transaction is not active.");
        }
    }
}
