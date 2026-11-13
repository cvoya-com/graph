# Apache AGE performance features

This directory documents the user-facing performance features of the `Cvoya.Graph.Age` provider.
Each document covers one feature end to end: a working example, the physical database objects it
creates, multi-client behavior, the query shapes it accelerates, fallback and idempotency, cleanup,
and a verification checklist.

The features are **opt-in declarations**. Registering a declaration only publishes provider-neutral
metadata; the provider changes storage only when you call `EnsureIndexesAsync` on an `AgeGraphStore`
that was constructed with that `SchemaRegistry`.

Declared property indexes and aggregates are **node-property features**. A declaration names a node
label; the AGE provider does not build or plan relationship/edge indexes. Use the type-safe
`SchemaRegistry.Node<TNode>()` builder or the advanced string API with a node label. There is no
`Relationship<T>()` builder.

## Prerequisites

- PostgreSQL with Apache AGE 1.7.0 or 1.8.0.
- An `AgeGraphStore` created with the same `SchemaRegistry` that carries the declarations.
- A graph created with `CreateGraphIfNotExistsAsync`.
- One explicit maintenance window (or coordinated startup) for `EnsureIndexesAsync`. The call runs
  in one transaction and holds a graph-scoped PostgreSQL advisory lock while it creates and
  backfills structures.

## Quick start

```csharp
using Cvoya.Graph;
using Cvoya.Graph.Age;

[Node("Contract")]
public sealed record Contract : Node
{
    public string Name { get; set; } = string.Empty;

    [Property(Label = "tariff")]
    public string? Tier { get; set; }

    public long Amount { get; set; }
}

var registry = new SchemaRegistry();

// Type-safe declarations (label and stored property names come from [Node] and [Property]).
registry.Node<Contract>()
    .AddPropertyIndex(contract => contract.Tier)                              // equality
    .AddPropertyIndex(contract => contract.Name, PropertyIndexKind.Ordering)  // paging gate
    .AddPropertyAggregate(
        contract => contract.Tier,
        contract => contract.Amount,
        AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
        AggregateFunction.Max | AggregateFunction.Average);

// The advanced string API stays available for dynamically built declarations.
registry.AddPropertyIndex(new PropertyIndexDefinition("Contract", "Name"));
registry.AddPropertyAggregate(new PropertyAggregateDefinition(
    "Contract", "tariff", "Amount", AggregateFunction.Count)); // merges additively

await using var store = new AgeGraphStore(connectionString, graphName, registry);
await store.CreateGraphIfNotExistsAsync(cancellationToken);
await store.EnsureIndexesAsync(cancellationToken);
```

The `__cvoya_index` catalog maps each declaration to its physical table, column or function name,
and format version. Hash names are opaque, so use the catalog when you inspect a deployed graph:

```sql
SELECT * FROM "<graph>"."__cvoya_index";
```

```csharp
var gold = await store.Graph.Nodes<Contract>()
    .Where(contract => contract.Tier == "gold")
    .Select(contract => contract.Name)
    .ToListAsync(cancellationToken);
```

`EnsureIndexesAsync` must run once per graph and declaration snapshot. Other application instances
must run it themselves (or be restarted into a deployment that does); declarations alone never
change another process's storage. Coordinate maintenance windows and do not write through one
instance while another instance is dropping the declared structures: such a write can pass a stale
cached presence check and fail with a missing-relation error (SQLSTATE 42P01), the transaction rolls
back atomically. The provider wraps the expected companion-table error in a `GraphException` with
the label and retry guidance; only positive answers are cached.
An immediate retry can still hit the stale positive for up to the 5 s presence-cache
TTL. Wait for the TTL, or run `EnsureIndexesAsync`/`DropIndexesAsync` on the writing store to clear
the cache, then retry. See [the index presence cache](07-index-presence-cache.md) to tune or disable
the cache.

## Which feature should I use?

| Feature | Use it for | Creates physical storage? |
| --- | --- | --- |
| [Declared equality indexes](01-declared-equality-indexes.md) | `==`/`!=`, `GROUP BY`, `DISTINCT` on one string property | Yes: `<label>__idx` companion table |
| [Ordering declarations and paging](02-ordering-declarations-and-paging.md) | `OrderBy` + `Skip`/`Take` over a string property | No: a plan gate over an equality companion |
| [Declared aggregates](03-declared-aggregates.md) | `Count`/`Sum`/`Min`/`Max`/`Average` grouped by one property | Yes: aggregate table, functions, triggers |
| [Complex property hydration](04-complex-property-hydration.md) | Auto-loading nested value objects and their collections | Yes: value nodes and relationships on write |
| [Native ID lists and AGE versions](05-native-id-lists-and-age-versions.md) | Full-text `Search` and endpoint filters that resolve many native ids | No: version-specific query lowering |
| [Collectible types in depth planning](06-collectible-plugin-types.md) | Measuring plugin-provided derived types without retaining them in depth caches | No: read-time type discovery |
| [Index presence cache](07-index-presence-cache.md) | Per-store positive-only presence cache: tuning, disabling, and the cross-process `42P01` window | No: per-process cache of catalog probes |

Start with equality indexes. Add ordering only for the exact sort property that paging uses. Add
aggregates only for a group key and numeric value that a dashboard or report repeatedly computes
over a large label. Do not declare every property: each structure adds write amplification and
maintenance cost.

## One naming scheme, no compatibility names

Every label uses the same generalized physical layout:

- index table `<label>__idx`, or `idx_<16 hex>_table` when the label would exceed PostgreSQL's
  63-byte identifier limit;
- hashed column and index names derived with SHA-256 from `(label, property, role)`;
- aggregate table `<label>__agg_<16 hex>`, bounded to `agg_<16 hex>` when needed;
- trigger functions and triggers derived from the aggregate table name.

There are no `Status`/`Kaufpreis` compatibility names. A property literally named `Status` or
`Kaufpreis` is stored exactly like any other property. `__status`, `__num_agg`, `status_text`, and
`kaufpreis` are never created, read, or probed. See the
[breaking-change and cleanup section](01-declared-equality-indexes.md#breaking-change-and-cleanup)
for the one-time manual cleanup of objects created by an older preview build.

## Example project

The snippets show model declarations and application statements together. Put each model type in
its own file and the application statements in your console host's entry point; supply connection
settings and cancellation tokens from that host. The repository's `examples/Playground` provides a
ready host. The AGE benchmark harness
`tests/Graph.Age.Performance.Tests/AgeIndexBenchmarkHarness.cs` is the executable end-to-end reference
for the declared-index scenarios.

## Operational checklist

1. Declare the smallest set of properties that real query shapes need.
2. Run `EnsureIndexesAsync` in a maintenance window; watch `StructuresCreated` and
   `RowsBackfilled` in the returned `GraphIndexMaintenanceReport`.
3. Confirm the documented `<label>__idx` table and `__cvoya_index` catalog rows exist.
4. Re-run `EnsureIndexesAsync` after adding declarations or creating new labels.
5. Use `DropIndexesAsync` to remove the recorded structures; it is idempotent.
6. Re-verify query correctness after maintenance; declarations must never change results.
