# PR-03: AGE 1.8 compatibility — remove native-identity `IN` SIGSEGVs

## Summary

Makes the full `Graph.Age.Tests` lane green on **Apache AGE 1.8.0 / PostgreSQL 18.6**
without any PostgreSQL backend SIGSEGV, and keeps it green on **Apache AGE 1.7.0 /
PostgreSQL 18.1**.

The change is deliberately narrow: Apache AGE 1.8.0 crashes when `id()` is the left operand
of `IN`. It does **not** crash for ordinary list-parameter membership such as
`node.Tags IN $list` or `rel.inheritance_labels[0] IN $relationshipTypes`.

This branch is based on `main` @ `039da83` and contains only the AGE compatibility work. It
does not include the serialization PR (`pr/01-serialization`).

## Root cause

The prior hypothesis — "any list parameter inside `IN` crashes AGE 1.8" — is too broad.
Reproduced directly on `apache/age:release_PG18_1.8.0` (PostgreSQL 18.6):

| Query | Result on AGE 1.8.0 |
|---|---|
| `RETURN 1 IN $ids` | OK |
| `MATCH (n:T) WHERE n.name IN $names` | OK |
| `MATCH (n:T) WHERE n.age IN $ages` | OK |
| `MATCH (n:T) WHERE id(n) IN [1,2]` | Type error, no crash |
| `MATCH (n:T) WHERE id(n) IN $ids` | **Backend SIGSEGV (`signal 11`)** |
| `MATCH (n) WITH collect(id(n)) AS ids MATCH (m) WHERE id(m) IN ids` | **Backend SIGSEGV (`signal 11`)** |

The crash is specific to a native graphid produced by `id()` on the left side of `IN`,
whatever the list source. After the crash PostgreSQL enters recovery and hundreds of
follow-up tests fail with `57P03`; those were secondary.

## Changes

### 1. Native-identity membership goes through the dialect

`ExpressionToCypherAstLowerer.TryLowerCollectionContains` previously emitted a raw binary
`IN` whenever the collection was a parameter:

```csharp
loweredSource is CollectionPropertyAccess
    ? new CollectionContainsExpression(loweredSource, Lower(item, aliases))
    : new AstBinaryExpression(CypherBinaryOperator.In, Lower(item, aliases), loweredSource);
```

That bypassed the dialect for the full-text graphid filter (`id(alias) IN $graphIds`).
`NativeElementIdentity` items are now routed through `CollectionContainsExpression` as well,
so the dialect decides how native identity membership is rendered.

`AgeDialect.RenderCollectionContains` renders native identity as a filtered list
comprehension:

```cypher
size([__cvoya_collection_item IN $graphIds WHERE __cvoya_collection_item = id(src)]) > 0
```

Stored-collection membership keeps the shared null-aware `toString()` rendering. This matters:
AGE 1.7.0 does **not** match an agtype null parameter with `IS NULL` (`size(...) = 0`), while
AGE 1.8.0 does. Replacing the null-aware form globally regressed three 1.7 null-collection
tests; keeping it for stored collections fixes both versions.

### 2. Raw `id(parent) IN $parentGraphIds`

`ComplexPropertyManager` had two raw sites (collection metadata replacement and complex value
deletion). Both now use the same semantics-preserving list-comprehension form:

```cypher
size([parentGraphId IN $parentGraphIds WHERE id(parent) = parentGraphId]) > 0
```

### 3. Uniqueness self-exclusion

`CypherEngine.ValidateUnselectedConstraintsAsync` now emits:

```cypher
size([selectedId IN $selectedIds WHERE id(candidate) = selectedId]) = 0
```

instead of `NOT (id(candidate) IN $selectedIds)`.

### 4. Delete-preflight prerequisite

Commit 2 (`dad818d`, cherry-pick of PR-02 / `a5c6e12`) preflights relationships in both
directions when deleting a node without cascade. It is the first commit and is required for
the full lane on both AGE versions.

## Semantics preserved

- Empty lists: the comprehension is empty and the predicate is false (`= 0` for exclusion).
- Deduplication: membership scans each candidate once; counts are unchanged.
- Ordering and counting: no rows are added or removed; the predicate is a `WHERE` filter.
- Transactions: every query remains one server-side statement inside the existing transaction.
- Null elements in stored collections: the shared `toString()` null-aware form is retained.

## Regression coverage

| Test | Shape covered |
|---|---|
| `AgeDialectTests.RenderCollectionContains_NeverPutsNativeIdentityOnTheLeftOfIn` | Rendered identity membership has no `id(...) IN` and no `toString(id(...))` |
| `AgeDialectTests.RenderCollectionContains_KeepsNullAwareRenderingForStoredCollections` | Stored collections keep the 1.7-compatible null-aware form |
| `AgeFullTextSearchRewriterTests.BuildGraphIdFilter_LowersToNativeIdentityWithoutPublicIdAccess` | Full-text graphid filter renders as a list comprehension |
| `AgeListParameterRegressionTests.FullTextSearch_MultipleGraphIds_ReturnsAllMatchesAndLeavesBackendHealthy` | Full-text search with several matches; queries again after to prove the backend survived |
| `AgeListParameterRegressionTests.UpdatingComplexCollection_ParentIds_ReplacesItemsAndLeavesBackendHealthy` | Complex collection replacement and value cleanup |
| `AgeListParameterRegressionTests.UpdatingUniqueNode_ExcludesSelectedIds_AndLeavesBackendHealthy` | Uniqueness revalidation excludes the selected row and still rejects a real duplicate |
| `IGraphCommandTests.SetBasedNodeDelete_PreflightsIncomingRelationshipsAndCascadeDetachesThem` | Delete preflight in both relationship directions |

## Verification matrix

All AGE runs used a fresh container, `--lane age --disable-diff-engine`, and the final branch.

| Run | Image / backing store | Total | Passed | Expected skips | Failed | `signal 11` | Restarts |
|---|---|---:|---:|---:|---:|---:|---:|
| AGE 1.8 run 1 | `apache/age:release_PG18_1.8.0` / PG 18.6 | 733 | 731 | 2 | 0 | 0 | 0 |
| AGE 1.8 run 2 | `apache/age:release_PG18_1.8.0` / PG 18.6 | 733 | 731 | 2 | 0 | 0 | 0 |
| AGE 1.7 run | `apache/age:release_PG18_1.7.0` / PG 18.1 | 733 | 731 | 2 | 0 | 0 | 0 |

Expected skips are only `OrderByEntity` (`CanOrderByBareEntity`) and `ShortestPath`
(`ShortestPaths_PinSelectionEndpointDirectionNoPathAndSameNodeSemantics`).

After every run:

- `docker logs` contained no `signal 11`, `terminated by signal`, or recovery marker.
- `pg_isready` and `SELECT 1` succeeded.
- Container restart count stayed `0`.

Additional gates:

| Gate | Result |
|---|---|
| `dotnet build` | Succeeded, 0 errors |
| Fast lane (`--lane fast --disable-diff-engine`) | 8 projects, 2,527 cases, 0 failed |
| Neo4j lane sanity | See attached `neo4j-lane.log` |

Commands:

```bash
AGE_CONNECTION_STRING='Host=localhost;Port=5471;Username=postgres;Password=postgres;Database=postgres' \
  ./scripts/run-tests.sh --configuration Debug --lane age --disable-diff-engine --no-build
AGE_CONNECTION_STRING='Host=localhost;Port=5472;Username=postgres;Password=postgres;Database=postgres' \
  ./scripts/run-tests.sh --configuration Debug --lane age --disable-diff-engine --no-build
./scripts/run-tests.sh --configuration Debug --lane fast --disable-diff-engine
NEO4J_URI=bolt://localhost:7687 ./scripts/run-tests.sh --configuration Debug --lane neo4j --disable-diff-engine --no-build
```

## Commits

| Commit | Subject |
|---|---|
| `dad818d19efc7871746e8c86da8394ed51ae6765` | `fix(age): detect incoming relationships in delete preflight` (PR-02 prerequisite) |
| `8ddfa3bfe6289626df1cb0be5457355f5625f1a6` | `fix(age): avoid native-identity IN for AGE 1.8 compatibility` |

## Compliance

`src/Graph.Age/COMPLIANCE.md` now records AGE 1.8.0 / PostgreSQL 18.6 as the primary backing
store, the AGE 1.7.0 / PostgreSQL 18.1 cross-check, the precise native-identity rule, and the
733-case evidence matrix.

## Approval gate

No merge to `main`/`master` and no pull request is opened without a board decision.
