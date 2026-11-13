# Neo4j performance benchmarks

The BenchmarkDotNet CRUD and relationship suites in this project run against a Neo4j instance.

Requirements:

- Neo4j reachable via `NEO4J_URI` (default `bolt://localhost:7687`) with `NEO4J_USER` /
  `NEO4J_PASSWORD` (default `neo4j` / `password`).

Run:

```bash
dotnet run --project tests/Graph.Neo4j.Performance.Tests --configuration Benchmark -- --all
```

See `scripts/run-benchmarks.sh` for the wrapper that adds report exporters and an output directory.
