# Ordering declarations and paging

`PropertyIndexKind.Ordering` is a **plan gate**, not a request to create a database index. Declare it
together with an equality declaration for the same string property when you page a label hierarchy
with `OrderBy(...).Skip(...).Take(...)`. The paging plan then orders through the declared equality
companion instead of loading and sorting full entities.

## Example

```csharp
using Cvoya.Graph;
using Cvoya.Graph.Age;

[Node("Document")]
public sealed record Document : Node
{
    public string Name { get; set; } = string.Empty;
    public string? Body { get; set; }
    public DocumentAddress? Address { get; set; }
}

public sealed class DocumentAddress
{
    public string City { get; set; } = string.Empty;
}

[Node("DocumentChild")]
public sealed record DocumentChild : Document;

var registry = new SchemaRegistry();
registry.Node<Document>()
    .AddPropertyIndex(document => document.Name)                                  // physical text column
    .AddPropertyIndex(document => document.Name, PropertyIndexKind.Ordering);     // paging gate
registry.Node<DocumentChild>()
    .AddPropertyIndex(document => document.Name)
    .AddPropertyIndex(document => document.Name, PropertyIndexKind.Ordering);

await using var store = new AgeGraphStore(connectionString, "documents", registry);
await store.CreateGraphIfNotExistsAsync(cancellationToken);
await store.EnsureIndexesAsync(cancellationToken);

// Accelerated page: the sort runs on the companion table before complex properties are loaded.
var page = await store.Graph.Nodes<Document>()
    .OrderBy(document => document.Name)
    .Skip(200)
    .Take(100)
    .ToListAsync(cancellationToken);
```

The label hierarchy matters. The paging plan requires an equality + ordering declaration for **every
label** the query can return. If any label lacks either declaration, the whole query keeps the
ordinary accessor ordering. Declare the child label explicitly, as above, even though it inherits
the property.

## What changes in the database

An ordering declaration creates **no structure of its own**. It adds one catalog declaration and
makes the existing equality companion eligible for the page plan:

- the `<label>__idx` table and its hashed `p_<16 hex>` text column come from the equality
  declaration (see [declared equality indexes](01-declared-equality-indexes.md));
- the B-tree `idx_<16 hex>_idx` on that column is the actual ordering access path;
- the `__cvoya_index` catalog records the ordering declaration so a provider restart can rebuild
  the same plan.

An ordering-only declaration materializes nothing. It still counts as an inspected definition in
maintenance reports.

## Scale-out and multiple clients

Ordering declarations participate in the same maintenance lock and presence probe as equality
declarations:

- `EnsureIndexesAsync` serializes across application instances with the graph-scoped advisory lock;
- a client that has the declarations but has not run maintenance cannot use the plan because the
  companion relation probe fails; it falls back to the AGE path;
- a declaration added after maintenance is not effective until the next `EnsureIndexesAsync`, even
  if the equality table already exists;
- no extra DDL, lock, or backfill is performed for the ordering declaration itself;
- coordinate maintenance windows across instances and do not write through one instance while
  another instance is dropping the declared structures. Such a write can pass a stale cached
  positive and fail with a missing-relation error (SQLSTATE 42P01); the transaction rolls back
  atomically. The provider wraps the expected companion-table error in a `GraphException` with the
  label and retry guidance.
  An immediate retry can still hit the stale positive for up to the 5 s presence-cache
  TTL. Wait for the TTL, or run `EnsureIndexesAsync`/`DropIndexesAsync` on the writing store to
  clear the cache, then retry. Only positive answers are cached; see
  [the index presence cache](07-index-presence-cache.md).

Operators adding ordering for an existing graph must still run `EnsureIndexesAsync`, because the
plan checks both declarations and physical presence.

## Accelerated query shapes

Accelerated:

- `OrderBy(entity => entity.StringProperty).Skip(n).Take(m)` on a label hierarchy where every label
  declares both `Equality` and `Ordering` for the sort property;
- the clause-level paging form where the provider can move `OFFSET`/`LIMIT` into the physical plan;
- complex-property read shapes where the page must be selected before nested values are hydrated.

Not accelerated:

- ordering by a property without an equality companion;
- ordering by a non-string property;
- a filtered page when the provider cannot push the filter through the same source shape;
- any label in the hierarchy missing either declaration;
- dynamic/interface queries whose labels do not resolve to declarations.

These keep the ordinary AGE ordering and return the same rows; only the access path differs.

## Fallback and idempotency

- **No ordering declaration:** the query keeps accessor ordering and sorts in the ordinary path.
- **Ordering only, no equality companion:** the plan is not eligible; no storage exists to use.
- **Missing companion table:** the relation probe fails and the query falls back.
- **Repeated `EnsureIndexesAsync`:** adds no ordering structures and refreshes equality rows.
- **`DropIndexesAsync`:** removes the equality companion; the ordering declaration remains in the
  registry but the plan falls back until maintenance runs again.
- **Failed maintenance:** rolls back; the previous structures and plans remain valid.

## Breaking change and cleanup

Ordering declarations never used legacy `Status`/`Kaufpreis` names. The breaking change is in the
companion table they point at: it is always the generalized `<label>__idx` table. If a preview
deployment still has a `__status` table, remove it manually as described in
[declared equality indexes](01-declared-equality-indexes.md#breaking-change-and-cleanup). There is no
runtime fallback to the old name.

## Risks and verification checklist

Risks:

- declaring ordering without equality gives a false sense of acceleration (the gate passes, but
  there is no access path);
- declaring only the base label silently disables paging for child labels;
- a stale catalog can make an application believe paging is enabled while the relation probe
  correctly falls back; monitor `AgeSqlPlan.Kind == "page"` or query latency.

Verify:

```sql
-- The equality companion and its B-tree exist.
SELECT indexname FROM pg_indexes
WHERE schemaname = 'documents' AND tablename = 'Document__idx';

-- The catalog records both kinds.
SELECT kind, label, property FROM "documents"."__cvoya_index"
WHERE label = 'Document' AND property = 'Name' ORDER BY kind;
```

Then compare a paged query against the same query with `EnsureIndexesAsync` never run (or after
`DropIndexesAsync`): both must return the same page in the same order. If the accelerated path is
selected, the generated SQL contains the companion table and a parameterized `ORDER BY` on its
hashed column rather than a complex-property load before paging.
