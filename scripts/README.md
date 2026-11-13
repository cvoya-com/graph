# CVOYA graph Scripts

This directory contains utility scripts for the CVOYA graph project.

## 🚀 Quick Start Commands

### Build System Validation

```bash
# Check project status
./scripts/status.sh

# Validate all build configurations
./scripts/validate-build.sh

# Validate builds and run local CodeQL analysis
./scripts/validate-build.sh --codeql
```

### Releasing

Releases are tag-authoritative — the tag is the version, and `release.sh` is the
only supported way to cut one. It pushes the tag, watches
`.github/workflows/release.yml` (build, test, pack, attest, publish to NuGet,
create the GitHub Release, and deploy the tagged documentation), then verifies
every package resolves on nuget.org. Before creating the immutable tag, it also
verifies that Pages uses GitHub Actions and that the protected `github-pages`
environment allows exactly `v*` tags.

```bash
# Preview the computed tag without pushing anything
./scripts/release.sh 1.2.3 --pre alpha --plan

# Cut a stable release  -> v1.2.3
./scripts/release.sh 1.2.3

# Cut a date-anchored pre-release  -> v1.2.3-alpha.20260716
./scripts/release.sh 1.2.3 --pre alpha

# ...and make it the current Latest on GitHub
./scripts/release.sh 1.2.3 --pre alpha --latest

# Run the release script's isolated command-flow tests
bash ./scripts/release.test.sh

# Show the version the current tree would build as
dotnet msbuild -target:ShowVersion
```

See [docs/release-process.md](../docs/release-process.md) for the version scheme
and the full process.

### Build Commands

```bash
# Development build (project references)
dotnet build --configuration Debug

# Performance testing build (project references + optimizations)
dotnet build --configuration Benchmark

# Local package-reference validation
dotnet msbuild eng/PackageValidation.proj -target:Validate

# Production package build (pack builds first by default)
dotnet pack src/Graph/Graph.csproj --configuration Release
```

### Testing

```bash
# Run the fast lane
./scripts/run-tests.sh --fast

# Run tests with coverage
./scripts/run-tests.sh --fast --coverage

# Start both provider services and run all tests
./scripts/run-tests.sh --lane all --neo4j --age

# Run the AGE suite against both released AGE versions (1.7.0 on port 5455, 1.8.0 on port 5456)
./scripts/containers/start-age.sh all
./scripts/run-tests.sh --lane age --age-versions 1.7.0,1.8.0 --no-build

# Run performance tests
./scripts/run-tests.sh --fast --performance
```

### CodeQL Analysis

```bash
# Run the same C# CodeQL query suite used by the GitHub workflow
./scripts/run-codeql.sh

# Trace the LocalFeed and Release builds when the local platform supports it
./scripts/run-codeql.sh --build-mode manual

# Treat any local CodeQL result as a failing validation
./scripts/run-codeql.sh --fail-on-alerts
```

### Package Management

```bash
# Set up local NuGet feed for testing
./scripts/setup-local-feed-msbuild.sh

# Clean up local feed
./scripts/cleanup-local-feed.sh

# Run benchmarks
./scripts/run-benchmarks.sh
```

### Cleanup

```bash
# Clean build artifacts
./scripts/clean-all.sh

# Clean everything (containers, cache, artifacts)
./scripts/clean-all.sh --all
```

## 📖 Complete Documentation

For comprehensive build system documentation, see: **[docs/graph-model-developers.md](../docs/graph-model-developers.md)**

## 📦 Build Configurations

| Configuration | Project Refs | Optimizations | Packages | VERSION Required | Use Case              |
| ------------- | ------------ | ------------- | -------- | ---------------- | --------------------- |
| **Debug**     | ✅ Yes       | ❌ No         | ❌ No    | ❌ No            | Development           |
| **Benchmark** | ✅ Yes       | ✅ Yes        | ❌ No    | ❌ No            | Performance testing   |
| **LocalFeed** | ✅ Yes       | ✅ Yes        | ✅ Yes   | ❌ No            | Local package testing |
| **Release**   | ✅ Default   | ✅ Yes        | Via `pack` | ✅ Yes         | Production package builds |

## Local NuGet Feed Scripts

For testing Release configuration with local packages before publishing:

### Repository package-validation orchestrator

`eng/PackageValidation.proj` is the only owner of feed setup, packing, package-reference restore/build, and cleanup:

```bash
# Run the complete package-reference gate
dotnet msbuild eng/PackageValidation.proj -target:Validate

# Prepare only the verified local feed
dotnet msbuild eng/PackageValidation.proj -target:PrepareLocalFeed

# Remove only repository-owned validation state
dotnet msbuild eng/PackageValidation.proj -target:Clean
```

It uses `eng/package-validation.NuGet.config`, maps `Cvoya.*` exclusively to the generated feed,
and isolates packages, HTTP cache, scratch, and plugin cache under
`artifacts/package-validation/`. `scripts/verify-package-set.sh` derives the expected inventory
from every packable project under `src/`, rejects missing or unexpected packages, and verifies all
packaged assembly version metadata. The workflow never registers a user-level source or clears
global NuGet state.

The inventory check requires `bash` and `jq`; path handling in the MSBuild orchestrator is OS-native on Windows, Linux, and macOS.

`scripts/setup-local-feed-msbuild.sh` and `scripts/cleanup-local-feed.sh` are compatibility wrappers that delegate to this project.

### `cleanup-local-feed.sh`

Removes the local NuGet feed and cleans up:

```bash
# Clean up local feed
./scripts/cleanup-local-feed.sh
```

It removes only `artifacts/package-validation/`; user sources and caches are untouched.

## 📚 Documentation Build Scripts

### `validate-documentation.sh` (Bash)

Runs deterministic source validation before the site generators:

- validates repository-local Markdown files and heading anchors without crawling external sites;
- proves the validator rejects broken-link and stale-snippet fixtures with file/line diagnostics;
- checks every `<!-- checked-snippet: examples/...#region -->` fence against its named compiled
  source region; and
- discovers and builds every project under `examples/`.

Important quick-start, provider, CRUD, and query snippets use checked regions. Edit the example
source first, then copy the marked region into the adjacent Markdown fence; CI reports the first
line that differs.

```bash
./scripts/validate-documentation.sh Debug
```

### `build-docs.sh` (Bash)

A Bash script for building XML documentation from all source projects.

**Usage:**

```bash
./scripts/build-docs.sh [configuration]
```

**Parameters:**

- `configuration`: Build configuration (default: `Release`)

**Examples:**

```bash
# Build documentation with Release configuration
./scripts/build-docs.sh

# Build documentation with Debug configuration
./scripts/build-docs.sh Debug

# Build documentation with Benchmark configuration
./scripts/build-docs.sh Benchmark
```

### `run-benchmarks.sh` (Bash)

A Bash script for running performance benchmarks on macOS/Linux.

**Usage:**

```bash
./scripts/run-benchmarks.sh [options]
```

**Options:**

- `-m, --mode <mode>`: Benchmark mode (default: `all`)
  - `all`: Run all benchmarks automatically
  - `crud`: Run only CRUD operation benchmarks
  - `relationships`: Run only relationship benchmarks
  - `interactive`: Interactive benchmark selection
- `-o, --output <dir>`: Output directory for results (default: `./benchmarks`)
- `-h, --help`: Show help message

## 🛠️ Development Tools Scripts

### `status.sh` ⭐ **New**

Shows the current state of the build system, containers, and project.

**Usage:**

```bash
./scripts/status.sh
```

**What it does:**

- Shows project information (version, .NET SDK)
- Reports build artifacts status
- Checks container status (Neo4j, Seq)
- Shows test results status
- Provides recommendations and quick actions

### `validate-build.sh` ⭐ **New**

Validates the entire build system and ensures all configurations work correctly.

**Usage:**

```bash
./scripts/validate-build.sh [options]
```

**Options:**

- `--codeql`: Run local CodeQL analysis after build validation
- `-h, --help`: Show help message

**What it does:**

- Tests all build configurations (Debug, Benchmark, LocalFeed, Release)
- Validates the repository-scoped package feed and package-reference build
- Checks prerequisites and project structure
- Ensures the build system is ready for development and CI/CD

### `run-codeql.sh`

Runs local CodeQL analysis for GitHub Actions, C#, and Ruby using the same
`security-and-quality` query suite as `.github/workflows/codeql.yml`. By default
it uses CodeQL's `none` build mode, which is the required portable local gate. In
that mode, the script analyzes a disposable source copy and temporary databases
outside the checkout so CodeQL dependency probing cannot rewrite repository files.

Use `--build-mode manual` to trace the same LocalFeed and Release builds used by
the GitHub workflow when the local platform supports CodeQL compiler tracing.
Manual mode is optional and may fail locally even when the default scan succeeds;
if it reports that no C# source was processed after a successful build, use the
default command for the local gate and include the CodeQL version plus
`db/csharp/log` path in PR notes only when relevant.

**Usage:**

```bash
./scripts/run-codeql.sh [options]
```

**Options:**

- `-o, --output-dir <dir>`: SARIF output directory (default: `artifacts/codeql`)
- `--build-mode <mode>`: CodeQL build mode, `none` or optional tracer-dependent `manual` (default: `none`)
- `--no-download`: Do not download/update the CodeQL query packs
- `--fail-on-alerts`: Exit non-zero if the SARIF file contains results
- `--threads <count>`: Number of CodeQL evaluator threads
- `--ram <mb>`: Maximum RAM for CodeQL, in MB
- `-h, --help`: Show help message

**Examples:**

```bash
./scripts/run-codeql.sh
./scripts/run-codeql.sh --build-mode manual
./scripts/run-codeql.sh --fail-on-alerts
./scripts/run-codeql.sh --threads 4 --ram 8192
```

### `run-zizmor.sh`

Runs the same pinned `zizmor` GitHub Actions security audit enforced by the CI
workflow, exiting non-zero if zizmor reports any finding. Requires `uv` (for
`uvx`). If the `gh` CLI is logged in, its token is reused so zizmor's online
audits (impostor commits, ref confusion) run exactly as they do in CI.

```bash
./scripts/run-zizmor.sh
```

### `run-tests.sh`

Discovers test projects under `tests/`, builds once, and runs an explicit fast,
Neo4j, AGE, or full lane. Benchmark projects remain separate.

**Usage:**

```bash
./scripts/run-tests.sh [options]
```

**Options:**

- `-c, --configuration <config>`: Build configuration (default: Debug)
- `-v, --verbosity <level>`: Test verbosity (default: normal)
- `--lane <fast|neo4j|age|all>`: Test lane (default: all)
- `--fast`: Alias for `--lane fast`
- `--project <name-or-path>`: Run one test project (repeatable)
- `--filter <xUnit-query>`: Apply an xUnit query filter (repeatable, OR)
- `--coverage`: Collect Cobertura coverage per test project
- `--report-trx`: Write an xUnit TRX report per test project
- `--results-directory <path>`: Root directory for coverage and test reports
- `--keep-going`: Run every selected project after a test failure
- `--neo4j`: Start Neo4j container before tests
- `--age`: Start Apache AGE container before tests
- `--seq`: Start Seq container before tests
- `--no-analyzers`: Skip analyzer tests
- `--no-neo4j`: Skip Neo4j tests in the full lane
- `--no-age`: Skip AGE tests in the full lane
- `--no-build`: Reuse an existing build
- `--disable-diff-engine`: Keep Verify snapshot failures in terminal output
- `--performance`: Run performance tests

**Examples:**

```bash
./scripts/run-tests.sh --fast                             # All service-free/in-memory tests
./scripts/run-tests.sh --lane neo4j --neo4j              # Start Neo4j and run its lane
./scripts/run-tests.sh --lane age --age                   # Start AGE and run its lane
./scripts/run-tests.sh --lane all --neo4j --age           # Start both services and run all tests
./scripts/run-tests.sh --fast --coverage                  # Fast lane with coverage
./scripts/run-tests.sh --lane all --no-build --coverage --report-trx --results-directory TestResults --keep-going
```

### `clean-all.sh` ⭐ **New**

Comprehensive cleanup script that removes all build artifacts, containers, and temporary files.

**Usage:**

```bash
./scripts/clean-all.sh [options]
```

**Options:**

- `--containers`: Clean up containers (Neo4j, Seq)
- `--nuget`: Clean NuGet cache
- `--all`: Clean everything (containers, cache, artifacts)

**Examples:**

```bash
./scripts/clean-all.sh                    # Clean build artifacts only
./scripts/clean-all.sh --containers       # Clean containers and artifacts
./scripts/clean-all.sh --all              # Clean everything
```

### `start-neo4j.sh`

Starts the Neo4j container.

**Usage:**

```bash
./scripts/containers/start-neo4j.sh
```

**What it does:**

- Starts the Neo4j container using Podman or Docker
- Prefers Podman when both runtimes are usable
- Supports `CONTAINER_RUNTIME=podman` or `CONTAINER_RUNTIME=docker` to force a runtime
- If the image doesn't exist locally, it is downloaded

### `start-seq.sh`

Starts Seq log aggregation system for development and testing using Podman.

**Usage:**

```bash
./scripts/containers/start-seq.sh
```

**What it does:**

- Starts Seq container with log aggregation using Podman
- Provides log UI at http://localhost:5341
- Stores log data in `~/tmp/logdata`
- Automatically removes existing containers
