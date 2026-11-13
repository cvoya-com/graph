# Declared aggregates

Declare an aggregate when a dashboard or report repeatedly computes `Count`, `Sum`, `Min`, `Max`,
or `Average` of one numeric property grouped by one text property of the same label. AGE would
otherwise scan and group the whole label table on every request.

## Example

```csharp
using Cvoya.Graph;
using Cvoya.Graph.Age;

[Node("Contract")]
public sealed record Contract : Node
{
    public string Name { get; set; } = string.Empty;

    public string? Status { get; set; }
    public long Kaufpreis { get; set; }
}

var registry = new SchemaRegistry();

// The type-safe builder resolves the label and both stored property names from
// [Node] and [Property(Label = "...")]; the group key becomes a text column automatically.
registry.Node<Contract>().AddPropertyAggregate(
    contract => contract.Status,
    contract => contract.Kaufpreis,
    AggregateFunction.Count | AggregateFunction.Sum | AggregateFunction.Min |
    AggregateFunction.Max | AggregateFunction.Average);

await using var store = new AgeGraphStore(connectionString, "billing", registry);
await store.CreateGraphIfNotExistsAsync(cancellationToken);

await store.Graph.CreateNodeAsync(
    new Contract { Name = "A-1", Status = "aktiv", Kaufpreis = 100 },
    cancellationToken: cancellationToken);
await store.Graph.CreateNodeAsync(
    new Contract { Name = "A-2", Status = "aktiv", Kaufpreis = 200 },
    cancellationToken: cancellationToken);

await store.EnsureIndexesAsync(cancellationToken);

var stats = await store.Graph.Nodes<Contract>()
    .GroupBy(contract => contract.Status)
    .Select(group => new
    {
        group.Key,
        Count = group.Count(),
        Total = group.Sum(contract => contract.Kaufpreis),
        Average = group.Average(contract => contract.Kaufpreis),
        Minimum = group.Min(contract => contract.Kaufpreis),
        Maximum = group.Max(contract => contract.Kaufpreis),
    })
    .ToListAsync(cancellationToken);
```

Only declare the functions you actually call. A function that was not declared keeps the ordinary
AGE path for that query, even when another function over the same group/value pair is declared.

The advanced string API remains available for dynamically composed declarations:
`registry.AddPropertyAggregate(new PropertyAggregateDefinition("Contract", "Status", "Kaufpreis", ...))`.
It expects the stored label and stored property names directly; the type-safe builder resolves them
for you.

## What changes in the database

Two groups of provider-owned objects are created inside the graph schema.

The existing per-label companion table receives the group and value columns:

```sql
-- Contract__idx (generalized, hashed column names)
"p_<group-key hash>" text,
"n_<value hash>"     numeric
```

A dedicated aggregate cache table is created for the declaration:

```sql
CREATE TABLE IF NOT EXISTS "billing"."Contract__agg_<16 hex>" (
    "group_key"  text PRIMARY KEY,
    "group_text" text,
    "cnt"        bigint NOT NULL DEFAULT 0,
    "sum_value"  numeric NOT NULL DEFAULT 0,
    "min_value"  numeric,
    "max_value"  numeric
);
```

The table is maintained by three functions and three triggers on the companion table:

- `..._ins_fn` + an `AFTER INSERT ... FOR EACH ROW` trigger: increments the group row;
- `..._upd_fn` + an `AFTER UPDATE ... FOR EACH STATEMENT` trigger: rebuilds affected groups from the
  old and new rows;
- `..._del_fn` + an `AFTER DELETE ... FOR EACH STATEMENT` trigger: deletes empty groups and rebuilds
  the rest.

Function and trigger names derive from the aggregate table name, so they are bounded and
deterministic. The group key uses `COALESCE(to_jsonb(group_column)::text, 'null')` because a primary
key cannot hold `NULL`. Real strings are JSON-quoted, so a null group stays distinct from every
string, including `"null"` and U+0001; `group_text` preserves the real nullable value for projection.

`EnsureIndexesAsync` clears and repopulates the cache table from the companion table before
re-creating the functions and triggers, then records an `aggregate` catalog row. A later pass drops
and re-creates the functions/triggers so a changed aggregate declaration cannot leave two trigger
sets that double-count rows.

## Scale-out and multiple clients

- `EnsureIndexesAsync` holds the graph-scoped advisory lock and runs in one transaction, so
  concurrent instances cannot both rebuild the triggers.
- The triggers fire for writes made by **any** provider client, not only the process that ran
  maintenance, because they live on the shared companion table. Keep the provider write path: it
  synchronizes the companion row in the same transaction as the node write.
- Aggregate triggers take transaction-scoped advisory locks for the affected group keys before
  reading the companion rows. Rebuilds acquire their keys in order and use fresh READ COMMITTED
  snapshots after waiting, so concurrent updates or deletes do not overwrite each other's totals
  with stale calculations. Lock namespaces include the graph and aggregate table.
- A client that has not run maintenance still has the declarations, but the aggregate relation probe
  fails and its queries fall back to AGE. It cannot read a half-built cache.
- If maintenance fails, the transaction rolls back; the previous cache and triggers remain. Investigate
  and retry.
- Direct SQL writes to the AGE label table bypass the companion table and the triggers. Re-run
  `EnsureIndexesAsync` to rebuild the cache after a bulk import.
- Do not run two different declaration sets for the same graph from different deployments during a
  rolling upgrade. Both may try to rebuild the same aggregate rows. Finish or stop the old
  deployment's maintenance first.
- Coordinate maintenance windows across instances and do not write through one instance while
  another instance is dropping the declared structures. Such a write can pass a stale cached
  positive and fail with a missing-relation error (SQLSTATE 42P01); the transaction rolls back
  atomically. The provider wraps the expected companion-table error in a `GraphException` with the
  label and retry guidance.
  An immediate retry can still hit the stale positive for up to the 5 s presence-cache
  TTL. Wait for the TTL, or run `EnsureIndexesAsync`/`DropIndexesAsync` on the writing store to
  clear the cache, then retry. Only positive answers are cached; see
  [the index presence cache](07-index-presence-cache.md).

## Accelerated query shapes

Accelerated:

- `GroupBy(entity => entity.GroupKey)` with `Count`, `Sum`, `Min`, `Max`, or `Average` over the
  declared numeric value;
- an equality filter on the group key combined with a declared aggregate;
- label hierarchies where every label in the query has the same declared group/value pair.

Not accelerated:

- an aggregate function that was not declared for the pair;
- a non-numeric value property, or a group key that is not stored as text;
- a different group key or value property;
- a query whose physical labels or declarations cannot be resolved;
- a missing aggregate table or trigger set (the relation probe falls back).

## Fallback and idempotency

- **No declaration:** ordinary AGE grouping and aggregation; identical results.
- **Declaration without maintenance:** no cache table exists; the probe fails and the query falls
  back.
- **Repeated `EnsureIndexesAsync`:** `CREATE TABLE IF NOT EXISTS`, the clear/repopulate, and
  `CREATE OR REPLACE FUNCTION` make the pass idempotent. The report does not count an existing cache
  table as new.
- **`DropIndexesAsync`:** drops the recorded aggregate functions and table, then the companion
  table. It is idempotent and leaves ordinary reads/writes working.
- **Partial failure:** maintenance is one transaction; retry from a clean state.

## Breaking change and cleanup

Aggregates previously used `Status`/`Kaufpreis`-specific names (`__num_agg`, `status_text`,
`*_num_agg_*_fn/_trg`). Those names are gone:

- the aggregate table is always `<label>__agg_<16 hex>` (bounded to `agg_<16 hex>`);
- the group column is `group_key`, the projected text column is `group_text`;
- properties literally named `Status` and `Kaufpreis` use the same generalized names as any other
  property;
- no `__num_agg` table or `status_text`/`kaufpreis` column is created, read, or probed, and there is
  no runtime fallback.

For a preview deployment with old objects, stop writers and remove them manually. The legacy
aggregate triggers live on the `<label>__status` equality-index table, not on the aggregate table.
Drop that table before the aggregate objects, then drop the functions:

```sql
-- The aggregate triggers live on the legacy "<label>__status" table, not on
-- "<label>__num_agg"; dropping the status table removes them.
DROP TABLE IF EXISTS "billing"."Contract__status" CASCADE;
-- The aggregate table only holds the cached aggregate rows.
DROP TABLE IF EXISTS "billing"."Contract__num_agg" CASCADE;
-- DROP FUNCTION ... CASCADE removes any dependent trigger that is still present.
DROP FUNCTION IF EXISTS "billing"."Contract_num_agg_ins_fn"() CASCADE;
DROP FUNCTION IF EXISTS "billing"."Contract_num_agg_upd_fn"() CASCADE;
DROP FUNCTION IF EXISTS "billing"."Contract_num_agg_del_fn"() CASCADE;
```

Then run `EnsureIndexesAsync` once with the new provider to build the generalized cache.

## Risks and verification checklist

Risks:

- trigger overhead on every insert/update/delete of the companion table;
- a large first backfill inside the maintenance transaction;
- a stale cache after direct SQL writes or a bulk import;
- contention on the group lock when many clients write to the same aggregate group.

Verify:

```sql
-- Generalized aggregate table and its trigger functions.
SELECT table_name FROM information_schema.tables
WHERE table_schema = 'billing' AND table_name LIKE 'Contract\_\_agg\_%';

SELECT proname FROM pg_proc
JOIN pg_namespace ON pg_namespace.oid = pg_proc.pronamespace
WHERE nspname = 'billing' AND proname LIKE 'Contract__agg_%\_fn';

-- Catalog row for the declaration.
SELECT kind, label, property, aux_property, table_name
FROM "billing"."__cvoya_index" WHERE kind = 'aggregate';
```

Then compare the declared aggregate query with the same query on a store with no declarations. The
results must match row for row. Exercise create, update of the value, update of the group key, and
delete, and re-check `Count`, `Sum`, `Min`, `Max`, and `Average` after each step.
