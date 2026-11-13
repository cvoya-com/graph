// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age.Core;
using Cvoya.Graph.Age.Querying.Cypher.Execution;
using Cvoya.Graph.CompatibilityTests;
using Npgsql;
using Npgsql.Age;

public sealed class AgeSubgraphBatchTests(AgeGraphCleanupFixture graphCleanup)
{
    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    [Fact]
    public async Task CreateAsync_NestedComplexProperties_ExecutesOneBatch()
    {
        var batchExecutions = 0;
        await using var dataSource = CreateDataSource();
        await using var store = await graphCleanup.CreateStoreAsync(
            dataSource,
            "cvoya_subgraph_batch",
            TestContext.Current.CancellationToken,
            (source, graphName) => new AgeGraphStore(
                source,
                graphName,
                schemaRegistry: null,
                loggerFactory: null,
                () => Interlocked.Increment(ref batchExecutions)));

        // Warm lazy schema initialization and the measured graph path before resetting the seam.
        await CreateNestedSubgraphAsync(store.Graph, "warm");
        Interlocked.Exchange(ref batchExecutions, 0);

        var (source, target) = await CreateNestedSubgraphAsync(store.Graph, "measured");

        Assert.Equal(1, Volatile.Read(ref batchExecutions));
        var fetchedSource = await store.Graph.Nodes<Class1>()
            .Where(node => node.Property1 == source.Property1)
            .SingleAsync(TestContext.Current.CancellationToken);
        var fetchedTarget = await store.Graph.Nodes<Class1>()
            .Where(node => node.Property1 == target.Property1)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("measured source nested", fetchedSource.A?.B?.Property1);
        Assert.Equal("measured target nested", fetchedTarget.A?.B?.Property1);
    }

    [Fact]
    public async Task EndpointCommandBatches_LeaveNoCorrelationOrSyntheticComplexIds()
    {
        var batchExecutions = 0;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        await using var store = await graphCleanup.CreateStoreAsync(
            dataSource,
            "cvoya_subgraph_transient",
            cancellationToken,
            (source, graphName) => new AgeGraphStore(
                source,
                graphName,
                schemaRegistry: null,
                loggerFactory: null,
                () => Interlocked.Increment(ref batchExecutions)));

        var selectedSource = new Person { FirstName = "Selected source" };
        var selectedTarget = new Person { FirstName = "Selected target" };
        await store.Graph.CreateNodeAsync(selectedSource, cancellationToken: cancellationToken);
        await store.Graph.CreateNodeAsync(selectedTarget, cancellationToken: cancellationToken);
        var selectedQuery = store.Graph.Nodes<Person>()
            .Where(person => person.TestKey == selectedSource.TestKey);
        var selectedTargetQuery = store.Graph.Nodes<Person>()
            .Where(person => person.TestKey == selectedTarget.TestKey);
        await store.Graph.CreateRelationshipAsync(
            selectedQuery,
            new Knows(),
            selectedTargetQuery,
            cancellationToken: cancellationToken);
        var hybridTarget = CreateNestedNode("hybrid");
        await store.Graph.CreateAsync(
            selectedQuery,
            new Knows(),
            hybridTarget,
            cancellationToken: cancellationToken);

        var collectionEndpoint = new Class2
        {
            Property1 = "shared collection root",
            A =
            {
                new ComplexClassA
                {
                    Property1 = "first",
                    B = new ComplexClassB { Property1 = "first nested" },
                },
                new ComplexClassA
                {
                    Property1 = "second",
                    B = new ComplexClassB { Property1 = "second nested" },
                },
            },
        };
        await store.Graph.CreateAsync(
            collectionEndpoint,
            new Knows(),
            collectionEndpoint,
            cancellationToken: cancellationToken);

        var selfLoop = CreateNestedNode("self loop");
        await store.Graph.CreateSelfLoopAsync(
            selfLoop,
            new Knows(),
            cancellationToken: cancellationToken);

        Assert.Equal(3, Volatile.Read(ref batchExecutions));
        Assert.Equal(
            2,
            await store.Graph.Nodes<Class2>()
                .Where(node => node.Property1 == collectionEndpoint.Property1)
                .CountAsync(cancellationToken));
        Assert.Equal("hybrid nested", (await store.Graph.Nodes<Class1>()
            .Where(node => node.Property1 == hybridTarget.Property1)
            .SingleAsync(cancellationToken)).A?.B?.Property1);
        await AssertNoTransientCorrelationOrSyntheticComplexIdsAsync(store.Graph, cancellationToken);
    }

    [Theory]
    [InlineData("source_root", false)]
    [InlineData("target_root", false)]
    [InlineData("complex_level_0", false)]
    [InlineData("complex_level_1", false)]
    [InlineData("relationship", false)]
    [InlineData("cleanup", false)]
    [InlineData("source_root", true)]
    [InlineData("target_root", true)]
    [InlineData("complex_level_0", true)]
    [InlineData("complex_level_1", true)]
    [InlineData("relationship", true)]
    [InlineData("cleanup", true)]
    public async Task EndpointCommandBatch_ValidationFailure_RollsBackEveryLevel(
        string failedCommand,
        bool callerOwnsTransaction)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        await using var store = await graphCleanup.CreateStoreAsync(
            dataSource,
            "cvoya_subgraph_failure",
            cancellationToken,
            (source, graphName) => new AgeGraphStore(
                source,
                graphName,
                schemaRegistry: null,
                loggerFactory: null,
                batchExecutionObserver: null,
                batchCommandTransform: command => command.Name == failedCommand
                    ? command with
                    {
                        Cypher = "UNWIND [] AS ignored RETURN " + string.Join(
                            ", ",
                            command.ProjectionColumns.Select(column => $"count(ignored) AS {column}")),
                    }
                    : command));
        var source = CreateNestedNode($"failed {failedCommand} source");
        var target = CreateNestedNode($"failed {failedCommand} target");
        var relationship = new Knows();

        if (callerOwnsTransaction)
        {
            await using var transaction = await store.Graph.GetTransactionAsync(cancellationToken);
            await Assert.ThrowsAsync<GraphException>(() => store.Graph.CreateAsync(
                source,
                relationship,
                target,
                RelationshipDirection.Outgoing,
                transaction,
                cancellationToken));

            await store.Graph.CreateNodeAsync(
                new Person { FirstName = "caller transaction remains usable" },
                transaction,
                cancellationToken);
            await transaction.CommitAsync();
        }
        else
        {
            await Assert.ThrowsAsync<GraphException>(() => store.Graph.CreateAsync(
                source,
                relationship,
                target,
                cancellationToken: cancellationToken));
        }

        Assert.Empty(await store.Graph.Nodes<Class1>().ToListAsync(cancellationToken));
        Assert.Empty(await store.Graph.Relationships<Knows>().ToListAsync(cancellationToken));
        await AssertNoTransientCorrelationOrSyntheticComplexIdsAsync(store.Graph, cancellationToken);
    }

    [Fact]
    public async Task ConcurrentEndpointCommandBatches_DoNotCrossCorrelateEqualValues()
    {
        const int operationCount = 12;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = CreateDataSource();
        await using var store = await graphCleanup.CreateStoreAsync(
            dataSource,
            "cvoya_subgraph_concurrent",
            cancellationToken);

        await Task.WhenAll(Enumerable.Range(0, operationCount).Select(_ =>
        {
            var equalEndpoint = CreateNestedNode("parallel equal");
            return store.Graph.CreateAsync(
                equalEndpoint,
                new Knows(),
                equalEndpoint,
                cancellationToken: cancellationToken);
        }));

        Assert.Equal(operationCount * 2, await store.Graph.Nodes<Class1>().CountAsync(cancellationToken));
        Assert.Equal(operationCount, await store.Graph.Relationships<Knows>().CountAsync(cancellationToken));
        await AssertNoTransientCorrelationOrSyntheticComplexIdsAsync(store.Graph, cancellationToken);
    }

    private static async Task<(Class1 Source, Class1 Target)> CreateNestedSubgraphAsync(
        IGraph graph,
        string prefix)
    {
        var source = new Class1
        {
            Property1 = $"{prefix} source",
            A = new ComplexClassA
            {
                Property1 = $"{prefix} source level one",
                B = new ComplexClassB { Property1 = $"{prefix} source nested" },
            },
        };
        var target = new Class1
        {
            Property1 = $"{prefix} target",
            A = new ComplexClassA
            {
                Property1 = $"{prefix} target level one",
                B = new ComplexClassB { Property1 = $"{prefix} target nested" },
            },
        };
        var relationship = new Knows();
        await graph.CreateAsync(
            source,
            relationship,
            target,
            cancellationToken: TestContext.Current.CancellationToken);
        return (source, target);
    }

    private static Class1 CreateNestedNode(string value) => new()
    {
        Property1 = value,
        A = new ComplexClassA
        {
            Property1 = $"{value} level one",
            B = new ComplexClassB { Property1 = $"{value} nested" },
        },
    };

    private static async Task AssertNoTransientCorrelationOrSyntheticComplexIdsAsync(
        IGraph graph,
        CancellationToken cancellationToken)
    {
        await using var transaction = await graph.GetTransactionAsync(cancellationToken);
        var runner = ((AgeGraphTransaction)transaction).Runner;
        await using var correlationResult = await runner.RunAsync(
            """
            MATCH (node)
            UNWIND keys(node) AS propertyName
            WITH node, propertyName
            WHERE propertyName STARTS WITH $prefix
            RETURN count(DISTINCT node) AS correlatedCount
            """,
            new { prefix = "__graphModelSubgraphCorrelation" },
            cancellationToken);
        var correlationRecord = await correlationResult.SingleAsync(cancellationToken);
        Assert.Equal(0, correlationRecord["correlatedCount"].As<int>());

        await using var identityResult = await runner.RunAsync(
            """
            MATCH ()-[ownership]->(value)
            WHERE ownership.__graphModelComplexProperty = true
            RETURN count(value) AS valueCount,
                   count(CASE WHEN value.Id IS NOT NULL THEN value ELSE null END) AS valueIdCount,
                   count(CASE WHEN ownership.Id IS NOT NULL THEN ownership ELSE null END) AS relationshipIdCount
            """,
            new { },
            cancellationToken);
        var identityRecord = await identityResult.SingleAsync(cancellationToken);
        Assert.Equal(0, identityRecord["valueIdCount"].As<int>());
        Assert.Equal(0, identityRecord["relationshipIdCount"].As<int>());
        await transaction.CommitAsync();
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseAge();
        return builder.Build();
    }
}
