# CVOYA graph — project instructions

CVOYA graph is an Apache-2.0 type-safe .NET library for graph data and graph databases. It targets .NET 10/C# 14 and ships provider-neutral querying, Neo4j, PostgreSQL + Apache AGE, an in-memory reference provider, analyzers, serialization/code generation, and a provider compatibility suite.

Tool-specific configuration lives in `.claude/` and `.codex/`; [docs/ai-agents.md](docs/ai-agents.md) maps the surfaces.

## Orientation

- `src/Graph`: provider-neutral model and LINQ surface.
- `src/Graph.{Neo4j,Age,InMemory}`: in-tree providers.
- `src/Graph.Cypher`: typed Cypher AST, validation, and rendering.
- `src/Graph.Analyzers`: `CG###` Roslyn analyzers.
- `src/Graph.Serialization*`: runtime representation and source generator.
- `src/Graph.CompatibilityTests`: packable provider contract suite (TCK).
- `tests/`, `examples/`, and `docs/`: verification, runnable examples, and canonical guidance.

## Build and test

```bash
dotnet build --configuration Debug
./scripts/run-tests.sh --configuration Debug --lane fast --disable-diff-engine
./scripts/run-tests.sh --configuration Debug --lane all --disable-diff-engine
```

Use repeatable `--project` and `--filter` selectors while iterating, then run the complete relevant lane on the stable diff. Run local Neo4j and AGE lanes serially because their suites mutate shared provider state.

The fast lane covers service-free core, analyzer, Cypher, translation, serialization/codegen, in-memory, and TCK meta-tests. The full lane also requires:

- Neo4j at `NEO4J_URI` or `bolt://localhost:7687` with `neo4j/password`; start it with `scripts/containers/start-neo4j.sh`.
- AGE at `AGE_CONNECTION_STRING`; start it with `scripts/containers/start-age.sh`.

`src/Graph.CompatibilityTests` defines contracts but executes almost no tests by itself; provider projects bind and run them. Benchmarks are outside the normal gate. See [provider implementers](docs/provider-implementers-guide.md) for the capability and certification model.

Validation is fail-closed. A project or build/test/package/release control-plane change updates solution membership, runner classification, release partitioning, and CI path scopes together. `ruby eng/ci/validation-inventory.test.rb` must continue to prove inventory completeness.

Run `dotnet msbuild eng/PackageValidation.proj -target:Validate` only for package/public-assembly changes; see [release process](docs/release-process.md).

## Conventions

- Follow [CONTRIBUTING.md](CONTRIBUTING.md), match surrounding style, and avoid unrelated reformatting.
- Put one public type per file and XML documentation on new public APIs.
- New source files use the repository Apache-2.0 header from `.editorconfig`.
- Public async APIs take `CancellationToken` and end in `Async`.
- Analyzer IDs use `CG###`; inspect `src/Graph.Analyzers/AnalyzerReleases.*.md` before allocating one. Suppress diagnostics explicitly through `.editorconfig` or a targeted pragma.
- Use conventional commit prefixes: `feat`, `fix`, `refactor`, `test`, `docs`, or `chore`.

## Workflow

- Work only in a prepared task worktree under `~/dev/worktrees/graph/<task>`, based on current `origin/main`; never edit the main checkout.
- One branch and PR owns one coherent outcome. Absorb small same-outcome gaps inside the owned surface and verification boundary; natively wire separate follow-ups.
- Install `eng/install-hooks.sh` once per clone. Linked worktrees inherit the pre-push hook, which runs the change-scoped Release build and format gate. Run the relevant test lane yourself; do not repeat the full static gate manually unless diagnosing it.
- Hosted CI owns the full provider matrix and CodeQL. Local CodeQL is opt-in for security-sensitive work, not a routine pre-push check.
- Treat `cvoya-graph.sln`, `Directory.Build.props`, `Directory.Packages.props`, `nuget.config`, `VERSION`, and workflows as high-conflict/protected. Make minimal additive edits and honor the repository guard.
- Rebase on current `origin/main` before pushing and merging. All changes land through a squash-merged PR.

PRs reference their issues and repeat the closing keyword per issue. Use native sub-issue/blocked-by relationships for dependencies, native issue types for category, milestones for release groups, and labels only for orthogonal attributes.

## Documentation

Ship documentation with behavior and public API changes. Keep samples compilable, grep docs when changing a public API, and update:

- [querying](docs/querying.md) for query operators or semantics;
- [provider implementers](docs/provider-implementers-guide.md) for provider contracts/capabilities;
- [migration guide](docs/migration-0.x.md) for breaking changes;
- [release process](docs/release-process.md) for packaging or publication.

New public APIs also receive XML docs.
