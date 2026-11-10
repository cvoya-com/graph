// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Querying.Cypher;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.CompatibilityTests;
using Npgsql;
using Npgsql.Age;

/// <summary>The compatibility-suite harness for Apache AGE.</summary>
public sealed class AgeHarness(AgeGraphCleanupFixture graphCleanup) : IGraphProviderTestHarness
{
    private readonly List<AgeGraphStore> stores = [];
    private readonly string connectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";
    private NpgsqlDataSource? dataSource;

    public string ProviderName => "Cvoya.Graph.Age";

    public CapabilitySet Capabilities => AgeDialect.Instance.Capabilities;

    public async ValueTask InitializeAsync()
    {
        try
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseAge();
            dataSource = builder.Build();
            await using var store = await graphCleanup.CreateStoreAsync(
                dataSource,
                "cvoya_tck",
                TestContext.Current.CancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new GraphProviderUnavailableException(
                "Apache AGE is unavailable. Set AGE_CONNECTION_STRING or run the repository AGE container.",
                exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in stores)
        {
            await store.DisposeAsync();
        }

        if (dataSource is null)
        {
            return;
        }

        await dataSource.DisposeAsync();
        dataSource = null;
    }

    public async ValueTask<IGraph> GetGraphAsync(
        StoreIsolation isolation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Every isolation level gets its own AGE graph and its own store instance, so previously
        // returned graphs are never reset or replaced and StoreIsolation.IndependentStore needs no
        // separate path.
        AgeGraphStore? store = null;
        var registered = false;
        try
        {
            store = await graphCleanup.CreateStoreAsync(
                dataSource ?? throw new GraphProviderUnavailableException("The AGE test data source was not initialized."),
                "cvoya_tck",
                cancellationToken);
            stores.Add(store);
            registered = true;
            return store.Graph;
        }
        catch (NpgsqlException exception)
        {
            throw new GraphProviderUnavailableException(
                "Apache AGE is unavailable. Set AGE_CONNECTION_STRING or run the repository AGE container.",
                exception);
        }
        finally
        {
            // Dispose on every failure path (including cancellation, which the narrowed catch
            // above deliberately doesn't wrap); a successfully registered store is cleaned up by
            // DisposeAsync instead.
            if (store is not null && !registered)
            {
                await store.DisposeAsync();
            }
        }
    }

    public async ValueTask SeedExternalGraphAsync(
        IGraph graph,
        string marker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        await using var transaction = await graph.GetTransactionAsync(cancellationToken);
        var native = transaction as AgeGraphTransaction
            ?? throw new ArgumentException("The graph was not created by the AGE harness.", nameof(graph));
        await using var result = await native.Runner.RunAsync(
            """
            CREATE (source:ContractExternalNode)
            SET source.Marker = $marker, source.Role = 'source',
                source.inheritance_labels = ['ContractExternalNode']
            CREATE (target:ContractExternalNode)
            SET target.Marker = $marker, target.Role = 'target',
                target.inheritance_labels = ['ContractExternalNode']
            CREATE (source)-[relationship:CONTRACT_EXTERNAL_RELATIONSHIP]->(target)
            SET relationship.Marker = $marker
            RETURN true AS created
            """,
            new { marker },
            cancellationToken);
        _ = await result.SingleAsync(cancellationToken);
        await transaction.CommitAsync();
    }

    public async ValueTask<IReadOnlyCollection<string>> GetStoreArtifactsAsync(
        IGraph graph,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        await using var transaction = await graph.GetTransactionAsync(cancellationToken);
        var native = transaction as AgeGraphTransaction
            ?? throw new ArgumentException("The graph was not created by the AGE harness.", nameof(graph));
        var artifacts = await native.Runner.QueryScalarStringsAsync(
            """
            SELECT artifact
            FROM (
                SELECT 'label:' || label.name || ':' || label.kind::text AS artifact
                FROM ag_catalog.ag_label AS label
                JOIN ag_catalog.ag_graph AS graph ON graph.graphid = label.graph
                WHERE graph.name = @query
                UNION ALL
                SELECT 'index:' || indexname || ':' || indexdef AS artifact
                FROM pg_indexes
                WHERE schemaname = @query
                UNION ALL
                SELECT 'constraint:' || constraint_name || ':' || constraint_type AS artifact
                FROM information_schema.table_constraints
                WHERE constraint_schema = @query
                UNION ALL
                SELECT 'function:' || routine_name || ':' || coalesce(routine_definition, '') AS artifact
                FROM information_schema.routines
                WHERE routine_schema = @query
            ) AS artifacts
            ORDER BY artifact
            """,
            native.Runner.GraphName,
            cancellationToken);
        await transaction.CommitAsync();
        return artifacts;
    }

    public async ValueTask<int> CountNodesByPropertyAsync(
        IGraph graph,
        string label,
        string propertyName,
        IReadOnlyCollection<string> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(values);
        var escapedProperty = CypherIdentifier.EscapeIfNeeded(propertyName, "property name");

        await using var transaction = await graph.GetTransactionAsync(cancellationToken);
        var ageTransaction = transaction as AgeGraphTransaction
            ?? throw new ArgumentException("The graph was not created by the AGE harness.", nameof(graph));
        var result = await ageTransaction.Runner.RunAsync(
            $$"""
            MATCH (n)
            WHERE ($label IN labels(n) OR properties(n) @> { inheritance_labels: [$label] })
              AND n.{{escapedProperty}} IN $values
            RETURN count(n) AS node_count
            """,
            new { label, values = values.ToArray() },
            cancellationToken);
        await using var resultLease = result.ConfigureAwait(false);
        var record = await result.SingleAsync(cancellationToken);
        await transaction.CommitAsync();
        return record["node_count"].As<int>();
    }

    public bool IsExpectedConcurrentUpdateException(Exception exception) =>
        exception.GetBaseException() is PostgresException { SqlState: "40001" or "40P01" };
}
