# Cvoya.Graph.Age compatibility compliance report

| | |
|---|---|
| Provider | `Cvoya.Graph.Age` (issue #86 implementation) |
| Compliance suite | Repository build (`Cvoya.Graph.CompatibilityTests` 0.0.0-dev) |
| Candidate source | `90b9699` |
| Backing store | Apache AGE 1.8.0 / PostgreSQL 18.6 (Debian 18.6-1.pgdg13+2) (`apache/age:release_PG18_1.8.0`) as the primary store; Apache AGE 1.7.0 / PostgreSQL 18.1 (Debian 18.1-1.pgdg13+2) (`apache/age:release_PG18_1.7.0`) also validated |
| Reference provider | Neo4j 5.26.28 Enterprise (`neo4j:5-enterprise`, image `037e96a1ddb9581ba8dc420a03dbced3ffe303b7f126628dfa477566481786f0`) |
| Date / run | 2026-11-09 / local NUL-89 native shortest-path verification |
| Retained evidence root | `artifacts/compliance/NUL-89/` (local, gitignored) |

## Certified v1.0 boundary

| Area | Certified contract |
|---|---|
| Identity | `IEntity` has no universal identity member. Domain keys are optional model properties, and a property named `Id` has no provider-defined semantics. |
| Native storage | User roots use mapped AGE labels/types; typed and dynamic reads include raw externally seeded native graph data. |
| Physical correlation | AGE graphids remain private query/write correlation values and never appear as public entity identity or property-bag metadata. |
| Mutations and endpoints | Set mutations freeze selected graphids inside one transaction. Relationship creation preserves exactly-one endpoint intent, including value-equal endpoints and explicit self-loops. |
| Paths | Path segments preserve relationship orientation relative to traversal direction without exposing endpoint IDs. |
| Queries | Relationship predicates, set operations, query expressions and terminals, grouping, optional traversal, and supported projections execute through structured lowering. |
| Collections and serialization | Nullable simple collections, nested complex collections, provider-neutral serialization, and transaction-cleanup contracts are included in the strict suite. |
| Search and provisioning | Full-text search is function-free and correlates private graphids; read-only use performs no provisioning. |
| Compatibility | The v1.0 provider is native-only. Retired universal roots, endpoint-ID contracts, mixed storage, and pre-v1 database compatibility are outside the supported boundary. |

## Declared capabilities

| Capability | Declared | Note |
|---|---|---|
| FullTextSearch | Yes | PostgreSQL discovers native/external tables; residual Cypher correlates private graphids. |
| Transactions | Yes | One PostgreSQL connection and transaction per graph transaction. |
| NestedTransactions | No | |
| ComplexPropertyCascade | Yes | Owned value nodes are deleted transactionally. |
| CallSubqueries | Yes | Correlated collection shapes are lowered to grouped matches and projections. |
| PatternSizeProjection | Yes | Pattern counts are lowered to sequential optional matches and grouped counts. |
| MultiLabelMatch | Yes | An AGE AST pass lowers logical inheritance labels to AGE-compatible predicates. |
| LabelFiltering | Yes | Caller-supplied labels lower to escaped literal membership tests over physical and logical inheritance labels. |
| OrderByEntity | No | Scalar property ordering is supported; bare whole-entity ordering is rejected rather than rewritten to public `Id`. |
| ShortestPath | Yes on AGE >= 1.8.0 | Lowered to the native `ag_catalog.age_shortest_path` / `ag_catalog.age_all_shortest_paths` functions. The capability is absent on AGE 1.7.0, which has neither function. |
| OptionalTraversal | Yes | Optional matches are lowered while preserving owners with absent paths. |
| GroupByAggregation | Yes | The shared structured `WITH` plan uses AGE-native grouping and aggregate functions. |
| RelationshipPredicates | Yes | Per-hop predicates use indexed list filtering; anchored existence uses optional-match count stages. |
| SetOperations | Yes | Every nested `Union`/`Concat` branch runs through the complete AGE lowering pipeline with disjoint parameters and normalized projection aliases. |
| NullElementsInSimpleCollections | Yes | Typed storage metadata preserves element type and null positions without entering public property bags. |

## Structured Cypher lowering

After the shared planner produces a `CypherStatement`, the AGE query adapter recursively lowers every
`SetOperationClause` branch, including nested and chained trees, through the same ordered
`CypherPassRunner` before rendering. Branch parameter namespaces remain disjoint; temporal parameters
added during lowering use the first free provider-local name. `AgeRelationshipPredicatePass` replaces
AGE-unsupported universal relationship predicates with indexed list filtering so every expanded hop
must satisfy the caller predicate.

`AgeCorrelatedProjectionPass` replaces AGE-unsupported pattern
comprehensions and `CALL {}` clauses with one correlated match plus grouped `collect`/aggregate
projections — per-projection filters become conditional aggregation so one filtered projection
cannot narrow its siblings, and the anchoring existence filter becomes a grouped row-count guard;
independent pattern counts and `EXISTS`/`COUNT` predicates in `WHERE` become sequential optional
matches, preserving zero-count owners without multiplying sibling counts (AGE parses `EXISTS { }`
and `COUNT { }` but silently matches nothing, so nothing is left for the renderer to emit natively).
Two correlated shapes have no equivalent staging and are rejected at translation time: a filtered
nested grouping and multiple ordered collections in one projection. `AgeLabelPatternPass` then removes node labels and
relationship types from those and other match patterns and adds native `type()` predicates for
relationships and a GIN-friendly `properties @> { inheritance_labels: [...] }` containment predicate
for nodes. Every written vertex carries its full logical label chain in `inheritance_labels`, so the
node fallback does not need a non-indexable `labels()` disjunction; provider-owned complex values are
still excluded by the marker guard;
`AgeClauseOrderPass` moves ordering and paging without parsing rendered Cypher (including the
path-decomposition and aggregate exceptions), while
`AgeTemporalParameterArithmeticPass` unwraps AGE-unsupported temporal constructors, folds
parameter-only duration arithmetic into bound values, and shifts stored temporal arithmetic across
comparisons into the opposing bound. `AgeEntityProjectionPass` expands entity
hydration into typed match, predicate, projection, and ordering clauses, then structurally lowers
named optional paths, `ALL`/`reduce` compatibility, temporal members, string containment, path
indexes, reserved aliases, and empty sums. `AgeInlineComplexPropertyProjectionPass` expands inline
node hydration into typed optional matches, list comprehensions, and collection clauses before the
entity pass applies AGE compatibility lowering. `AgeSetOperationProjectionPass` assigns every leaf
projection one compatible alias shape before rendering. `AgeUnionAllExpansionPass` expands a
logical label hierarchy into one single-label branch per physical label. When the statement also
carries global ordering, paging, `DISTINCT`, or a mergeable aggregate, AGE cannot express the
global clause after a `UNION` (it attaches `ORDER BY`/`SKIP`/`LIMIT` to the final branch) and
cannot execute a `CALL { }` wrapper. The pass therefore lifts that row shaping into
`CypherStatement.PostProcessing`, and `AgeQueryRunner` applies it as a structured SQL wrapper
around `cypher()`: the wrapper orders and pages the combined rows, deduplicates with
`SELECT DISTINCT` when no ordering is present, and merges per-branch partials. `count`/`sum`
merge with `sum`, `average` merges a partial sum with a partial non-null count, and `min`/`max`
merge with AGE's type-aware `age_min`/`age_max` comparison aggregates. A complex-property
projection whose `collect` stage precedes the paging tail instead receives a per-branch
`skip + limit` page before the property match, so only the candidate page is grouped. No rendered
Cypher is textually rewritten; the wrapper only composes validated identifiers, sort columns, and
integer paging values around the rendered statement.

Scalar-key grouped aggregation needs no AGE-specific grouping pass: the shared planner emits a
structured `WITH` stage whose non-aggregate key establishes AGE's implicit group, with aggregate
columns for `Count`, `Sum`, `Average`, `Min`, and `Max`; key-only projections use `WITH DISTINCT`.
The normal AGE label, alias, and empty-sum passes operate on that AST before rendering. When the
grouped node type has registered physical subtypes, the same `AgeUnionAllExpansionPass` expands
the grouping into one branch per label table and merges the partial aggregates; `collect`,
percentile, and standard-deviation aggregates retain the containment fallback because their
partial values are not losslessly mergeable without additional type metadata. Unsupported
grouping shapes retain the provider-neutral validation message and fail before execution.

## Atomic subgraph creation

`CreateAsync(source, relationship, target, options)` sends its existence and schema-uniqueness
probes, conditional endpoint writes, breadth-first complex-property levels, relationship write, and
transient-marker cleanup through one `NpgsqlBatch.ExecuteReaderAsync` call. Value-node and
complex-property relationship IDs are assigned before execution, so no batch command depends on a
client read from an earlier result set. Create-only mode gates both roots together; create-missing
mode gates each root independently and never updates or extends a matched endpoint. Failures found
in later result sets roll back the provider-owned transaction or the operation savepoint inside a
caller-owned transaction. This shape is covered against the pinned Apache AGE 1.7.0 / PostgreSQL 18
runtime in both endpoint modes, with nested complex properties on both endpoints and an internal
execution-boundary counter asserting one batch round-trip.

## Native storage and commands

New user roots use their mapped AGE label/type. The label-lowering pass matches either native
`labels()` / `type()` or the provider-reserved representation for mapped names outside AGE's native
grammar, while concrete external rows need no CVOYA metadata. Graph creation provisions only the
graph; an authorized write discovers or creates only the native label table it needs under the
graph-scoped advisory lock. Read transactions verify graph existence and perform no DDL.

The packaged provider-boundary contract seeds a keyless node/relationship fixture through native
AGE commands and reads it back through typed nodes, dynamic nodes, typed/dynamic relationship
roots, and oriented path segments. Exact public property bags exclude graphids and provider marker
metadata. A before/after inventory of graph labels, indexes, constraints, and graph-schema
functions proves the read path creates no store artifacts.

Full-text discovery covers native logical and genuinely external tables and excludes the reserved
`CvoyaNode` / `CvoyaRelationship` tables from root search. Registered labels use their declared
included properties; the global all-string fallback applies only to unregistered external labels.
Search uses function-free SQL and correlates native graphids without exposing them as model data.

Set mutations select, deduplicate, and freeze native graphids before writes. Node/relationship
updates and deletes, uniqueness self-exclusion, and exact endpoint-intent creation use those graphids
inside one transaction. All-new endpoint creation distinguishes value-equal nodes from an explicit
self-loop, and caller-owned multi-statement operations roll back to an operation savepoint on any
validation, cancellation, or database failure.

## AGE 1.8 native-identity compatibility

Apache AGE 1.8.0 / PostgreSQL 18.6 segfaults when `id()` is the left operand of `IN`, for
example `MATCH (n) WHERE id(n) IN $ids`. Non-identity list membership (`node.Tags IN $list`,
`... IN $relationshipTypes`) is safe on the same build, so the defect is the native graphid
operand rather than list parameters as a class. The crash terminates the backend with
`signal 11`; follow-up statements fail with `57P03` until recovery completes.

The provider now never emits `id(...) IN ...`:

- `AgeDialect.RenderCollectionContains` renders native-identity membership as
  `size([item IN $list WHERE item = id(alias)]) > 0`. Stored-collection membership keeps the
  shared null-aware `toString()` form because AGE 1.7.0 does not match an agtype null parameter
  with `IS NULL`, while AGE 1.8.0 does.
- `ExpressionToCypherAstLowerer` routes `NativeElementIdentity` through the dialect's
  collection-membership hook even when the collection is a parameter, so the full-text search
  graphid filter cannot bypass the dialect.
- `ComplexPropertyManager` replaces `id(parent) IN $parentGraphIds` with a list-comprehension
  membership test for complex-collection replacement and deletion.
- `CypherEngine.ValidateUnselectedConstraintsAsync` replaces
  `NOT (id(candidate) IN $selectedIds)` with
  `size([selectedId IN $selectedIds WHERE id(candidate) = selectedId]) = 0`.

The list-comprehension form preserves empty-list, deduplication, counting, ordering, and
transaction semantics. Regression coverage lives in `AgeDialectTests`,
`AgeFullTextSearchRewriterTests`, and `AgeListParameterRegressionTests`.

## AGE 1.8 native shortest paths

Apache AGE 1.8.0 exposes `ag_catalog.age_shortest_path` and `ag_catalog.age_all_shortest_paths`,
but it still rejects the Cypher `shortestPath(...)` / `allShortestPaths(...)` pattern syntax. The
provider therefore never emits that syntax. On the first connection it reads the installed
`pg_extension.extversion`, caches the matching per-store dialect, and advertises
`GraphCapability.ShortestPath` only for AGE 1.8.0 or later.

For a shortest-path query the provider plans a normal source/target node-pair match, applies the
root and endpoint predicates plus `src <> tgt` before the function call, and projects one native
path value per row:
`WITH ag_catalog.age_shortest_path(<graph>, src, tgt, <physical edge types>, <direction>, <min>, <max>) AS p`.
`ShortestPath` uses `age_shortest_path`; `AllShortestPaths` uses `age_all_shortest_paths`. Direction
maps to `out`, `in`, or `any`; edge types are the physical AGE label names; an unbounded maximum is
passed as SQL null. The engine hydrates each returned `GraphValue.Path` into `IGraphPath` by
reusing the existing node/relationship materializer per hop, preserving relationship orientation.
A native path value carries only the path nodes and relationships; provider-owned complex values
are separate nodes, so native shortest-path materialization rejects entity types that declare
complex properties with an explicit error instead of silently returning incomplete entities.
Those queries must use `TraversePaths`. No client-side enumeration, finite-depth fallback, or
provider-local graph algorithm is used.

On AGE 1.7.0 the store caches the baseline dialect, `ShortestPath` stays absent, the
capability-gated TCK test skips with the recorded capability reason, and reachable shortest-path
use is rejected before execution by the shared planner. Translation coverage asserts both versions,
including that the 1.8 plan contains the native functions and no Cypher pattern function.

## Results

| AGE server | Inventory test methods | Executed | Capability-skipped | Statically skipped | Failed |
|---|---:|---:|---:|---:|---:|
| AGE 1.8.0 | 502 | 501 | 1 | 0 | 0 |
| AGE 1.7.0 | 502 | 500 | 2 | 0 | 0 |

The inventory was derived from the candidate assemblies with
`ComplianceInventory.TotalTestMethods`, `ComplianceInventory.MinimumExecuted(declared)`, and
`ComplianceInventory.ExpectedCapabilitySkips(declared)`, using the connected store's declared
capabilities. On AGE 1.8.0 only `OrderByEntity` is capability-skipped; on AGE 1.7.0 both
`OrderByEntity` and `ShortestPath` are skipped. Theory rows, the provider-specific adapter tests, and
the versioned feature-control tests expand the provider run to 758 runtime cases: 757 passed and
1 capability-skipped on AGE 1.8.0; 747 passed and 11 capability-skipped on AGE 1.7.0 (the 9
shortest-path provider tests plus the TCK contract and `OrderByEntity`).

`AgeGraphStoreOptions` can force `NativeShortestPath` and `NativePredicateFunctions` `Enabled`
(failing clearly below AGE 1.8.0) or `Disabled` (keeping the pre-1.8 behavior on a newer server).
The default `Auto` mode is what the capacity matrix below certifies.

### AGE backing-store matrix

| Run | Backing store | Runtime cases | Passed | Expected skips | Failed | `signal 11` | Container restarts |
|---|---|---:|---:|---:|---:|---:|---:|
| AGE 1.8 run 1 (fresh container) | AGE 1.8.0 / PostgreSQL 18.6 | 758 | 757 | 1 | 0 | 0 | 0 |
| AGE 1.8 run 2 (fresh container) | AGE 1.8.0 / PostgreSQL 18.6 | 758 | 757 | 1 | 0 | 0 | 0 |
| AGE 1.7 baseline (fresh container) | AGE 1.7.0 / PostgreSQL 18.1 | 758 | 747 | 11 | 0 | 0 | 0 |

Each container was created afresh from `apache/age:release_PG18_1.8.0` and
`apache/age:release_PG18_1.7.0` and received the full AGE lane once. After every run, `docker logs`
contained no `signal 11` or `terminated by signal`, `pg_isready` and `SELECT 1` succeeded, and the
restart count stayed 0. 

| Prior strict run | Runtime cases | Passed | Expected skips | Failed | TRX |
|---|---:|---:|---:|---:|---|
| Compatibility meta-tests | 30 | 28 | 2 | 0 | `strict-meta/graph-compatibilitytests-tests/graph-compatibilitytests-tests.trx` |
| InMemory provider | 541 | 540 | 1 | 0 | `strict-inmemory/graph-inmemory-tests/graph-inmemory-tests.trx` |
| Neo4j provider | 616 | 616 | 0 | 0 | `strict-neo4j/graph-neo4j-tests/graph-neo4j-tests.trx` |
| AGE provider (superseded by the matrix above) | 733 | 731 | 2 | 0 | `strict-age/graph-age-tests/graph-age-tests.trx` |

TRX paths are relative to the retained evidence root in the report header. There were no
infrastructure, discovery, static, ad hoc, or unplanned capability skips.

| Repository gate | Result |
|---|---|
| Debug build | Passed with 0 errors |
| Fast test lane | Passed: 8 projects, 2,527 runtime cases |
| Neo4j lane sanity | Passed: 617 runtime cases |
| AGE 1.8.0 lane, two fresh containers | Passed: 758 runtime cases each, 0 failed, 0 `signal 11` |
| AGE 1.7.0 lane, fresh container | Passed: 758 runtime cases, 11 expected skips |
| All test lane (prior certification, not re-run) | Passed: 10 projects, 3,869 runtime cases |
| Package validation (prior certification, not re-run) | Passed: exact 9-package / 15-assembly inventory and package-reference consumer build |
| Formatting | Passed with no changes required |
| Portable CodeQL (prior certification, not re-run) | Passed: Actions, C#, and Ruby SARIF each contained 0 results |

### Expected skip ledger

| Category | Run / exact method | Recorded reason |
|---|---|---|
| AGE 1.7 ShortestPath | AGE 1.7 / `Cvoya.Graph.Age.Tests.GraphTests.QueryTraversalTests.ShortestPaths_PinSelectionEndpointDirectionNoPathAndSameNodeSemantics` and the nine `AgeShortestPathIntegrationTests` cases | `Capability 'ShortestPath' not declared by provider 'Cvoya.Graph.Age'` |
| OrderByEntity architectural exclusion | AGE / `Cvoya.Graph.Age.Tests.GraphTests.AdvancedQueryTests.CanOrderByBareEntity` | `Capability 'OrderByEntity' not declared by provider 'Cvoya.Graph.Age'` |
| OrderByEntity architectural exclusion | InMemory / `Cvoya.Graph.InMemory.Tests.GraphTests.AdvancedQueryTests.CanOrderByBareEntity` | `Capability 'OrderByEntity' not declared by provider 'Cvoya.Graph.InMemory'` |
| Deliberate meta-test fixture | Meta / `Cvoya.Graph.CompatibilityTests.Tests.CapabilitySkipTests.RequiresUndeclaredCapability_IsSkippedNotExecuted` | `Capability 'FullTextSearch' not declared by provider 'Cvoya.Graph.CompatibilityTests.Tests.FakeProvider'` |
| Deliberate meta-test fixture | Meta / `Cvoya.Graph.CompatibilityTests.Tests.InterfaceLevelCapabilitySkipTests.InterfaceGatedTest_MustSkip` | `Capability 'FullTextSearch' not declared by provider 'Cvoya.Graph.CompatibilityTests.Tests.FakeProvider'` |

The provider-neutral shortest-path contract remains in `IQueryTraversalTests` and pins one/all
shortest-path selection, endpoint direction, no-path, and same-node semantics. It runs on AGE 1.8.0
and skips on AGE 1.7.0; `AgeShortestPathIntegrationTests` additionally pins cycles, ties, multiple
source/endpoint pairs, all directions, endpoint filtering, source exclusion, paging, count/any
terminals, typed segment hydration, and backend health. On AGE 1.7.0 reachable shortest-path use is
rejected before execution with `GraphQueryTranslationException`; `AgeShortestPathTranslationTests`
pins both the deterministic rejection and the native 1.8 plan shape. Provider-specific adapter,
dialect, SQL-envelope, full-text, and security tests contribute runtime cases but do not change the
compatibility method inventory.

## Reproduction

Start the repository-pinned providers. `start-age.sh` starts AGE 1.7.0 on port 5455 and AGE 1.8.0
on port 5456, so the AGE suite can run against both released versions:

```bash
./scripts/containers/start-neo4j.sh
./scripts/containers/start-age.sh all   # or: 1.7 | 1.8 to start a single version
export AGE_CONNECTION_STRING='Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres'
```

Run the AGE provider suite against both AGE versions in one pass (`--age-versions` maps 1.7 to
port 5455 and 1.8 to port 5456 and writes per-version TRX directories):

```bash
GRAPHMODEL_COMPLIANCE_STRICT=1 ./scripts/run-tests.sh --configuration Debug --lane age \
  --project Graph.Age.Tests --age-versions 1.7.0,1.8.0 --disable-diff-engine --report-trx \
  --results-directory artifacts/compliance/NUL-89/strict-age
```

Run the retained strict evidence:

```bash
./scripts/run-tests.sh --configuration Debug --lane fast \
  --project Graph.CompatibilityTests.Tests --disable-diff-engine --report-trx \
  --results-directory artifacts/compliance/NUL-88/strict-meta
GRAPHMODEL_COMPLIANCE_STRICT=1 ./scripts/run-tests.sh --configuration Debug --lane fast \
  --project Graph.InMemory.Tests --disable-diff-engine --report-trx \
  --results-directory artifacts/compliance/NUL-88/strict-inmemory
GRAPHMODEL_COMPLIANCE_STRICT=1 ./scripts/run-tests.sh --configuration Debug --lane neo4j \
  --project Graph.Neo4j.Tests --disable-diff-engine --report-trx \
  --results-directory artifacts/compliance/NUL-88/strict-neo4j
GRAPHMODEL_COMPLIANCE_STRICT=1 ./scripts/run-tests.sh --configuration Debug --lane age \
  --project Graph.Age.Tests --disable-diff-engine --report-trx \
  --results-directory artifacts/compliance/NUL-88/strict-age
```

Run the repository gates against the same source candidate:

```bash
dotnet build --configuration Debug
./scripts/run-tests.sh --configuration Debug --lane fast --disable-diff-engine
./scripts/run-tests.sh --configuration Debug --lane all --disable-diff-engine
dotnet msbuild eng/PackageValidation.proj -target:Validate
dotnet format cvoya-graph.sln --verify-no-changes --no-restore --verbosity minimal
./scripts/run-codeql.sh --output-dir artifacts/compliance/NUL-88/codeql
```

Every runtime skip counted in the capability column carries the suite's declared-capability
reason. Any failed case, unexpected dynamic skip, unavailable-store skip under strict mode, or
compliance-guard failure invalidates this report.
