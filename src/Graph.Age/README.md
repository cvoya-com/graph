**Downloadable open-source computer software from [CVOYA](https://cvoya.com).** See the
[CVOYA software catalog](https://cvoya.com/software).

# Cvoya.Graph.Age

`Cvoya.Graph.Age` is the PostgreSQL + Apache AGE provider for CVOYA graph. It uses the shared
`Cvoya.Graph.Cypher` dialect SPI and exposes the same typed graph, LINQ, serialization, and
transaction APIs as the other providers.

## Install

```bash
dotnet add package Cvoya.Graph.Age
```

The PostgreSQL database must have the Apache AGE extension installed. The provider loads AGE and
sets the required `ag_catalog` search path on every pooled connection.

## Connect

<!-- checked-snippet: examples/Playground/Documentation/UsingDirectives.cs#age-usings; examples/Playground/DocumentationSnippets.cs#age-quick-start -->
```csharp
using Cvoya.Graph;
using Cvoya.Graph.Age;

await using var store = new AgeGraphStore(
    "Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres",
    graphName: "my_graph");

await store.CreateGraphIfNotExistsAsync();
var graph = store.Graph;

var activeUsers = await graph.Nodes<User>()
    .Where(user => user.IsActive)
    .ToListAsync();
```

Production applications should supply the connection string directly or through
`AGE_CONNECTION_STRING`; the provider deliberately has no fallback credentials. `AGE_GRAPH`
selects the default graph name when the constructor does not specify one.

An application that already owns its connection pool can pass an AGE-enabled
`NpgsqlDataSource`. In that form, disposing the store does not dispose the caller's data source.

## Supported surface

- typed and dynamic node/relationship CRUD;
- shared LINQ-to-Cypher translation and streaming queries;
- ACID transactions on a dedicated PostgreSQL connection;
- atomic endpoint–relationship–endpoint creation in one Npgsql batch round-trip, including nested
  complex-property subtrees and create-missing endpoint semantics;
- complex-property persistence and cascading cleanup;
- native logical AGE labels/types for model roots and catalog-vetted raw native data;
- runtime label-hierarchy registration and first-use validation for FormConfig-driven labels;
- write-scoped native label/type provisioning (ordinary reads perform no DDL);
- scalar-key grouped aggregation through AGE-native `WITH` grouping and aggregate functions;
- correlated collection projections and relationship/complex-collection counts, lowered to
  AGE-supported grouped matches (see below); and
- agtype adaptation for vertices, edges, paths, maps, arrays, large integers, decimals, and
  ISO-8601 temporal values.

The provider declares full-text search, relationship predicates, and set operations. It does not
declare nested transactions, whole-entity ordering, or `GraphCapability.ShortestPath`. Shortest
path is deliberately unsupported for v1.0; the capability-gated TCK test skips for AGE, and #355
tracks any future implementation. Unsupported operations fail during translation or are skipped
only when a compatibility test is explicitly gated by the missing capability.

### Native storage and commands

New `Person` nodes are stored in AGE's `Person` label table and new `KNOWS` relationships use the
native `KNOWS` type. Concrete raw AGE rows need no CVOYA discriminator, inheritance, entity-kind, or
CLR metadata to participate in typed, dynamic, and traversal queries. Provider-owned complex-value
nodes remain isolated through their relationship marker and reserved physical storage.

AGE catalog label names are limited to plain symbolic names. A mapped name outside that grammar
(for example, a label containing a space) uses the provider-reserved physical-table representation
so the escaped-identifier API remains usable; reserved provider names are rejected before writing.

Set updates and deletes first freeze distinct `id(n)` / `id(r)` values inside the active write
transaction, then mutate only those graphids. Endpoint-intent relationship creation likewise joins
selected or newly created endpoints by graphid and supports selected/selected, hybrid, all-new, and
explicit self-loop shapes. Caller-owned multi-statement commands are isolated behind a savepoint.
The public set-based mutation and endpoint-intent APIs use these native command paths without
exposing graphids.

### Runtime label hierarchies and startup validation

Typed `[Node]` inheritance is discovered automatically from the loaded assemblies. Labels created
at runtime have no CLR type, so the provider cannot discover them. Register every physical child
label that can store a derived instance with
`AgeGraphStore.RegisterLabelHierarchy(parentLabel, childLabel)` before the first query. A base-label query expands only over the
registered hierarchy, so an unregistered physical label stays invisible even though its rows declare
the base label in `inheritance_labels`.

The provider checks the AGE catalog and the `inheritance_labels` of physical labels on the first
graph operation (the explicit graph creation or the first read/write transaction). The behaviour is
controlled by `AgeGraphStore.LabelHierarchyValidation`:

- `Warn` (the default) logs one warning per unregistered physical label and continues.
- `Fail` throws `GraphException` before the operation can commit.
- `Off` disables the check.

`FindUnregisteredLabelHierarchyAsync` returns the same findings without changing the registry, so an
application can report them at startup. Registration is process-wide and additive; a registration
made after the first operation is honoured by every subsequent query, but it does not re-run the
automatic validation. Call `FindUnregisteredLabelHierarchyAsync` again, or restart the process, when
labels can appear after startup.

Detection uses the `inheritance_labels` property that the provider writes for nodes with multiple
logical labels. A physical label whose rows do not declare the base label cannot be attributed to a
hierarchy and is therefore not reported; such rows need their `inheritance_labels` fixed before the
provider can include them in a base-label query. The provider deliberately does not auto-register
discovered labels: a label name is data, and letting database contents silently extend the query
surface would make the type model depend on what happens to be stored.

### Scalar-key grouped aggregation

The shared planner represents scalar-key `GroupBy` as a structured `WITH` stage: the non-aggregate
key establishes AGE's implicit grouping key, while `Count`, `Sum`, `Average`, `Min`, and `Max`
become aggregate columns consumed by the final projection. Key-only projections use `WITH DISTINCT`.
The existing AGE AST passes then lower labels, reserved aliases, and empty sums without inspecting or
rewriting rendered Cypher. The supported and rejected shapes therefore match the provider-neutral
`GroupByAggregation` contract; unsupported entity keys, element selectors, collection projections,
and filters inside a group fail before database execution with the shared actionable diagnostic.

### Label-hierarchy expansion and ordered paging

A query on a base or intermediate label expands to one single-label `UNION ALL` branch per
physical label table of the hierarchy. AGE attaches `ORDER BY`/`SKIP`/`LIMIT` after a `UNION` to
the final branch instead of the combined result, so global paging and sorting are lifted into a
structured SQL wrapper around `cypher()`: each branch is bounded to `skip + limit` rows (letting a
B-tree index on the ordering property serve the per-branch top-N) and the wrapper applies the final
order, offset, and limit. `DISTINCT` projections become `SELECT DISTINCT` over the union unless an
ordering forces a distinct union, and grouped `Count`/`Sum` results merge per-branch partials with
`sum`. Ordering by a property over a large hierarchy needs a B-tree expression index on the AGE
accessor to still be fast; without one the union still avoids the former all-table scan, but
PostgreSQL sorts every hierarchy row. `AgeQueryRunner` composes this wrapper from validated
identifiers and integer paging values; it never edits rendered Cypher text.

### Correlated collections and pattern counts

Apache AGE 1.7 cannot execute Cypher pattern comprehensions or `CALL {}` subqueries, and it parses
`EXISTS { ... }` / `COUNT { ... }` pattern subqueries but silently matches nothing. The provider
implements the corresponding shared capabilities through an AGE-local structured AST pass before
rendering. Correlated collection queries match the traversal once and group by its source node,
using `collect`, conditional `count`, ordinary aggregates, and ordered/nested `WITH` stages as
required; a filter inside one projection (`group.Where(...).Average(...)`) becomes conditional
aggregation so it cannot narrow the rows sibling projections aggregate over, and a segment filter
becomes a grouped row-count guard so owners with no qualifying rows produce no result row.
Independent relationship-degree and complex-collection counts — in projections, orderings, and
`Where` filters (including `.Any(...)` existence checks) — use sequential `OPTIONAL MATCH` stages so
owners with no match return zero and multiple counts in one projection do not multiply each other.
Two correlated shapes have no equivalent staging and fail at translation time instead: a filtered
nested grouping and multiple ordered collections in one projection. The resulting nested lists and
maps continue through the provider-neutral result wire model and shared materializer.

### Full-text search

AGE full-text search discovers the graph's concrete vertex/edge tables from the AGE catalog, searches
native logical storage and externally managed labels, then correlates the combined distinct result
through transaction-local `id(n)` / `id(r)` values. The provider-reserved `CvoyaNode` and
`CvoyaRelationship` tables are excluded from external root discovery. Graphids remain provider
plumbing and are never substituted for public `Id` data; a domain property named `Id` participates
like any other included string property.

Registered labels search exactly their included string properties, and a registered type with no
included property matches nothing. Global searches use the all-string fallback only for genuinely
unregistered external labels; dynamic entities retain their public all-string-value contract.
Ordinary reads inline the PostgreSQL text-search predicate and require no managed function, index,
or DDL permission. The final native AGE provider owns no index artifacts, so
`RecreateManagedIndexesAsync` is a successful no-op: it performs no discovery, function creation,
index creation, drop, rebuild, or graph provisioning. Existing PostgreSQL artifacts are untouched.
Missing-property filters benefit from a caller-owned partial index; the SQL recipe and measured
gate are documented in the performance guide (`docs/performance.md`).

## Local AGE

From the repository root:

```bash
scripts/containers/start-age.sh
export AGE_CONNECTION_STRING='Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres'
dotnet test tests/Graph.Age.Tests/Graph.Age.Tests.csproj --configuration Debug
```

The implementation incorporates and substantially reworks Apache AGE provider ideas from
[@paule96's PR #66](https://github.com/cvoya-com/graph/pull/66). In particular, the connection
bootstrap, agtype conversion, SQL envelope, transaction, serialization, and CRUD designs informed
this provider; the dialect-SPI implementation and security hardening were performed as part of
issue #86.
