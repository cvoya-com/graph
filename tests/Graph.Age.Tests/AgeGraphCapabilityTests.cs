// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.CompatibilityTests;
using Npgsql;
using Npgsql.Age;

public sealed class AgeGraphCapabilityTests(AgeGraphCleanupFixture graphCleanup)
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("AGE_CONNECTION_STRING")
        ?? "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    [Fact]
    public async Task ServerCapabilities_ReportVersionAndOptionalFeatures()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var store = new AgeGraphStore(ConnectionString, $"caps_{Guid.NewGuid():N}");

        var capabilities = await store.GetServerCapabilitiesAsync(cancellationToken);

        Assert.True(capabilities.Version >= new Version(1, 7, 0));
        Assert.Equal(
            capabilities.Version >= new Version(1, 8, 0),
            capabilities.SupportsNativeShortestPath);
        Assert.Equal(
            capabilities.Version >= new Version(1, 8, 0),
            capabilities.SupportsNativePredicateFunctions);
    }

    [Fact]
    public async Task DisabledOptions_RemoveVersionedFeatures()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var store = new AgeGraphStore(
            ConnectionString,
            $"caps_{Guid.NewGuid():N}",
            options: new AgeGraphStoreOptions
            {
                NativeShortestPath = AgeFeatureMode.Disabled,
                NativePredicateFunctions = AgeFeatureMode.Disabled,
            });

        var capabilities = await store.GetServerCapabilitiesAsync(cancellationToken);

        Assert.False(capabilities.SupportsNativeShortestPath);
        Assert.False(capabilities.SupportsNativePredicateFunctions);
    }

    [Fact]
    public async Task EnabledOptions_RequireAge18()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Version version;
        await using (var probe = new AgeGraphStore(ConnectionString, $"caps_{Guid.NewGuid():N}"))
        {
            version = (await probe.GetServerCapabilitiesAsync(cancellationToken)).Version;
        }

        await using var store = new AgeGraphStore(
            ConnectionString,
            $"caps_{Guid.NewGuid():N}",
            options: new AgeGraphStoreOptions
            {
                NativeShortestPath = AgeFeatureMode.Enabled,
                NativePredicateFunctions = AgeFeatureMode.Enabled,
            });

        if (version >= new Version(1, 8, 0))
        {
            var capabilities = await store.GetServerCapabilitiesAsync(cancellationToken);
            Assert.True(capabilities.SupportsNativeShortestPath);
            Assert.True(capabilities.SupportsNativePredicateFunctions);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<GraphException>(
                () => store.GetServerCapabilitiesAsync(cancellationToken));
            Assert.Contains("1.8.0", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DisabledShortestPath_RejectsTheTypedQueryWithCapabilityDiagnostic()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseAge();
        await using var dataSource = builder.Build();
        await using var store = await graphCleanup.CreateStoreAsync(
            dataSource,
            "caps",
            cancellationToken,
            (source, graphName) => new AgeGraphStore(
                source,
                graphName,
                options: new AgeGraphStoreOptions { NativeShortestPath = AgeFeatureMode.Disabled }));

        var start = new Person { FirstName = $"disabled-{Guid.NewGuid():N}" };
        var end = new Person { FirstName = $"disabled-{Guid.NewGuid():N}" };
        await store.Graph.CreateNodeAsync(start, null, cancellationToken);
        await store.Graph.CreateNodeAsync(end, null, cancellationToken);
        await store.Graph.ConnectAsync(start, new Knows(), end, cancellationToken: cancellationToken);

        var query = store.Graph.Nodes<Person>()
            .Where(person => person.TestKey == start.TestKey)
            .ShortestPath<Knows, Person>(person => person.TestKey == end.TestKey);

        var exception = await Assert.ThrowsAnyAsync<GraphException>(
            () => query.ToListAsync(cancellationToken));
        Assert.Contains("ShortestPath", exception.Message, StringComparison.Ordinal);
    }
}
