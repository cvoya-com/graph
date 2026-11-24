// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Npgsql;
using Npgsql.Age;

/// <summary>
/// Owns every AGE graph created by the test assembly and removes those graphs after all tests and
/// class fixtures have finished.
/// </summary>
/// <remarks>
/// Cleanup belongs to an assembly fixture so xUnit reports a teardown failure separately from any
/// test failure that preceded it. Graph names are registered before setup starts but marked as
/// created only after provisioning succeeds, which keeps partial initialization safe.
/// </remarks>
public sealed class AgeGraphCleanupFixture : IAsyncDisposable
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5456;Username=postgres;Password=postgres;Database=postgres";

    private readonly Dictionary<string, bool> graphs = new(StringComparer.Ordinal);
    private readonly object sync = new();
    private readonly Func<string, CancellationToken, Task>? dropGraph;
    private int disposed;

    /// <summary>Initializes the assembly-wide AGE graph cleanup owner.</summary>
    public AgeGraphCleanupFixture()
    {
    }

    internal AgeGraphCleanupFixture(Func<string, CancellationToken, Task> dropGraph)
    {
        this.dropGraph = dropGraph;
    }

    internal string CreateGraphName(string prefix)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            var graphName = $"{prefix}_{Guid.NewGuid():N}";
            graphs.Add(graphName, false);
            return graphName;
        }
    }

    internal void MarkGraphCreated(string graphName)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (!graphs.ContainsKey(graphName))
            {
                throw new InvalidOperationException($"AGE test graph '{graphName}' is not owned by this fixture.");
            }

            graphs[graphName] = true;
        }
    }

    internal async Task<AgeGraphStore> CreateStoreAsync(
        NpgsqlDataSource dataSource,
        string graphNamePrefix,
        CancellationToken cancellationToken,
        Func<NpgsqlDataSource, string, AgeGraphStore>? createStore = null)
    {
        var graphName = CreateGraphName(graphNamePrefix);
        var store = createStore?.Invoke(dataSource, graphName) ?? new AgeGraphStore(dataSource, graphName);
        try
        {
            await store.CreateGraphIfNotExistsAsync(cancellationToken);
            MarkGraphCreated(graphName);
            return store;
        }
        catch
        {
            await store.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        List<string> createdGraphs;
        lock (sync)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            createdGraphs = graphs
                .Where(graph => graph.Value)
                .Select(graph => graph.Key)
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        TestContext.Current.SendDiagnosticMessage(
            "AGE graph cleanup owns {0} created graph(s).",
            createdGraphs.Count);

        return DisposeAsyncCore(createdGraphs);
    }

    private async ValueTask DisposeAsyncCore(List<string> createdGraphs)
    {
        if (createdGraphs.Count == 0)
        {
            return;
        }

        var failures = new List<Exception>();
        NpgsqlDataSource? dataSource = null;
        try
        {
            Func<string, CancellationToken, Task> cleanup;
            if (dropGraph is null)
            {
                var builder = new NpgsqlDataSourceBuilder(
                    Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING") ?? DefaultConnectionString);
                builder.UseAge();
                dataSource = builder.Build();
                cleanup = (graphName, cancellationToken) =>
                    DropGraphAsync(dataSource, graphName, cancellationToken);
            }
            else
            {
                cleanup = dropGraph;
            }

            foreach (var graphName in createdGraphs)
            {
                try
                {
                    await cleanup(graphName, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    TestContext.Current.SendDiagnosticMessage(
                        "Failed to drop AGE test graph '{0}': {1}",
                        graphName,
                        exception);
                    failures.Add(new InvalidOperationException(
                        $"Failed to drop AGE test graph '{graphName}'. The graph may have leaked.",
                        exception));
                }
            }
        }
        finally
        {
            if (dataSource is not null)
            {
                try
                {
                    await dataSource.DisposeAsync();
                }
                catch (Exception exception)
                {
                    failures.Add(new InvalidOperationException(
                        "Failed to dispose the data source used to clean up AGE test graphs.",
                        exception));
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"AGE test graph cleanup failed for {failures.Count} operation(s).",
                failures);
        }
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
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = @name)";
        command.Parameters.AddWithValue("name", graphName);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            return;
        }

        command.CommandText = "SELECT ag_catalog.drop_graph(@name, true)";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
