// Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
// See LICENSE in the project root for full license terms.

namespace Cvoya.Graph.Age.Schema;

using System.Collections.Concurrent;

/// <summary>
/// Caches whether a set of provider relations exists for one store and declaration snapshot.
/// </summary>
/// <remarks>
/// The cache is an instance rather than a static so two stores cannot share answers. A static
/// generation counter is bumped by every maintenance operation in the process, so a store that did
/// not perform the operation drops its cached answers too. Entries also expire after a configurable
/// interval, which bounds how long an external process's DDL can leave a stale <em>positive</em>
/// answer.
/// <para>
/// Only positive answers are cached. A missing relation is the uncommon fallback path, and caching
/// it would hide another process's <c>EnsureIndexesAsync</c> for the whole TTL: the write would skip
/// the companion row and leave the aggregate table permanently incomplete after the TTL expired.
/// Negatives therefore always re-probe <c>information_schema</c>.
/// </para>
/// </remarks>
internal sealed class AgeIndexPresenceCache
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(5);

    /// <summary>The largest TTL the cache accepts, chosen so an expiry stays representable.</summary>
    internal static readonly TimeSpan MaxTtl = TimeSpan.FromDays(365);

    private static long generation;
    private readonly ConcurrentDictionary<string, Entry> presence = new(StringComparer.Ordinal);
    private long ttlTicks = DefaultTtl.Ticks;
    private int enabled = 1;

    /// <summary>Gets a cached availability answer for one graph, snapshot, and relation set.</summary>
    internal bool TryGet(string graphName, long catalogVersion, IReadOnlyList<string> tableNames, out bool present)
    {
        present = false;
        if (!IsEnabled || tableNames.Count == 0)
        {
            return false;
        }

        var key = Key(graphName, catalogVersion, tableNames);
        if (!presence.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (!entry.Present ||
            entry.Generation != Volatile.Read(ref generation) ||
            entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            presence.TryRemove(key, out _);
            return false;
        }

        present = true;
        return true;
    }

    /// <summary>Stores a positive availability answer for one graph, snapshot, and relation set.</summary>
    internal void Set(string graphName, long catalogVersion, IReadOnlyList<string> tableNames, bool present)
    {
        if (tableNames.Count == 0)
        {
            return;
        }

        var key = Key(graphName, catalogVersion, tableNames);
        if (!IsEnabled || !present)
        {
            // A negative answer is never cached, and an explicit negative also evicts an older
            // positive answer for the same relation set.
            presence.TryRemove(key, out _);
            return;
        }

        presence[key] = new Entry(
            Present: true,
            Volatile.Read(ref generation),
            DateTimeOffset.UtcNow.Add(Ttl));
    }

    /// <summary>Drops every cached answer in this process after a maintenance operation.</summary>
    internal void Clear()
    {
        presence.Clear();
        Interlocked.Increment(ref generation);
    }

    /// <summary>Gets whether the cache serves answers at all.</summary>
    internal bool IsEnabled => Volatile.Read(ref enabled) != 0;

    /// <summary>Gets how long a positive answer stays valid.</summary>
    internal TimeSpan Ttl => TimeSpan.FromTicks(Volatile.Read(ref ttlTicks));

    /// <summary>Applies a validated configuration to this cache instance.</summary>
    internal void Configure(bool cacheEnabled, TimeSpan ttl)
    {
        ValidateTtl(ttl);
        Volatile.Write(ref ttlTicks, ttl.Ticks);
        Volatile.Write(ref enabled, cacheEnabled ? 1 : 0);
        if (!cacheEnabled)
        {
            presence.Clear();
        }
    }

    /// <summary>
    /// Rejects a non-positive TTL because it would make the cache unusable, and a TTL above
    /// <see cref="MaxTtl"/> because the computed expiry would overflow <see cref="DateTimeOffset"/>.
    /// </summary>
    internal static void ValidateTtl(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                ttl,
                "The index presence cache TTL must be greater than zero. Disable the cache instead.");
        }

        if (ttl > MaxTtl)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                ttl,
                "The index presence cache TTL must not exceed 365 days. Use a shorter TTL or disable the cache.");
        }
    }

    private static string Key(string graphName, long catalogVersion, IReadOnlyList<string> tableNames)
    {
        var sorted = tableNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal);
        return graphName + "\u001f" + catalogVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "\u001f" + string.Join("\u001e", sorted);
    }

    private sealed record Entry(bool Present, long Generation, DateTimeOffset ExpiresAt);
}
