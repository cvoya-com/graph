// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Tests;

using Cvoya.Graph.Age;
using Cvoya.Graph.Age.Schema;

/// <summary>
/// Unit coverage for the per-store declared-index presence cache: negatives are never cached, a
/// positive answer expires at the configured TTL, and the cache can be disabled entirely.
/// </summary>
[Collection(GraphTests.AgeIndexPresenceCacheIsolation.Name)]
public sealed class AgeIndexPresenceCacheTests
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres";

    [Fact]
    public void NegativeAnswersAreNeverReturned()
    {
        var cache = new AgeIndexPresenceCache();

        cache.Set("graph", 1, ["Widget__idx"], present: false);

        Assert.False(cache.TryGet("graph", 1, ["Widget__idx"], out var present));
        Assert.False(present);
    }

    [Fact]
    public void ANewNegativeAnswerEvictsAnOlderPositiveAnswer()
    {
        var cache = new AgeIndexPresenceCache();
        cache.Set("graph", 1, ["Widget__idx"], present: true);
        Assert.True(cache.TryGet("graph", 1, ["Widget__idx"], out var present));
        Assert.True(present);

        cache.Set("graph", 1, ["Widget__idx"], present: false);

        Assert.False(cache.TryGet("graph", 1, ["Widget__idx"], out present));
        Assert.False(present);
    }

    [Fact]
    public async Task PositiveAnswersExpireAtTheConfiguredTtl()
    {
        var cache = new AgeIndexPresenceCache();
        cache.Configure(cacheEnabled: true, TimeSpan.FromMilliseconds(100));
        cache.Set("graph", 1, ["Widget__idx"], present: true);
        Assert.True(cache.TryGet("graph", 1, ["Widget__idx"], out _));

        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        Assert.False(cache.TryGet("graph", 1, ["Widget__idx"], out var present));
        Assert.False(present);
    }

    [Fact]
    public void DisabledCacheNeverServesAnAnswer()
    {
        var cache = new AgeIndexPresenceCache();
        cache.Configure(cacheEnabled: false, TimeSpan.FromSeconds(5));
        cache.Set("graph", 1, ["Widget__idx"], present: true);

        Assert.False(cache.IsEnabled);
        Assert.False(cache.TryGet("graph", 1, ["Widget__idx"], out var present));
        Assert.False(present);
    }

    [Fact]
    public void ClearDropsEveryAnswerForTheProcessGeneration()
    {
        var cache = new AgeIndexPresenceCache();
        cache.Set("graph", 1, ["Widget__idx"], present: true);
        Assert.True(cache.TryGet("graph", 1, ["Widget__idx"], out _));

        cache.Clear();

        Assert.False(cache.TryGet("graph", 1, ["Widget__idx"], out var present));
        Assert.False(present);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTtlIsRejected(int milliseconds)
    {
        var cache = new AgeIndexPresenceCache();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => cache.Configure(cacheEnabled: true, TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void TtlAtTheUpperBoundIsAccepted()
    {
        var documentedBound = TimeSpan.FromDays(365);
        Assert.Equal(documentedBound, AgeIndexPresenceCache.MaxTtl);

        var cache = new AgeIndexPresenceCache();

        cache.Configure(cacheEnabled: true, documentedBound);
        cache.Set("graph", 1, ["Widget__idx"], present: true);

        Assert.Equal(documentedBound, cache.Ttl);
        Assert.True(cache.TryGet("graph", 1, ["Widget__idx"], out var present));
        Assert.True(present);
    }

    [Fact]
    public void TtlAboveTheUpperBoundIsRejected()
    {
        var cache = new AgeIndexPresenceCache();
        var tooLarge = TimeSpan.FromDays(365) + TimeSpan.FromTicks(1);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => cache.Configure(cacheEnabled: true, tooLarge));

        Assert.Contains("must not exceed 365 days", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoreConfigurationIsAppliedToTheSharedCache()
    {
        await using var store = new AgeGraphStore(DefaultConnectionString, "nul112_cache_configuration");

        store.IndexPresenceCacheEnabled = false;
        store.IndexPresenceCacheTtl = TimeSpan.FromSeconds(2);

        Assert.False(store.IndexPresenceCache.IsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(2), store.IndexPresenceCache.Ttl);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.IndexPresenceCacheTtl = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.IndexPresenceCacheTtl = TimeSpan.FromMilliseconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.IndexPresenceCacheTtl = TimeSpan.FromDays(365) + TimeSpan.FromTicks(1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.IndexPresenceCacheTtl = TimeSpan.MaxValue);
    }
}
