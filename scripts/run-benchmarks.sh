#!/bin/bash

# CVOYA graph Performance Benchmarks Runner
# Usage: ./scripts/run-benchmarks.sh [options]

set -e

# Default values
MODE="all"
OUTPUT_DIR="./benchmarks"
SHOW_HELP=false

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -m|--mode)
            MODE="$2"
            shift 2
            ;;
        -o|--output)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        -h|--help)
            SHOW_HELP=true
            shift
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

if [ "$SHOW_HELP" = true ]; then
    echo "CVOYA graph Performance Benchmarks Runner"
    echo ""
    echo "Usage: ./scripts/run-benchmarks.sh [options]"
    echo ""
    echo "Options:"
    echo "  -m, --mode <mode>      Benchmark mode: all, crud, relationships, or interactive (default: all)"
    echo "  -o, --output <dir>     Output directory for results (default: ./benchmarks)"
    echo "  -h, --help             Show this help message"
    echo ""
    echo "Examples:"
    echo "  ./scripts/run-benchmarks.sh                        # Run all benchmarks"
    echo "  ./scripts/run-benchmarks.sh --mode crud            # Run only CRUD benchmarks"
    echo "  ./scripts/run-benchmarks.sh --mode interactive     # Interactive selection"
    exit 0
fi

# Ensure we're in the right directory
PROJECT_DIR="tests/Graph.Neo4j.Performance.Tests"
if [ ! -d "$PROJECT_DIR" ]; then
    echo "Error: Performance test project not found. Make sure you're running from the repository root."
    exit 1
fi

# Build the project first
echo "🔨 Building performance tests..."
dotnet build --configuration Benchmark

# Prepare arguments based on mode
ARGS=()
MODE_LOWER=$(echo "$MODE" | tr '[:upper:]' '[:lower:]')
case $MODE_LOWER in
    "all")
        ARGS+=("--all")
        echo "🚀 Running all benchmarks..."
        ;;
    "crud")
        ARGS+=("--filter" "*CrudOperations*")
        echo "🚀 Running CRUD operation benchmarks..."
        ;;
    "relationships")
        ARGS+=("--filter" "*Relationship*")
        echo "🚀 Running relationship benchmarks..."
        ;;
    "interactive")
        echo "🚀 Starting interactive benchmark selection..."
        ;;
    *)
        echo "Error: Invalid mode: $MODE. Use 'all', 'crud', 'relationships', or 'interactive'"
        exit 1
        ;;
esac

# Add common arguments
ARGS+=("--artifacts" "$OUTPUT_DIR")
ARGS+=("--exporters" "html" "json" "markdown")

# Run the benchmarks
echo "🏃‍♂️ Running benchmarks with arguments: ${ARGS[*]}"
dotnet run --project "$PROJECT_DIR" --configuration Benchmark --no-build -- "${ARGS[@]}"

echo ""
echo "✅ Benchmarks completed successfully!"
echo "📊 Results available in: $OUTPUT_DIR" 