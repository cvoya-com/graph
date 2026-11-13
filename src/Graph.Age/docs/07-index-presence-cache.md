# Index presence cache

The AGE provider confirms that every declared companion table a plan or write needs still exists.
That check runs against `information_schema` and is deliberately cheap, but it happens on every
eligible plan and every write to a label with declarations. `AgeGraphStore` keeps a small
per-store cache of the answer so repeated operations in one process do not repeat the catalog
query. This page documents what is cached, the cross-process trade-off, and how to tune or disable
the cache.

## What is cached

- **Positive answers only.** An entry means "every required relation for this graph, declaration
  snapshot, and relation set exists". A missing relation is never stored.
- The key is the graph name, the registry declaration version, and the sorted relation names. A new
  declaration makes older entries unreachable without an explicit clear.
- The cache belongs to one `AgeGraphStore` instance. Two stores never share entries, and nothing is
  shared across processes.
- A maintenance call on any store in the process (`EnsureIndexesAsync` or `DropIndexesAsync`)
  bumps a process-wide generation counter. That invalidates every store's cached answers in the
  process, including stores that did not run the maintenance.
- A positive entry expires after a configurable TTL. The default is **five seconds**.

## Why negative answers are not cached

Caching "the table is absent" looks cheap, but it hides another process's `EnsureIndexesAsync` for
the rest of the TTL. Consider two processes:

1. Store A writes while another process has dropped the companion table. The probe is negative and
   the provider correctly skips the companion write.
2. Store B re-creates the structures and backfills the rows that exist at that moment.
3. Store A writes again inside the negative TTL. A cached negative would skip the companion row
   even though the table now exists.
4. When the TTL expires, the next probe succeeds, but nothing re-runs the skipped write. The
   companion row and the aggregate row stay permanently missing, so accelerated aggregate,
   equality, and group plans can return wrong results.

A missing table is the uncommon fallback path, so re-probing it on every write is cheap. Removing
negative entries makes a peer's ensure visible to the next write and limits the TTL to the
stale-**positive** window only. This is a correctness property, not a tuning option.

## Defaults and configuration

Both settings are on `AgeGraphStore` and default to the documented behavior:

```csharp
await using var store = new AgeGraphStore(connectionString, graphName, registry)
{
    // The defaults; set these before the first graph operation on the store.
    IndexPresenceCacheEnabled = true,
    IndexPresenceCacheTtl = TimeSpan.FromSeconds(5),
};
```

- `IndexPresenceCacheEnabled = false` makes every eligible plan and write probe
  `information_schema`. It removes the stale-positive window at the cost of one catalog query per
  operation.
- `IndexPresenceCacheTtl` must be greater than zero and no greater than 365 days. A zero or
  negative value, or a value above the upper bound, throws `ArgumentOutOfRangeException`; disable
  the cache instead of setting an unsupported TTL. The upper bound keeps the computed expiry
  representable.
- Like `LabelHierarchyValidation`, set both properties before the first graph operation. Changing
  them later affects subsequent probes, but the documented contract is "configure before use".

The TTL bounds only how long this process can believe a structure still exists after another
process dropped it. It does not affect a missing structure, which is always re-probed.

## The atomic `42P01` window

No cache can close the window between a successful presence probe and the companion `INSERT`: the
other instance can drop the table in between. In that window the provider receives PostgreSQL
`42P01` ("relation does not exist"). When the missing relation is the companion table the write
targets (or one of its aggregate tables), the provider wraps the error in a `GraphException` with
the original `PostgresException` as `InnerException` and a message like:

```text
The declared index structures for label 'Contract' were dropped by another instance within the
presence-cache TTL. Retry the write, or run EnsureIndexesAsync to recreate them.
```

The transaction rolls back atomically. An unrelated `42P01` error is not rewritten. The retry can
still hit a stale positive for up to the configured TTL; wait for it, or run
`EnsureIndexesAsync`/`DropIndexesAsync` on the writing store to clear the cache, then retry.

## When to disable or tune

- **Disable** on a writer that must observe every cross-process DDL change immediately, or when one
  catalog probe per plan/write is acceptable and maximum freshness matters more than the probe cost.
- **Shorten** the TTL when instances coordinate closely and a stale positive should last only
  briefly.
- **Keep the default** for a normally provisioned graph. Structures are created once, and a stale
  positive is only reachable during an uncoordinated drop/write window.
- **Do not disable** as a workaround for a missing `EnsureIndexesAsync`. Without maintenance there
  are no structures to find, and every probe still correctly falls back to the AGE path.

## Shared cache (for example Redis)

A shared cache is **not required for correctness** once negative answers are not cached. The local
positive cache only saves catalog probes. A shared cache could reduce those probes further, but a
shared positive entry would broaden the stale window unless every process invalidates it on drop,
and that invalidation is itself a distributed-coordination problem. This release adds no Redis
dependency and no shared transport. If one is ever added, it belongs behind a small seam and must
preserve the positive-only rule. Until then, disabling the local cache gives strict cross-process
freshness at the cost of one `information_schema` probe per plan or write.

## What still brings a stale graph back to correctness

- `EnsureIndexesAsync` re-creates missing structures and backfills the existing label rows.
- The provider write path repairs a row it writes; it cannot repair rows written while the cache
  held a stale negative, because this release never caches negatives.
- Direct SQL writes bypass the provider and still need `EnsureIndexesAsync` to rebuild derived
  rows.

## Verification checklist

1. `IndexPresenceCacheTtl` is positive and at most 365 days; use
   `IndexPresenceCacheEnabled = false` to probe every time.
2. No application code relies on a negative answer being cached; negatives always re-probe.
3. Maintenance windows are coordinated; a drop is not run concurrently with unrelated writers.
4. After an uncoordinated drop, the writer reports the wrapped `GraphException` and the caller
   retries or runs `EnsureIndexesAsync`.
5. If cross-process freshness is a hard requirement, the cache is disabled on the writer.

## Related pages

- [Declared equality indexes](01-declared-equality-indexes.md)
- [Ordering declarations and paging](02-ordering-declarations-and-paging.md)
- [Declared aggregates](03-declared-aggregates.md)
