# PR-06: AGE 1.8 compatibility + native ShortestPath — combined result

## Summary

Combines the two completed AGE 1.8 workstreams into one branch:

1. **NUL-88 / PR-03** — remove the native-identity `IN` SIGSEGV and make the full AGE lane green on
   AGE 1.8.0 and 1.7.0.
2. **NUL-89 / PR-05** — native `ShortestPath` / `AllShortestPaths` on AGE 1.8.0 through
   `ag_catalog.age_shortest_path` / `age_all_shortest_paths`, with per-server capability detection.
3. **NUL-88 / PR-04 controls merged in** — explicit `Auto` / `Enabled` / `Disabled` feature modes and
   the AGE 1.8 `any()` predicate-function optimization.

The branch is stacked on `pr/03-age18-list-param` @ `69c591e` and reaches `main` @ `039da83`.
It contains no serialization-PR changes.

## Part 1 — native-identity `IN` fix (NUL-88)

AGE 1.8.0 segfaults when `id()` is the left operand of `IN`; ordinary property/list membership does
not crash. The provider now renders native-identity membership as
`size([item IN $list WHERE item = id(alias)]) > 0` and routes the full-text graphid filter through
the dialect hook. Stored-collection membership keeps the null-aware `toString()` form because AGE
1.7 does not match an agtype null parameter with `IS NULL`. Regression coverage:
`AgeDialectTests`, `AgeFullTextSearchRewriterTests`, `AgeListParameterRegressionTests`, and the
delete-preflight TCK test.

## Part 2 — native shortest paths (NUL-89)

Apache AGE 1.8.0 released public SQL functions `ag_catalog.age_shortest_path` and
`ag_catalog.age_all_shortest_paths`; AGE 1.7.0 has neither. AGE 1.8.0 still rejects Cypher
`shortestPath()` / `allShortestPaths()` syntax, so the provider uses a dedicated AGE planner:

- independent source and endpoint matches, endpoint predicates applied before selection, `src <> tgt`;
- physical edge types, `out` / `in` / `any` direction mapping, and min/max hop bounds;
- `age_shortest_path` for one minimum path, `age_all_shortest_paths` for every tied minimum path;
- whole `GraphValue.Path` results materialized through the existing typed node/relationship
  materializer, preserving segment direction and entity hydration;
- path-scoped `Where` / `OrderBy`, paging, `Count`, `Any`, and endpoint projections.

## Part 3 — feature controls and predicate optimization (PR-04)

`AgeGraphStore` detects the AGE `extversion` and probes the native shortest-path functions on the
first connection. `AgeGraphStoreOptions` exposes:

| Option | `Auto` (default) | `Enabled` | `Disabled` |
|---|---|---|---|
| `NativeShortestPath` | enable on AGE >= 1.8.0 with the native functions | fail clearly below 1.8.0 | keep the pre-1.8 rejection on every server |
| `NativePredicateFunctions` | use `any()` on AGE >= 1.8.0 | fail clearly below 1.8.0 | keep the list-comprehension form |

`NativePredicateFunctions` uses AGE 1.8's `any()` for stored-collection membership. Native-identity
membership stays on the list form because AGE 1.8's `any()` transform mis-types graphid comparisons
(`no relation entry for relid`).

## Verification

| Run | Backing store | Runtime cases | Passed | Expected skips | Failed | `signal 11` | Restarts |
|---|---|---:|---:|---:|---:|---:|---:|
| AGE 1.8 run 1 (fresh) | AGE 1.8.0 / PG 18.6 | 758 | 757 | 1 | 0 | 0 | 0 |
| AGE 1.8 run 2 (fresh) | AGE 1.8.0 / PG 18.6 | 758 | 757 | 1 | 0 | 0 | 0 |
| AGE 1.7 run (fresh) | AGE 1.7.0 / PG 18.1 | 758 | 747 | 11 | 0 | 0 | 0 |
| Fast lane | service-free | 2,527 | 2,527 | 0 | 0 | n/a | n/a |
| Neo4j sanity | Neo4j 5 Enterprise | 617 | 617 | 0 | 0 | n/a | n/a |

The only AGE 1.8 skip is `OrderByEntity`. The AGE 1.7 skips are `OrderByEntity`, the shortest-path
TCK contract, and the nine native shortest-path integration tests. After every AGE run,
`pg_isready` and `SELECT 1` succeeded, `docker logs` contained no `signal 11` / recovery marker, and
the restart count stayed 0. `dotnet build` and `dotnet format --verify-no-changes` passed.

## Branch and commits

- Branch: `pr/06-age18-shortest-path-combined`
- Base: `pr/03-age18-list-param` @ `69c591e` (includes NUL-88)
- Commits:
  - `79ed6fa83b8442cd5c8f208d58d3ce3f191b497a` — native shortest-path planner, hydration, tests
  - `8d33a7ab7da2fae1a72358d96e9ab7ecae84e620` — shortest-path documentation
  - `c881e9ed4832f66b5831b0b45e236df57fc72381` — complex-property boundary note
  - `90b96990913eca3331d02f0680b0a98b4b812411` — feature controls, predicate optimization, combined docs/tests

## Approval gate

No merge to `main`/`master` and no pull request is opened without a board decision.
