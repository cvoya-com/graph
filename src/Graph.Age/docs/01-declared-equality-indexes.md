# Declared equality indexes

Declare an equality index when one string property of one label is compared for equality or used as
a grouping/distinct key often enough that scanning the AGE label table dominates the query.

## Example

```csharp
using Cvoya.Graph;
using Cvoya.Graph.Age;

[Node("Contract")]
public sealed record Contract : Node
{
    public string Name { get; set; } = string.Empty;

    [Property(Label = "tariff")]
    public string? Tariff { get; set; }
}

var registry = new SchemaRegistry();
registry.Node<Contract>().AddPropertyIndex(contract => contract.Tariff);

await using var store = new AgeGraphStore(connectionString, "billing", registry);
await store.CreateGraphIfNotExistsAsync(cancellationToken);

await store.Graph.CreateNodeAsync(
    new Contract { Name = "A-1", Tariff = "gold" }, cancellationToken: cancellationToken);
await store.Graph.CreateNodeAsync(
    new Contract { Name = "A-2", Tariff = "silver" }, cancellationToken: cancellationToken);

await store.EnsureIndexesAsync(cancellationToken);

// Accelerated: equality filter.
var gold = await store.Graph.Nodes<Contract>()
    .Where(contract => contract.Tariff == "gold")
    .Select(contract => contract.Name)
    .ToListAsync(cancellationToken);

// Also accelerated: GROUP BY and DISTINCT over the declared property.
var counts = await store.Graph.Nodes<Contract>()
    .GroupBy(contract => contract.Tariff)
    .Select(group => new { group.Key, Count = group.Count() })
    .ToListAsync(cancellationToken);
```

The advanced string API is equivalent:

```csharp
registry.AddPropertyIndex(new PropertyIndexDefinition("Contract", "tariff"));
```

## What changes in the database

For each declared label the provider creates one companion table in the graph schema:

```sql
CREATE TABLE IF NOT EXISTS "billing"."Contract__idx" (
    "id" ag_catalog.graphid PRIMARY KEY,
    "p_3f2a...e1" text
);
CREATE INDEX IF NOT EXISTS "idx_9c01...77_idx"
    ON "billing"."Contract__idx" ("p_3f2a...e1");
```

- The table is never an AGE vertex label table. Keeping derived columns off the AGE label table
  preserves the two-column row shape that AGE variable-length traversal expects.
- The column is a SHA-256-derived `p_<16 hex>` name computed from the label, stored property name,
  and `text` role. A label or property name change produces a different column.
- The table name is `<label>__idx`, or `idx_<16 hex>_table` when the label name would exceed 63
  bytes.
- `EnsureIndexesAsync` backfills the table from the AGE label table with one UPSERT, then runs
  `ANALYZE`.
- The provider records an `index` row and (for aggregates) `column` rows in the documented
  `__cvoya_index` catalog table, with the physical table name and format version.
- The provider write path synchronizes the companion row in the same transaction as create, update,
  and delete. Direct SQL writes that bypass the provider need `EnsureIndexesAsync` again.

## Scale-out and multiple clients

`EnsureIndexesAsync` runs in one transaction and takes the graph-scoped PostgreSQL advisory lock
used by graph provisioning. Concurrent `EnsureIndexesAsync`/`DropIndexesAsync` calls from several
application instances therefore serialize; one creates and backfills, the others observe the
completed structures and report no new structures.

- The structures become visible to other clients when the transaction commits. A failed pass rolls
  back and leaves the previous structures usable.
- Planner eligibility is confirmed with a per-store relation probe. Each store caches only
  **positive** answers, for five seconds by default, and invalidates them on its own maintenance;
  a store in another process cannot know that a peer created a table until the positive expires and
  the probe re-runs. A missing relation is never cached, so the next write observes a peer's
  `EnsureIndexesAsync` immediately. See
  [the index presence cache](07-index-presence-cache.md) to tune or disable it.
- Coordinate maintenance windows across instances and do not write through one instance while
  another instance is dropping the declared structures. Such a write can pass a stale cached
  positive and fail with a missing-relation error (SQLSTATE 42P01); the transaction rolls back
  atomically. The provider wraps that expected companion-table error in a `GraphException` whose
  message names the label and says to retry or run `EnsureIndexesAsync`; unrelated `42P01` errors
  pass through unchanged.
  An immediate retry can still hit the stale positive for up to the 5 s presence-cache
  TTL. Wait for the TTL, or run `EnsureIndexesAsync`/`DropIndexesAsync` on the writing store to
  clear the cache, then retry.
- If a declaration is added after `EnsureIndexesAsync`, the new declaration has no physical column
  until the next pass. The plan needs every declared column, so queries keep using the AGE path
  in the meantime.
- Operators should run maintenance during a quiet window for the first backfill. Re-runs are cheap
  when the table is already populated, but they still scan each label to refresh rows.

## Accelerated query shapes

Accelerated for a declared string property:

- equality: `entity.Property == "value"`;
- negated equality with the provider's null guard: `entity.Property != "value"`;
- `GROUP BY` the property;
- `DISTINCT` the property;
- paged `OrderBy` over the property when the matching **ordering** declaration also exists (see
  [ordering declarations](02-ordering-declarations-and-paging.md)).

Not accelerated and left on the ordinary AGE Cypher path:

- comparisons (`<`, `>`, `StartsWith`, `Contains`) and non-string values;
- sorting without an ordering declaration;
- a property that is not declared;
- a label whose companion table or required catalog structure is missing;
- dynamic/interface queries whose physical labels cannot be resolved to declarations.

## Fallback and idempotency

- **No declaration:** the provider keeps the ordinary AGE plan. Results are unchanged.
- **Declaration present, table missing:** the relation probe fails and the query falls back to the
  AGE path for that query. Nothing is created lazily.
- **Repeated `EnsureIndexesAsync`:** `CREATE ... IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`, and the
  backfill UPSERT make it idempotent. The report shows no new structures and refreshed rows.
- **Failed `EnsureIndexesAsync`:** the transaction rolls back; retry after fixing the cause.
- **`DropIndexesAsync`:** removes the recorded index table(s) and catalog table. It is idempotent
  and returns `StructuresRemoved == 0` on a second call. Queries immediately fall back to AGE.

## Breaking change and cleanup

The legacy `Status`/`Kaufpreis` physical naming compatibility was removed. This is intentional:
only proof-of-concept deployments used those names.

- A property literally named `Status` gets the same hashed `p_...` column as any other property.
- No `__status` table, `status_text` column, or `kaufpreis` column is created or read.
- There is no runtime migration shim and no fallback to old names.

For a preview deployment that still has the old objects, stop writers and remove them manually.
Run this once per affected graph, after replacing the old process with the generalized provider:

```sql
-- Inspect first, then drop only the objects that belong to the provider.
SELECT table_name FROM information_schema.tables
WHERE table_schema = 'billing' AND (table_name LIKE '%\_\_status' OR table_name LIKE '%\_\_num_agg');

DROP TABLE IF EXISTS "billing"."Contract__status" CASCADE;
DROP TABLE IF EXISTS "billing"."Contract__num_agg" CASCADE;
-- Drop the old trigger functions too; DROP TABLE ... CASCADE removes their triggers
-- but leaves the functions behind.
DROP FUNCTION IF EXISTS "billing"."Contract_num_agg_ins_fn"() CASCADE;
DROP FUNCTION IF EXISTS "billing"."Contract_num_agg_upd_fn"() CASCADE;
DROP FUNCTION IF EXISTS "billing"."Contract_num_agg_del_fn"() CASCADE;
```

If the old preview never created a `__cvoya_index` table, `DropIndexesAsync` drops the generalized
`<label>__idx` table through its generalized pre-catalog probe and then drops the catalog table. It
does not probe or remove the legacy names.

## Risks and verification checklist

Risks:

- extra storage and write amplification for each declared column;
- a maintenance transaction that holds the provisioning lock and backfills a large label;
- direct SQL writes or bulk imports bypass the provider and leave derived rows stale until the next
  `EnsureIndexesAsync`;
- hash names are opaque, so use the [quick-start catalog query](README.md#quick-start)
  (`SELECT * FROM "<graph>"."__cvoya_index";`), `information_schema`, and the detailed queries below
  when troubleshooting.

Verify:

```sql
-- The generalized table and column exist.
SELECT table_name FROM information_schema.tables
WHERE table_schema = 'billing' AND table_name = 'Contract__idx';

SELECT column_name FROM information_schema.columns
WHERE table_schema = 'billing' AND table_name = 'Contract__idx';

-- The catalog knows the declaration and the physical table.
SELECT kind, label, property, format_version, table_name
FROM "billing"."__cvoya_index" ORDER BY kind, label, property;

-- No compatibility objects exist.
SELECT table_name FROM information_schema.tables
WHERE table_schema = 'billing' AND (table_name LIKE '%\_\_status' OR table_name LIKE '%\_\_num_agg');
```

Then run the accelerated query and the same query with the companion table renamed away; both must
return identical rows. That fallback comparison is the strongest correctness check.
