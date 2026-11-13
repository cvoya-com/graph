# Native ID lists and AGE versions

The AGE provider uses provider-native graph ids internally to connect query results, full-text
search matches, and selected relationship endpoints. Those ids never become a public entity
property, but the SQL shape that filters by a list of them depends on the AGE server version.
This feature documents how the provider keeps that shape portable.

## Example

```csharp
using Cvoya.Graph;
using Cvoya.Graph.Age;

[Node("Document")]
public sealed record Document : Node
{
    public string Tenant { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

await using var store = new AgeGraphStore(connectionString, "documents");
await store.CreateGraphIfNotExistsAsync(cancellationToken);

var matches = await store.Graph.Nodes<Document>()
    .Search("apache age")
    .Where(document => document.Tenant == "acme")
    .OrderBy(document => document.Title)
    .Take(50)
    .ToListAsync(cancellationToken);
```

`Search` first resolves matching native graph ids on the server, then rewrites the full-text call
into an internal native-id membership predicate. No public `Id` property takes part in correlation.
The same lowering is used when a command selects relationship endpoints by a set of native ids.

## What changes in the database

This feature creates **no tables, columns, functions, triggers, or catalog rows**. It only changes
the generated SQL predicate:

- on AGE 1.7, the server accepts `id(alias) IN <parameter>` directly, so the provider keeps the
  native predicate;
- on AGE 1.8, `id()` returns `graphid` while a list parameter is `agtype`, and the native form fails
  with `XX000: not a common type` (it can also crash the backend), so the provider emits
  `id(alias)::agtype IN <parameter>`;
- a predicate that would otherwise need several native-id aliases is rewritten so the list is
  evaluated once, not once per row per id;
- an empty native-id list folds to `false` without an `UNWIND`;
- a native-id membership inside `OPTIONAL MATCH` fails closed on AGE 1.8 because an `UNWIND` would
  drop the optional null row and the comprehension cannot be typed there.

The store probes the AGE extension version once per store instance. The probe is a read-only
`SELECT`; it does not need a maintenance window or a lock.

## Scale-out and multiple clients

- The version probe is per store instance and cached. Every application instance performs it once
  on first use; no shared state or DDL is involved.
- All clients targeting the same AGE server version generate the same predicate shape. Mixing AGE
  1.7 and 1.8 clients against different servers is safe; mixing them against the same server is a
  deployment error unrelated to this feature.
- A server upgrade is detected by new store instances. Recycle long-lived stores after an AGE
  version change so the cached probe matches the server.
- No locks, catalog rows, or background work are involved, so no operator action is required.

## Accelerated query shapes

The rewrite is about correctness and avoiding row multiplication, not about an index.

Handled efficiently:

- full-text `Search` followed by filters/projections on typed or dynamic nodes;
- filtering one native-id alias by a parameter list;
- a multi-alias predicate where the same list applies to both relationship endpoints;
- literal and parameterized id lists;
- an empty id list.

Not handled / fails closed:

- native-id membership inside `OPTIONAL MATCH` on AGE 1.8 raises
  `GraphQueryTranslationException`; filter the ids before the optional match;
- an id list with a non-integer element fails closed during lowering;
- a non-id list parameter used in the internal native-id position fails closed;
- a shape the provider cannot prove equivalent to the list membership is not rewritten.

## Fallback and idempotency

- **AGE 1.7:** native `IN`; no rewrite beyond the shared planner.
- **AGE 1.8:** casted `IN`; identical rows and a bounded cost.
- **Repeated execution:** deterministic SQL; no state changes.
- **Failed lowering:** throws instead of producing a query that could return the cross product.
- **No runtime fallback to an older row-preserving comprehension:** that form was removed because
  it was O(rows x ids) and produced a 1,000,000-row cross product in the reference shape.

## Breaking change and cleanup

This feature has no `Status`/`Kaufpreis` physical names and creates no objects to clean up. The
general breaking change applies to declared index and aggregate objects; see
[declared equality indexes](01-declared-equality-indexes.md#breaking-change-and-cleanup). No legacy
native-id compatibility mode or migration shim exists.

## Risks and verification checklist

Risks:

- very large id lists increase parameter size and plan time; `Search` fails informatively above
  `10_000` combined matches rather than build an unbounded parameter, so narrow the search;
- parameterized lists are slower than literals (roughly 10 ms vs 1-2 ms for a 1,000-id, two-alias
  shape on the reference containers), so avoid unnecessary parameter indirection in hot paths;
- native ids are not portable across graph exports/imports and must never be persisted as business
  data;
- an `OPTIONAL MATCH` with native ids fails closed; restructure the query.

Verify:

```sql
-- Confirm the server version the store will detect.
SELECT extversion FROM pg_extension WHERE extname = 'age';
```

Then run the same `Search` query on AGE 1.7 and 1.8 and compare the row set and row count. On 1.8,
enable statement logging and confirm the generated SQL uses `id(alias)::agtype IN` and one list
parameter, not a per-row `UNWIND` cross product. Also run an empty search result and confirm it
returns zero rows without error.
