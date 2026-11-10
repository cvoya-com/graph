#!/bin/bash

# CVOYA graph Test Runner
# Discovers repository test projects and runs the requested test lane.

set -euo pipefail

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
BLUE='\033[0;34m'
NC='\033[0m'

print_status() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

print_header() {
    echo -e "${BLUE}[HEADER]${NC} $1"
}

usage() {
    cat <<'EOF'
CVOYA graph Test Runner

Usage: ./scripts/run-tests.sh [options]

Options:
  -c, --configuration <config>  Build configuration (default: Debug)
  -v, --verbosity <level>       Build/test verbosity (default: normal)
  --lane <fast|neo4j|age|all>   Test lane (default: all)
  --fast                        Alias for --lane fast
  --project <name-or-path>      Run one test project (repeatable)
  --filter <xunit-query>        Apply an xUnit query filter (repeatable, OR)
  --coverage                    Collect Cobertura coverage per test project
  --report-trx                  Write an xUnit TRX report per test project
  --results-directory <path>   Root directory for coverage and test reports
  --keep-going                  Run every selected project after a test failure
  --neo4j                       Start the local Neo4j container before tests
  --age                         Start the local Apache AGE container(s) before tests
  --age-versions <csv>          Test the AGE lane once per AGE version (e.g. 1.7.0,1.8.0);
                                1.7 uses port ${AGE_PORT:-5455}, 1.8 uses port ${AGE18_PORT:-5456}
                                (override per version with AGE17_CONNECTION_STRING / AGE18_CONNECTION_STRING)
  --seq                         Start the local Seq container before tests
  --no-analyzers                Exclude analyzer tests
  --no-neo4j                    Exclude Neo4j tests from the all lane
  --no-age                      Exclude AGE tests from the all lane
  --no-build                    Reuse an existing build
  --disable-diff-engine         Keep Verify snapshot failures in terminal output
  --performance                 Run benchmarks after tests
  -h, --help                    Show this help message

Examples:
  ./scripts/run-tests.sh --fast
  ./scripts/run-tests.sh --lane neo4j --neo4j
  ./scripts/run-tests.sh --lane age --age
  ./scripts/run-tests.sh --lane age --age --age-versions 1.7.0,1.8.0
  ./scripts/run-tests.sh --neo4j --age
  ./scripts/run-tests.sh --fast --project Graph.Core.Tests
  ./scripts/run-tests.sh --fast --project Graph.Core.Tests --filter '/*/*/GraphTests/*'
  ./scripts/run-tests.sh --lane all --no-build --coverage --report-trx --results-directory TestResults --keep-going
  ./scripts/run-tests.sh --fast --disable-diff-engine
EOF
}

CONFIGURATION="Debug"
VERBOSITY="normal"
LANE="all"
COLLECT_COVERAGE=false
REPORT_TRX=false
RESULTS_DIRECTORY_ROOT=""
KEEP_GOING=false
START_NEO4J=false
START_AGE=false
START_SEQ=false
RUN_ANALYZERS=true
RUN_NEO4J=true
RUN_AGE=true
RUN_PERFORMANCE=false
BUILD_SOLUTION=true
DISABLE_DIFF_ENGINE=false
PROJECT_SELECTORS=()
TEST_FILTERS=()
AGE_VERSIONS=()

while [[ $# -gt 0 ]]; do
    case $1 in
        -c|--configuration)
            CONFIGURATION="${2:?--configuration requires a value}"
            shift 2
            ;;
        -v|--verbosity)
            VERBOSITY="${2:?--verbosity requires a value}"
            shift 2
            ;;
        --lane)
            LANE="${2:?--lane requires a value}"
            shift 2
            ;;
        --fast)
            LANE="fast"
            shift
            ;;
        --project)
            PROJECT_SELECTORS+=("${2:?--project requires a value}")
            shift 2
            ;;
        --filter)
            TEST_FILTERS+=("${2:?--filter requires a value}")
            shift 2
            ;;
        --coverage)
            COLLECT_COVERAGE=true
            shift
            ;;
        --report-trx)
            REPORT_TRX=true
            shift
            ;;
        --results-directory)
            RESULTS_DIRECTORY_ROOT="${2:?--results-directory requires a value}"
            shift 2
            ;;
        --keep-going)
            KEEP_GOING=true
            shift
            ;;
        --neo4j)
            START_NEO4J=true
            shift
            ;;
        --age)
            START_AGE=true
            shift
            ;;
        --age-versions)
            IFS=',' read -r -a AGE_VERSIONS <<< "${2:?--age-versions requires a value}"
            shift 2
            ;;
        --seq)
            START_SEQ=true
            shift
            ;;
        --no-analyzers)
            RUN_ANALYZERS=false
            shift
            ;;
        --no-neo4j)
            RUN_NEO4J=false
            shift
            ;;
        --no-age)
            RUN_AGE=false
            shift
            ;;
        --no-build)
            BUILD_SOLUTION=false
            shift
            ;;
        --disable-diff-engine)
            DISABLE_DIFF_ENGINE=true
            shift
            ;;
        --performance)
            RUN_PERFORMANCE=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            print_error "Unknown option: $1"
            usage
            exit 64
            ;;
    esac
done

case "$LANE" in
    fast|neo4j|age|all) ;;
    *)
        print_error "--lane must be fast, neo4j, age, or all."
        exit 64
        ;;
esac

if [ "$START_NEO4J" = true ] && [ "$RUN_NEO4J" = false ]; then
    print_error "--neo4j and --no-neo4j cannot be combined."
    exit 64
fi

if [ "$START_AGE" = true ] && [ "$RUN_AGE" = false ]; then
    print_error "--age and --no-age cannot be combined."
    exit 64
fi

for age_version in "${AGE_VERSIONS[@]}"; do
    if ! [[ "$age_version" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
        print_error "Invalid AGE version '$age_version' in --age-versions (expected e.g. 1.7.0 or 1.8.0)."
        exit 64
    fi
done

# Maps an AGE version to the host port its container publishes (see scripts/containers/start-age.sh).
age_port_for_version() {
    case "$1" in
        1.8*) echo "${AGE18_PORT:-5456}" ;;
        *) echo "${AGE_PORT:-5455}" ;;
    esac
}

# Builds the connection string for one AGE version. Per-version overrides win;
# a single --age-versions entry also honors an explicit AGE_CONNECTION_STRING.
age_connection_string_for() {
    local version="$1"
    local override=""

    case "$version" in
        1.7*) override="${AGE17_CONNECTION_STRING:-}" ;;
        1.8*) override="${AGE18_CONNECTION_STRING:-}" ;;
    esac

    if [ -z "$override" ] && [ "${#AGE_VERSIONS[@]}" -eq 1 ] && [ -n "${AGE_CONNECTION_STRING:-}" ]; then
        override="$AGE_CONNECTION_STRING"
    fi

    if [ -n "$override" ]; then
        echo "$override"
        return
    fi

    local port
    port=$(age_port_for_version "$version")
    echo "Host=localhost;Port=$port;Username=${AGE_USERNAME:-postgres};Password=${AGE_PASSWORD:-postgres};Database=${AGE_DATABASE:-postgres}"
}

print_header "CVOYA graph test runner"
print_status "Configuration: $CONFIGURATION"
print_status "Lane: $LANE"

if [ "${#PROJECT_SELECTORS[@]}" -gt 0 ]; then
    print_status "Projects: ${PROJECT_SELECTORS[*]}"
fi

if [ "${#TEST_FILTERS[@]}" -gt 0 ]; then
    print_status "xUnit query filters: ${TEST_FILTERS[*]}"
fi

if [ "${#AGE_VERSIONS[@]}" -gt 0 ]; then
    print_status "AGE versions: ${AGE_VERSIONS[*]}"
fi

if [ ! -f "cvoya-graph.sln" ] || [ ! -d "tests" ]; then
    print_error "Run this script from the repository root."
    exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
    print_error ".NET SDK not found."
    exit 1
fi

print_status ".NET SDK: $(dotnet --version)"

project_matches_selector() {
    local project="${1#./}"
    local selector="${2#./}"
    local project_name
    local project_stem

    project_name=$(basename "$project")
    project_stem="${project_name%.csproj}"

    case "$selector" in
        "$project"|"${project%.csproj}"|"$project_name"|"$project_stem")
            return 0
            ;;
    esac

    return 1
}

matches_project_selector() {
    local project="$1"
    local selector

    if [ "${#PROJECT_SELECTORS[@]}" -eq 0 ]; then
        return 0
    fi

    for selector in "${PROJECT_SELECTORS[@]}"; do
        project_matches_selector "$project" "$selector" && return 0
    done

    return 1
}

should_run_project() {
    local project="$1"
    local project_name

    project_name=$(basename "$project")

    matches_project_selector "$project" || return 1

    case "$project_name" in
        Graph.Performance.Tests.csproj)
            return 1
            ;;
        Graph.Analyzers.Tests.csproj)
            [ "$RUN_ANALYZERS" = true ] || return 1
            ;;
        Graph.Neo4j.Tests.csproj)
            [ "$RUN_NEO4J" = true ] || return 1
            case "$LANE" in
                neo4j|all) return 0 ;;
                *) return 1 ;;
            esac
            ;;
        Graph.Age.Tests.csproj)
            [ "$RUN_AGE" = true ] || return 1
            case "$LANE" in
                age|all) return 0 ;;
                *) return 1 ;;
            esac
            ;;
    esac

    case "$LANE" in
        fast|all) return 0 ;;
        *) return 1 ;;
    esac
}

SELECTED_PROJECTS=()

while IFS= read -r project; do
    if should_run_project "$project"; then
        SELECTED_PROJECTS+=("$project")
    fi
done < <(find tests -name '*.csproj' -print | LC_ALL=C sort)

if [ "${#PROJECT_SELECTORS[@]}" -gt 0 ]; then
    for selector in "${PROJECT_SELECTORS[@]}"; do
        selector_matched=false

        for project in "${SELECTED_PROJECTS[@]}"; do
            if project_matches_selector "$project" "$selector"; then
                selector_matched=true
                break
            fi
        done

        if [ "$selector_matched" = false ]; then
            print_error "Project selector '$selector' did not match a project in the selected lane."
            exit 1
        fi
    done
fi

if [ "${#SELECTED_PROJECTS[@]}" -eq 0 ]; then
    print_error "The selected lane and project filters did not contain any test projects."
    exit 1
fi

if [ "$START_NEO4J" = true ]; then
    print_header "Starting Neo4j"
    ./scripts/containers/start-neo4j.sh
    export NEO4J_URI="${NEO4J_URI:-bolt://localhost:7687}"
    export NEO4J_USER="${NEO4J_USER:-neo4j}"
    export NEO4J_PASSWORD="${NEO4J_PASSWORD:-password}"
fi

if [ "$START_AGE" = true ]; then
    print_header "Starting Apache AGE"
    if [ "${#AGE_VERSIONS[@]}" -gt 1 ]; then
        ./scripts/containers/start-age.sh all
    elif [ "${#AGE_VERSIONS[@]}" -eq 1 ]; then
        ./scripts/containers/start-age.sh "${AGE_VERSIONS[0]}"
    else
        ./scripts/containers/start-age.sh
    fi
    if [ "${#AGE_VERSIONS[@]}" -eq 0 ]; then
        export AGE_CONNECTION_STRING="${AGE_CONNECTION_STRING:-Host=localhost;Port=${AGE_PORT:-5455};Username=postgres;Password=postgres;Database=postgres}"
    fi
fi

if [ "$START_SEQ" = true ]; then
    print_header "Starting Seq"
    ./scripts/containers/start-seq.sh
fi

if [ "$DISABLE_DIFF_ENGINE" = true ]; then
    export DiffEngine_Disabled=true
fi

if [ "$BUILD_SOLUTION" = true ]; then
    if [ "${#PROJECT_SELECTORS[@]}" -gt 0 ]; then
        print_header "Building selected test projects"
        for project in "${SELECTED_PROJECTS[@]}"; do
            dotnet build "$project" \
                --configuration "$CONFIGURATION" \
                --verbosity "$VERBOSITY"
        done
    else
        print_header "Building solution"
        dotnet build cvoya-graph.sln \
            --configuration "$CONFIGURATION" \
            --verbosity "$VERBOSITY"
    fi
fi

run_test_project() {
    local project="$1"
    local slug_suffix="${2:-}"
    local project_name
    local slug
    local log_file
    local results_root
    local test_count
    local -a command

    project_name=$(basename "$project" .csproj)
    slug=$(printf '%s' "$project_name" | tr '[:upper:]' '[:lower:]' | tr '.' '-')
    if [ -n "$slug_suffix" ]; then
        slug="$slug-$slug_suffix"
    fi
    log_file=$(mktemp "${TMPDIR:-/tmp}/cvoya-test-output.XXXXXX")
    command=(
        dotnet test
        --project "$project"
        --configuration "$CONFIGURATION"
        --no-build
        --verbosity "$VERBOSITY"
    )

    results_root="$RESULTS_DIRECTORY_ROOT"
    if [ -z "$results_root" ] && [ "$COLLECT_COVERAGE" = true ]; then
        results_root="coverage"
    elif [ -z "$results_root" ] && [ "$REPORT_TRX" = true ]; then
        results_root="TestResults"
    fi

    if [ -n "$results_root" ]; then
        command+=(--results-directory "$results_root/$slug")
    fi

    if [ "$COLLECT_COVERAGE" = true ]; then
        command+=(
            --coverage
            --coverage-output "$slug.cobertura.xml"
            --coverage-output-format cobertura
        )
    fi

    if [ "$REPORT_TRX" = true ]; then
        command+=(
            --report-xunit-trx
            --report-xunit-trx-filename "$slug.trx"
        )
    fi

    if [ "${#TEST_FILTERS[@]}" -gt 0 ]; then
        command+=(--filter-query "${TEST_FILTERS[@]}")
    fi

    print_header "Testing $project_name${slug_suffix:+ ($slug_suffix)}"
    if ! "${command[@]}" 2>&1 | tee "$log_file"; then
        rm -f "$log_file"
        return 1
    fi

    test_count=$(
        sed $'s/\033\[[0-9;]*m//g' "$log_file" \
            | sed -nE 's/^[[:space:]]*total:[[:space:]]*([0-9]+)[[:space:]]*$/\1/p' \
            | tail -n 1
    )
    rm -f "$log_file"

    if [ -z "$test_count" ] || [ "$test_count" -eq 0 ]; then
        print_error "$project completed without reporting a nonzero test count."
        return 1
    fi

    SELECTED_TEST_COUNT=$((SELECTED_TEST_COUNT + test_count))
    SELECTED_PROJECT_COUNT=$((SELECTED_PROJECT_COUNT + 1))
}

SELECTED_PROJECT_COUNT=0
SELECTED_TEST_COUNT=0
TEST_FAILURE=false

run_age_project_for_versions() {
    local project="$1"
    local version
    local version_slug
    local failed=false

    for version in "${AGE_VERSIONS[@]}"; do
        export AGE_CONNECTION_STRING
        AGE_CONNECTION_STRING=$(age_connection_string_for "$version")
        version_slug=$(printf '%s' "$version" | tr '.' '-')
        print_status "AGE_CONNECTION_STRING for AGE $version: $AGE_CONNECTION_STRING"
        if ! run_test_project "$project" "age-$version_slug"; then
            failed=true
            if [ "$KEEP_GOING" = false ]; then
                return 1
            fi
        fi
    done

    [ "$failed" = false ]
}

for project in "${SELECTED_PROJECTS[@]}"; do
    project_name=$(basename "$project")
    if [ "$project_name" = "Graph.Age.Tests.csproj" ] && [ "${#AGE_VERSIONS[@]}" -gt 0 ]; then
        if ! run_age_project_for_versions "$project"; then
            TEST_FAILURE=true
            if [ "$KEEP_GOING" = false ]; then
                exit 1
            fi
        fi
        continue
    fi
    if ! run_test_project "$project"; then
        TEST_FAILURE=true
        if [ "$KEEP_GOING" = false ]; then
            exit 1
        fi
    fi
done

if [ "$TEST_FAILURE" = true ]; then
    print_error "One or more selected test projects failed."
    exit 1
fi

if [ "$RUN_PERFORMANCE" = true ]; then
    print_header "Running performance benchmarks"
    ./scripts/run-benchmarks.sh
fi

print_header "Test lane completed"
print_status "$SELECTED_PROJECT_COUNT project(s), $SELECTED_TEST_COUNT test(s)"

if [ "$COLLECT_COVERAGE" = true ]; then
    print_status "Coverage reports: ${RESULTS_DIRECTORY_ROOT:-coverage}/"
fi

if [ "$REPORT_TRX" = true ]; then
    print_status "TRX reports: ${RESULTS_DIRECTORY_ROOT:-TestResults}/"
fi
