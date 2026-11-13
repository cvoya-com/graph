// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Core;

using Cvoya.Graph.Age.Entities;
using Cvoya.Graph.Age.Querying;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.Age.Querying.Linq.Providers;
using Cvoya.Graph.Querying.Commands;
using Cvoya.Graph.Querying.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

/// <summary>
/// Age implementation of the IGraph interface using a modular design with IGraphQueryable support.
/// </summary>
/// <remarks>
/// Instances are created and owned by <see cref="AgeGraphStore"/>. Dispose the store to release
/// provider-owned resources; this graph object does not own the PostgreSQL data-source lifetime.
/// </remarks>
internal class AgeGraph : IGraph
{
    private readonly ILogger _logger;
    private readonly AgeGraphContext _graphContext;
    private readonly SchemaRegistry _schemaRegistry;

    // Keep a reference to the graph store to ensure
    // that it's not garbage collected
    private readonly AgeGraphStore _graphStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgeGraph"/> class.
    /// </summary>
    public AgeGraph(AgeGraphStore store, string graphName, SchemaRegistry schemaRegistry, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<AgeGraph>() ?? NullLogger<AgeGraph>.Instance;
        _schemaRegistry = schemaRegistry ?? throw new ArgumentNullException(nameof(schemaRegistry));
        _graphStore = store ?? throw new ArgumentNullException(nameof(store));

        ArgumentException.ThrowIfNullOrWhiteSpace(graphName);

        _graphContext = new AgeGraphContext(
            this,
            _graphStore,
            loggerFactory,
            _schemaRegistry);

        // Don't initialize schema registry here - let it be initialized lazily on first use
        // This avoids double initialization and concurrency issues
        _logger.LogInformationAgeGraph49(graphName);
    }

    public SchemaRegistry SchemaRegistry => _schemaRegistry;

    /// <summary>Gets the owning store for provider-internal query planning and tests.</summary>
    internal AgeGraphStore Store => _graphStore;

    /// <inheritdoc />
    public async Task<IGraphTransaction> GetTransactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _logger.LogDebugAgeGraph61();

            var graphTransaction = new AgeGraphTransaction(_graphContext);
            await graphTransaction.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogDebugAgeGraph66();
            return graphTransaction;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (NpgsqlException ex)
        {
            const string message = "Failed to begin transaction";
            _logger.LogErrorAgeGraph80(ex);
            throw new GraphException(message, ex);
        }
    }

    /// <inheritdoc />
    public IGraphQueryable<N> Nodes<N>(IGraphTransaction? transaction = null)
        where N : class, Graph.INode
    {
        _logger.LogDebugAgeGraph89(typeof(N).Name);

        // Building a queryable performs no I/O. When no transaction is provided, execution time
        // (GraphQueryProvider.ExecuteAsync -> TransactionHelpers.ExecuteInTransactionAsync)
        // creates a per-execution transaction with proper lifecycle management, which prevents
        // session/connection leaks from long-lived queryables.
        var ageTransaction = ToAgeTransaction(transaction);
        var provider = new GraphQueryProvider(_graphContext, ageTransaction, isReadOnly: true);
        return new GraphNodeQueryable<N>(provider);
    }

    /// <inheritdoc />
    public IGraphQueryable<R> Relationships<R>(IGraphTransaction? transaction = null)
        where R : class, Graph.IRelationship
    {
        _logger.LogDebugAgeGraph104(typeof(R).Name);

        var ageTransaction = ToAgeTransaction(transaction);
        var provider = new GraphQueryProvider(_graphContext, ageTransaction, isReadOnly: true);
        return new GraphRelationshipQueryable<R>(provider);
    }

    /// <summary>
    /// Converts a public <see cref="IGraphTransaction"/> to the internal <see cref="AgeGraphTransaction"/>
    /// used by queryable construction, or <see langword="null"/> if none was given (in which case
    /// execution creates a per-execution transaction). Rejects transactions from another provider
    /// and transactions owned by a different AGE graph (reference identity, not settings).
    /// </summary>
    private AgeGraphTransaction? ToAgeTransaction(IGraphTransaction? transaction) => transaction switch
    {
        null => null,
        AgeGraphTransaction ageTransaction when ageTransaction.BelongsTo(_graphContext) => ageTransaction,
        AgeGraphTransaction => throw new GraphException(
            "The given transaction was created by a different AGE graph store. A transaction can only be used with the graph that created it."),
        _ => throw new GraphException(
            "The given transaction is not a valid AGE transaction. Use AgeGraphStore.Graph.GetTransactionAsync() to create it.")
    };

    /// <summary>
    /// Validates a caller-supplied transaction at a CRUD entry point: foreign-provider and
    /// wrong-graph transactions are rejected here, before schema initialization or any store
    /// work, and without touching the caller-owned transaction's lifecycle.
    /// </summary>
    private void EnsureOwnedTransaction(IGraphTransaction? transaction) => _ = ToAgeTransaction(transaction);

    /// <inheritdoc />
    public async Task CreateNodeAsync<N>(N node, IGraphTransaction? transaction = null, CancellationToken cancellationToken = default)
        where N : class, Graph.INode
    {
        if (node is null)
            throw new ArgumentException("Node cannot be null.", nameof(node));

        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwnedTransaction(transaction);

        try
        {
            _logger.LogDebugAgeGraph160(typeof(N).Name);

            // Ensure schema is created before any transaction (to avoid mixing schema and data operations)
            await _graphContext.SchemaManager.InitializeSchemaAsync(cancellationToken).ConfigureAwait(false);

            await TransactionHelpers.ExecuteInTransactionAsync(
                graphContext: _graphContext,
                transaction: transaction,
                tx => _graphContext.NodeManager.CreateNodeAsync(node, tx, cancellationToken),
                $"Failed to create node of type {typeof(N).Name}",
                _logger,
                cancellationToken: cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"Failed to create node of type {typeof(N).Name}";
            _logger.LogErrorAgeGraph182(ex, typeof(N).Name);

            if (ex is GraphException)
            {
                // If it's already a GraphException, rethrow it
                throw;
            }

            throw new GraphException(message, ex);
        }
    }

    /// <inheritdoc />
    public Task CreateAsync<TSource, TRelationship, TTarget>(
        TSource source,
        TRelationship relationship,
        TTarget target,
        RelationshipDirection direction = RelationshipDirection.Outgoing,
        IGraphTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        where TSource : class, Graph.INode
        where TRelationship : class, Graph.IRelationship
        where TTarget : class, Graph.INode
        => GraphCommandExtensions.CreateNewAsync(
            this,
            source,
            relationship,
            target,
            direction,
            GraphRelationshipCreationMode.Standard,
            transaction,
            cancellationToken);

    // Dynamic entity methods

    /// <inheritdoc />
    public IGraphQueryable<DynamicNode> DynamicNodes(IGraphTransaction? transaction = null)
    {
        _logger.LogDebugAgeGraph457();

        var ageTransaction = ToAgeTransaction(transaction);
        var provider = new GraphQueryProvider(_graphContext, ageTransaction, isReadOnly: true);
        return new GraphNodeQueryable<DynamicNode>(provider);
    }

    /// <inheritdoc />
    public IGraphQueryable<DynamicRelationship> DynamicRelationships(IGraphTransaction? transaction = null)
    {
        _logger.LogDebugAgeGraph467();

        var ageTransaction = ToAgeTransaction(transaction);
        var provider = new GraphQueryProvider(_graphContext, ageTransaction, isReadOnly: true);
        return new GraphRelationshipQueryable<DynamicRelationship>(provider);
    }

    /// <inheritdoc />
    public IGraphQueryable<Graph.IEntity> Search(string query, IGraphTransaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query, nameof(query));

        var ageTransaction = ToAgeTransaction(transaction);
        var provider = new GraphQueryProvider(_graphContext, ageTransaction, isReadOnly: true);
        var nodeSource = ((IQueryable)new GraphNodeQueryable<Graph.INode>(provider)).Expression;
        var relationshipSource = ((IQueryable)new GraphRelationshipQueryable<Graph.IRelationship>(provider)).Expression;
        var searchRoot = new AgeMixedSearchRootExpression(query, nodeSource, relationshipSource);
        return new GraphQueryable<Graph.IEntity>(provider, searchRoot);
    }

    /// <inheritdoc />
    public IGraphQueryable<Graph.INode> SearchNodes(string query, IGraphTransaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query, nameof(query));

        return Nodes<Graph.INode>(transaction).Search(query);
    }

    /// <inheritdoc />
    public IGraphQueryable<Graph.IRelationship> SearchRelationships(string query, IGraphTransaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query, nameof(query));

        return Relationships<Graph.IRelationship>(transaction).Search(query);
    }

    /// <inheritdoc />
    public IGraphQueryable<T> SearchNodes<T>(string query, IGraphTransaction? transaction = null) where T : class, Graph.INode
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query, nameof(query));

        return Nodes<T>(transaction).Search(query);
    }

    /// <inheritdoc />
    public IGraphQueryable<T> SearchRelationships<T>(string query, IGraphTransaction? transaction = null) where T : class, Graph.IRelationship
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query, nameof(query));

        return Relationships<T>(transaction).Search(query);
    }

    /// <inheritdoc />
    public Task RecreateManagedIndexesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

}
