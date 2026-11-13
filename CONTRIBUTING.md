# Contributing to CVOYA Graph

Thank you for contributing. CVOYA Graph targets .NET 10 and C# 14 and accepts changes through pull
requests.

## Prerequisites

- .NET 10 SDK
- Git
- Podman or Docker for Neo4j/Apache AGE provider tests
- Bash and `jq` for package validation

AI coding agents should start with [AGENTS.md](AGENTS.md), the canonical repository instructions.

## Set up

```bash
git clone https://github.com/cvoya-com/graph.git
cd graph
dotnet restore
dotnet build --configuration Debug
```

Use a focused branch and conventional commit messages such as `feat:`, `fix:`, `test:`, `docs:`,
`refactor:`, or `chore:`.

## Test lanes

Use the discovering repository runner. It builds once, finds test projects under `tests/`, excludes
benchmarks, and rejects any selected project that reports zero tests.

```bash
# Service-free projects and the in-memory provider contract suite
./scripts/run-tests.sh --configuration Debug --lane fast

# Provider lanes; start the repository service container
./scripts/run-tests.sh --configuration Debug --lane neo4j --neo4j
./scripts/run-tests.sh --configuration Debug --lane age --age

# Complete suite with both services
./scripts/run-tests.sh --configuration Debug --lane all --neo4j --age
```

Run Neo4j and AGE lanes serially. Their suites create, mutate, and clean provider state and are not
documented as safe for concurrent runs.

Snapshot tests use Verify.Xunit. Agent-run commands should add `--disable-diff-engine` so a mismatch
stays in terminal output rather than opening a GUI diff tool.

During iteration, select the narrowest project/filter:

```bash
./scripts/run-tests.sh \
  --configuration Debug \
  --lane fast \
  --project Graph.Core.Tests \
  --filter '/*/*/SchemaRegistry*'
```

Use `--no-build` only when a compatible build already exists.

### Provider prerequisites

Neo4j defaults to `bolt://localhost:7687` with `neo4j/password`. Override with `NEO4J_URI`,
`NEO4J_USER`, `NEO4J_PASSWORD`, and `NEO4J_DATABASE`. Start the repository container directly with:

```bash
./scripts/containers/start-neo4j.sh
```

The script prefers Podman and falls back to Docker. Set `CONTAINER_RUNTIME=podman` or
`CONTAINER_RUNTIME=docker` to force one.

AGE uses `AGE_CONNECTION_STRING`; the default repository container listens on port 5455:

```bash
./scripts/containers/start-age.sh
export AGE_CONNECTION_STRING='Host=localhost;Port=5455;Username=postgres;Password=postgres;Database=postgres'
```

Setting `CI=true` locally does not start either service.

### What the projects cover

| Project | Role | Service |
| --- | --- | --- |
| `src/Graph.CompatibilityTests` | Packable provider TCK interfaces/harness; not a standalone runnable certification | None |
| `tests/Graph.InMemory.Tests` | TCK bound to the in-memory provider plus provider tests; full-text runs against its index-free matcher | None |
| `tests/Graph.Neo4j.Tests` | TCK bound to Neo4j plus provider integration tests | Neo4j |
| `tests/Graph.Age.Tests` | TCK bound to Apache AGE plus provider integration tests | AGE |
| `tests/Graph.CompatibilityTests.Tests` | TCK lifecycle, capability-skip, and compliance-guard meta-tests | None |
| `tests/Graph.Core.Tests` | Provider-neutral model, query shape, and serialization integration | None |
| `tests/Graph.Cypher.Tests` | Shared typed Cypher AST/planning/rendering | None |
| `tests/Graph.Neo4j.Translation.Tests` | Neo4j translation without database execution | None |
| `tests/Graph.QuerySurface.CompilationTests` | Compile-time query-surface contracts | None |
| `tests/Graph.Serialization.CodeGen.Tests` | Incremental serializer generator | None |
| `tests/Graph.Analyzers.Tests` | Roslyn analyzer diagnostics | None |
| `tests/Graph.Neo4j.Performance.Tests`, `tests/Graph.Age.Performance.Tests` (shared code in `tests/Graph.Performance.Tests.Shared`) | Benchmarks; outside normal gates | Provider-specific benchmark setup |

Provider-agnostic behavior belongs in the TCK so every provider binding inherits it. Running
`src/Graph.CompatibilityTests` alone proves nothing; see the
[provider implementers guide](docs/provider-implementers-guide.md#certifying-a-provider).

## Project layout

```text
src/Graph/                       Core model, query, transaction, and mutation contracts
src/Graph.Neo4j/                 Neo4j provider
src/Graph.Age/                   PostgreSQL + Apache AGE provider
src/Graph.InMemory/              In-memory reference provider
src/Graph.Cypher/                Shared typed Cypher model/planner/renderer
src/Graph.Serialization/         Runtime serialization and result materialization
src/Graph.Serialization.CodeGen/ Incremental serializer generator
src/Graph.Analyzers/             Consumer-model analyzers
src/Graph.CompatibilityTests/    Packable provider TCK
tests/                           Test bindings, provider tests, and library tests
examples/                        Compiling usage examples
docs/                            Conceptual, provider, build, and release documentation
scripts/                         Test, container, benchmark, release, and validation helpers
```

## Code changes

- Match surrounding C# style and do not reformat unrelated code.
- Put one public type in each source file.
- Add XML documentation to public APIs.
- Add the repository Apache 2.0 header to new source files.
- Public async APIs use an `Async` suffix and accept a `CancellationToken`.
- Add provider-neutral contract behavior to the TCK; add provider-specific tests to the provider
  test project.
- When adding/moving/removing a project or changing validation control-plane files, keep solution,
  runner, release, and CI inventories/scopes complete in the same PR.
- Do not add public provider identity, universal root metadata, or undocumented storage
  companions. V1 entities are identity-free and provider physical identity is private.

## Documentation changes

Update conceptual docs, package READMEs, XML comments, and compiling examples with behavior changes.
Code snippets must match the final public API. The v1 migration boundary is explicit: alpha-era
data must be recreated/reimported; do not present ad-hoc SQL/Cypher as supported migration tooling.

Build the documentation output:

```bash
./scripts/build-docs.sh Debug
```

Docs-only changes may skip the test lane when they do not touch source/XML or examples. Changes to
source XML or compiling examples should build the affected project/examples.

## Package changes

Package validation derives the expected inventory from packable projects under `src/`, verifies
missing/unexpected packages and assembly version metadata, then builds with package references:

```bash
dotnet msbuild eng/PackageValidation.proj -target:Validate
```

Run this gate when public API, assembly output, package contents/references, or release metadata
changes. It is not a substitute for focused tests.

## Pull requests

Before opening a PR:

1. Keep the diff focused on the declared issue.
2. Run targeted checks while iterating.
3. Review the final diff for unrelated changes.
4. Run the complete relevant test lane once on the stable final patch.
5. Update docs and examples for behavior/API changes.
6. Reference issues with a closing keyword for each issue (`Closes #1, closes #2`).

The installed pre-push hook enforces the repository Release build/format gate. Do not duplicate
that gate manually; run the relevant test lane described above.

## Reporting bugs

Include:

- expected and actual behavior;
- minimal reproduction;
- .NET, OS, and provider/database versions;
- relevant model and query snippets; and
- full exception/inner-exception details with secrets removed.

Report security vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License and conduct

Contributions are licensed under the Apache License 2.0. Follow the
[Code of Conduct](CODE_OF_CONDUCT.md).
