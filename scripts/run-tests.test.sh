#!/bin/bash

set -euo pipefail

REPOSITORY_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
TEST_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/cvoya-run-tests.XXXXXX")
FAKE_BIN="$TEST_ROOT/bin"
CALLS_FILE="$TEST_ROOT/dotnet-calls"

cleanup() {
    rm -rf "$TEST_ROOT"
}

trap cleanup EXIT

mkdir -p \
    "$FAKE_BIN" \
    "$TEST_ROOT/tests/Graph.Core.Tests" \
    "$TEST_ROOT/tests/Graph.Analyzers.Tests" \
    "$TEST_ROOT/tests/Graph.Neo4j.Tests" \
    "$TEST_ROOT/tests/Graph.Age.Tests" \
    "$TEST_ROOT/tests/Graph.Performance.Tests"

touch \
    "$TEST_ROOT/cvoya-graph.sln" \
    "$TEST_ROOT/tests/Graph.Core.Tests/Graph.Core.Tests.csproj" \
    "$TEST_ROOT/tests/Graph.Analyzers.Tests/Graph.Analyzers.Tests.csproj" \
    "$TEST_ROOT/tests/Graph.Neo4j.Tests/Graph.Neo4j.Tests.csproj" \
    "$TEST_ROOT/tests/Graph.Age.Tests/Graph.Age.Tests.csproj" \
    "$TEST_ROOT/tests/Graph.Performance.Tests/Graph.Performance.Tests.csproj"

cp "$REPOSITORY_ROOT/scripts/run-tests.sh" "$TEST_ROOT/scripts-run-tests.sh"

cat > "$FAKE_BIN/dotnet" <<'EOF'
#!/bin/bash
set -euo pipefail

printf '%q ' "$@" >> "$CALLS_FILE"
printf '\n' >> "$CALLS_FILE"

if [ "${1:-}" = "--version" ]; then
    echo "10.0.100"
elif [ "${1:-}" = "test" ]; then
    printf '\033[32mTest run summary: Passed!\033[0m\n\033[0m  total: 3\n'

    if [ -n "${FAKE_FAIL_PROJECT:-}" ]; then
        for argument in "$@"; do
            if [[ "$argument" == *"$FAKE_FAIL_PROJECT"* ]]; then
                exit 1
            fi
        done
    fi
fi
EOF
chmod +x "$FAKE_BIN/dotnet"

run_runner() {
    (
        cd "$TEST_ROOT"
        PATH="$FAKE_BIN:$PATH" \
            CALLS_FILE="$CALLS_FILE" \
            FAKE_FAIL_PROJECT="${FAKE_FAIL_PROJECT:-}" \
            ./scripts-run-tests.sh "$@"
    )
}

assert_contains() {
    local expected="$1"

    if ! grep -F -- "$expected" "$CALLS_FILE" >/dev/null; then
        echo "Expected dotnet invocation to contain: $expected" >&2
        cat "$CALLS_FILE" >&2
        exit 1
    fi
}

assert_not_contains() {
    local unexpected="$1"

    if grep -F -- "$unexpected" "$CALLS_FILE" >/dev/null; then
        echo "Unexpected dotnet invocation contained: $unexpected" >&2
        cat "$CALLS_FILE" >&2
        exit 1
    fi
}

assert_no_command() {
    local command="$1"

    if grep -E "^${command}( |$)" "$CALLS_FILE" >/dev/null; then
        echo "Unexpected dotnet command: $command" >&2
        cat "$CALLS_FILE" >&2
        exit 1
    fi
}

: > "$CALLS_FILE"
run_runner --lane fast --no-build

assert_contains "test --project tests/Graph.Core.Tests/Graph.Core.Tests.csproj"
assert_contains "test --project tests/Graph.Analyzers.Tests/Graph.Analyzers.Tests.csproj"
assert_not_contains "Graph.Age.Tests.csproj"
assert_not_contains "Graph.Neo4j.Tests.csproj"
assert_no_command "build"

: > "$CALLS_FILE"
run_runner \
    --lane fast \
    --project Graph.Core.Tests \
    --filter '/*/*/GraphTests/*' \
    --filter '/*/*/SerializationTests/*'

assert_contains "build tests/Graph.Core.Tests/Graph.Core.Tests.csproj --configuration Debug --verbosity normal"
assert_contains "test --project tests/Graph.Core.Tests/Graph.Core.Tests.csproj"
assert_contains "--filter-query /\*/\*/GraphTests/\* /\*/\*/SerializationTests/\*"
assert_not_contains "Graph.Analyzers.Tests.csproj"
assert_not_contains "cvoya-graph.sln"

: > "$CALLS_FILE"
run_runner \
    --lane fast \
    --project Graph.Core.Tests \
    --no-build \
    --coverage \
    --report-trx \
    --results-directory TestResults

assert_contains "--results-directory TestResults/graph-core-tests"
assert_contains "--coverage --coverage-output graph-core-tests.cobertura.xml --coverage-output-format cobertura"
assert_contains "--report-xunit-trx --report-xunit-trx-filename graph-core-tests.trx"

: > "$CALLS_FILE"
if FAKE_FAIL_PROJECT=Graph.Analyzers.Tests run_runner --lane fast --no-build --keep-going; then
    echo "Expected --keep-going to preserve the failing exit status." >&2
    exit 1
fi

assert_contains "test --project tests/Graph.Analyzers.Tests/Graph.Analyzers.Tests.csproj"
assert_contains "test --project tests/Graph.Core.Tests/Graph.Core.Tests.csproj"

: > "$CALLS_FILE"
run_runner --lane all --project tests/Graph.Age.Tests/Graph.Age.Tests.csproj --no-build

assert_contains "test --project tests/Graph.Age.Tests/Graph.Age.Tests.csproj"
assert_no_command "build"
assert_not_contains "Graph.Core.Tests.csproj"

: > "$CALLS_FILE"
if run_runner --lane fast --project Graph.Core.Tests --project Graph.Age.Tests; then
    echo "Expected every project selector to match the selected lane." >&2
    exit 1
fi

assert_no_command "build"
assert_no_command "test"

: > "$CALLS_FILE"
run_runner --lane age --age-versions 1.7.0,1.8.0 --no-build

age_runs=$(grep -c "test --project tests/Graph.Age.Tests/Graph.Age.Tests.csproj" "$CALLS_FILE")
if [ "$age_runs" -ne 2 ]; then
    echo "Expected the AGE project to run once per --age-versions entry (2 runs), got $age_runs." >&2
    cat "$CALLS_FILE" >&2
    exit 1
fi

assert_not_contains "Graph.Core.Tests.csproj"

: > "$CALLS_FILE"
run_runner --lane age --age-versions 1.8.0 --no-build --report-trx --results-directory TestResults

assert_contains "--results-directory TestResults/graph-age-tests-age-1-8-0"

: > "$CALLS_FILE"
if run_runner --lane age --age-versions not-a-version --no-build; then
    echo "Expected an invalid --age-versions value to be rejected." >&2
    exit 1
fi

assert_no_command "test"

: > "$CALLS_FILE"
sentinel_output=$(AGE18_CONNECTION_STRING='Host=db.internal;Port=5433;Username=u;Password=REVIEW_PASSWORD_SENTINEL;Database=graph' \
    run_runner --lane age --age-versions 1.8.0 --no-build)

assert_contains "test --project tests/Graph.Age.Tests/Graph.Age.Tests.csproj"

if grep -q "REVIEW_PASSWORD_SENTINEL" <<<"$sentinel_output"; then
    echo "Runner output leaked the connection-string password." >&2
    exit 1
fi

echo "run-tests.sh tests passed"
