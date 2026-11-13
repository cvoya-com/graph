# AGE performance benchmarks

`AgeIndexBenchmarkHarness` measures the declared-index scenarios — equality and `GROUP BY`/`DISTINCT`
on a declared string property, grouped count/sum/min/max/average aggregates, complex-property paging,
and native-id list predicates — against the ordinary AGE query path and against the path selected by
definitions registered through `SchemaRegistry`. It is not a BenchmarkDotNet suite, because the
scenarios share one seeded graph and need a before/after comparison on the same data.

Requirements:

- Apache AGE with the graph provider on `AGE_CONNECTION_STRING` (default `localhost:5455`, database
  `postgres`, user/password `postgres`).
- Enough disk/time for the requested node count; the default is 30,000 nodes.

Run:

```bash
AGE_CONNECTION_STRING='Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres' \
  dotnet run --project tests/Graph.Age.Performance.Tests -c Release -- \
  --age-index-harness --nodes 30000 --iterations 10 --warmups 3 --output benchmarks
```

Options:

| Option | Meaning |
|---|---|
| `--nodes N` | Number of seeded nodes (default 30,000). |
| `--iterations N` | Measured iterations per scenario (default 20). |
| `--warmups N` | Warm-up iterations per scenario (default 3). |
| `--output DIR` | Directory for the JSON/Markdown results (default `./benchmarks`). |
| `--graph NAME` | Benchmark graph name (default `cvoya_age_index_benchmark`). |
| `--non-sealed` | Seed the paging scenario's document labels with a non-sealed address record instead of the sealed one, so the common non-sealed property shape is measured with the same paging scenario. |
| `--keep-graph` | Keep the benchmark graph and companion tables after the run. |

The harness registers the name property (equality + ordering), a group-key equality property, and a
matching aggregate for the benchmark labels through `SchemaRegistry`. Companion structures are created
only by `EnsureIndexesAsync`. It also creates the `USING gin (properties)` index on the benchmark graph,
because the containment query shape depends on it; the provider does not create that index.

Every label resolves to the single generalized `<label>__idx` companion table and the hashed aggregate
table. There is no legacy variant to select: a property literally named `Status` or `Kaufpreis` uses
the same physical scheme as any other property. A benchmark run therefore measures the only path the
provider has, and its variant field reports `generalized (__idx)`.

The paging scenario orders by the companion text column's B-tree. At 30,000 nodes the companion
page-id pre-query is sub-millisecond, and the complex-property hydration is bounded by the projected
type's declared depth: a depth-1 address uses one direct hop instead of the global variable-length
range. The bound also covers concrete types derived from a non-sealed property type, so the
`--non-sealed` variant uses the same one-hop shape as the sealed default. After those changes the
declared path measures roughly 54-59 ms p95 on an AGE 1.7 harness (down from roughly 2.7 s), with the
baseline at roughly 70-83 ms; 54-59 ms is the measured floor for this harness on this hardware.

The graph is dropped at the start and end of a normal run. Results are written as
`age-index-<timestamp>.json` and `.md`.
