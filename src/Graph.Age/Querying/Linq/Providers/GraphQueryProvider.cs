// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Querying.Linq.Providers;

using System.Linq.Expressions;
using System.Reflection;
using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Querying.Commands;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Querying;
using Cvoya.Graph.Querying.Commands;
using Cvoya.Graph.Querying.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

internal sealed class GraphQueryProvider : GraphQueryProviderBase<AgeGraphTransaction>, IGraphCommandProvider
{
    private readonly AgeGraphContext context;
    private readonly AgeGraphTransaction? transaction;
    private readonly bool isReadOnly;
    private readonly ILogger<GraphQueryProvider> logger;
    private readonly CypherEngine cypherEngine;

    public GraphQueryProvider(AgeGraphContext context, AgeGraphTransaction? transaction, bool isReadOnly = false)
        : base(context?.Graph ?? throw new ArgumentNullException(nameof(context)), transaction is null)
    {
        this.context = context;
        this.transaction = transaction;
        this.isReadOnly = isReadOnly;
        logger = context.LoggerFactory?.CreateLogger<GraphQueryProvider>()
            ?? NullLogger<GraphQueryProvider>.Instance;
        cypherEngine = new CypherEngine(context);
    }

    protected override async Task<TResult> ExecuteCoreAsync<TResult>(
        Expression expression,
        CancellationToken cancellationToken)
    {
        if (GraphMutationModelBuilder.IsMutation(expression))
        {
            var mutation = GraphMutationModelBuilder.Build(expression);
            CypherEngine.ValidateMutation(mutation);
            var affected = await ((IGraphCommandProvider)this).InWriteTransactionAsync(
                (command, token) => command.ApplyAsync(mutation, expression, token),
                cancellationToken).ConfigureAwait(false);
            return (TResult)(object)affected;
        }

        await context.SchemaManager.InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);

        var result = await TransactionHelpers.ExecuteInTransactionAsync(
            context,
            transaction,
            tx => cypherEngine.ExecuteAsync<TResult>(expression, tx, cancellationToken),
            "Error executing query",
            logger,
            isReadOnly,
            cancellationToken).ConfigureAwait(false);
        return result!;
    }

    object IGraphCommandProvider.GraphOwnershipToken => context;

    IGraphTransaction? IGraphCommandProvider.BoundTransaction => transaction;

    Task IGraphCommandProvider.PrepareRelationshipCreationAsync(CancellationToken cancellationToken) =>
        context.SchemaManager.InitializeSchemaAsync(cancellationToken);

    async Task<TResult> IGraphCommandProvider.InWriteTransactionAsync<TResult>(
        Func<IGraphCommandExecutionContext, CancellationToken, Task<TResult>> command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (transaction?.IsReadOnly == true)
        {
            throw new GraphException("A graph command cannot use a transaction opened with read-only access.");
        }

        // Public command execution reaches this method directly rather than passing through the
        // normal query execution path, but node/relationship validation uses the synchronous schema
        // lookup APIs. Initialize once before entering either an owned or caller-bound transaction.
        await context.SchemaManager.InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);

        return await TransactionHelpers.ExecuteInTransactionAsync(
            context,
            transaction,
            async tx =>
            {
                var savepoint = transaction is null ? null : $"cvoya_command_{Guid.NewGuid():N}";
                if (savepoint is not null)
                {
                    await tx.DbTransaction.SaveAsync(savepoint, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    var result = await command(
                        new AgeGraphCommandExecutionContext(tx, cypherEngine, context), cancellationToken).ConfigureAwait(false);
                    if (savepoint is not null)
                    {
                        await tx.DbTransaction.ReleaseAsync(savepoint, cancellationToken).ConfigureAwait(false);
                    }

                    return result;
                }
                catch (Exception operationException) when (savepoint is not null)
                {
                    try
                    {
                        await tx.DbTransaction.RollbackAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                        await tx.DbTransaction.ReleaseAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception rollbackException) when (rollbackException is NpgsqlException or InvalidOperationException)
                    {
                        throw new GraphException(
                            "Failed to restore the caller transaction after a graph command failed.",
                            new AggregateException(operationException, rollbackException));
                    }

                    throw;
                }
            },
            "Error executing graph command",
            logger,
            isReadOnly: false,
            cancellationToken).ConfigureAwait(false);
    }

    protected override async Task<AgeGraphTransaction> GetOrCreateTransactionAsync(CancellationToken cancellationToken)
    {
        await context.SchemaManager.InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);
        return await TransactionHelpers
            .GetOrCreateTransactionAsync(context, transaction, isReadOnly, cancellationToken)
            .ConfigureAwait(false);
    }

    protected override IAsyncEnumerable<TResult> StreamCoreAsync<TResult>(
        Expression expression,
        AgeGraphTransaction graphTransaction,
        CancellationToken cancellationToken) =>
        cypherEngine.StreamAsync<TResult>(expression, graphTransaction, cancellationToken);

    protected override bool IsTransactionActive(AgeGraphTransaction graphTransaction) => graphTransaction.IsActive;

    protected override bool UnwrapCreateQueryInvocationException => true;

    protected override bool ShouldWrapCreateQueryException(Exception exception) =>
        exception is AmbiguousMatchException or MethodAccessException or ArgumentException;

    protected override void LogExecution(Expression expression, Type resultType, bool streaming)
    {
        if (streaming)
        {
            logger.LogDebugGraphQueryProvider71(resultType.Name);
        }
        else
        {
            logger.LogDebugGraphQueryProvider75(resultType.Name);
        }
        logger.LogDebugGraphQueryProvider77(expression.Type.Name);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            LogExpressionTree(expression);
        }
    }

    protected override void LogExecutionFailure(Exception exception) =>
        logger.LogErrorGraphQueryProvider86(exception);

    protected override void LogRollbackFailure(Exception exception) =>
        logger.LogWarningGraphQueryProvider89(exception);

    protected override void LogEnumeratorDisposalFailure(Exception exception) =>
        logger.LogWarningGraphQueryProviderEnumeratorDisposalFailure(exception);

    protected override void LogDisposalFailure(Exception exception) =>
        logger.LogWarningGraphQueryProviderTransactionDisposalFailure(exception);

    private void LogExpressionTree(Expression expression, int depth = 0)
    {
        var indent = new string(' ', depth * 2);

        if (expression is MethodCallExpression methodCall)
        {
            logger.LogDebugGraphQueryProvider97(indent, methodCall.Method.Name, methodCall.Method.DeclaringType?.Name);

            foreach (var argument in methodCall.Arguments)
            {
                LogExpressionTree(argument, depth + 1);
            }
        }
        else if (expression is ConstantExpression constant)
        {
            logger.LogDebugGraphQueryProvider110(indent, constant.Value?.GetType().Name ?? "null");
        }
    }
}
