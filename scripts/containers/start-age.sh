#!/bin/bash

# Copyright CVOYA LLC. Licensed under the Apache License, Version 2.0.
# See LICENSE in the project root for full license terms.

set -euo pipefail

usage() {
    cat <<'EOF'
Usage: ./scripts/containers/start-age.sh [command]

Commands:
  1.7 (default)   Start Apache AGE 1.7.0 as 'cvoya-age' on host port 5455
  1.8             Start Apache AGE 1.8.0 as 'cvoya-age18' on host port 5456
  all             Start both AGE versions
  stop [1.7|1.8|all]   Remove the selected containers (default: all)

Environment overrides:
  CONTAINER_RUNTIME=podman|docker
  AGE_CONTAINER_IMAGE / AGE_CONTAINER_NAME / AGE_PORT       (1.7 defaults)
  AGE18_CONTAINER_IMAGE / AGE18_CONTAINER_NAME / AGE18_PORT (1.8 defaults)

Defaults:
  1.7: apache/age:release_PG18_1.7.0  cvoya-age   port 5455
  1.8: apache/age:release_PG18_1.8.0  cvoya-age18 port 5456
EOF
}

runtime="${CONTAINER_RUNTIME:-}"
if [ -z "$runtime" ]; then
    if command -v podman >/dev/null 2>&1 && podman info >/dev/null 2>&1; then
        runtime=podman
    elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
        runtime=docker
    else
        echo "No usable Podman or Docker runtime found." >&2
        exit 1
    fi
fi

if [ "$runtime" != podman ] && [ "$runtime" != docker ]; then
    echo "CONTAINER_RUNTIME must be 'podman' or 'docker'." >&2
    exit 1
fi

start_age() {
    local label="$1"
    local image="$2"
    local container_name="$3"
    local host_port="$4"

    if "$runtime" ps -a --format '{{.Names}}' | grep -Fqx "$container_name"; then
        "$runtime" rm --force "$container_name" >/dev/null
    fi

    "$runtime" run --detach \
        --name "$container_name" \
        --publish "$host_port:5432" \
        --env POSTGRES_USER=postgres \
        --env POSTGRES_PASSWORD=postgres \
        --env POSTGRES_DB=postgres \
        "$image" >/dev/null

    local attempt=0
    until "$runtime" exec "$container_name" pg_isready -U postgres -d postgres >/dev/null 2>&1; do
        attempt=$((attempt + 1))
        if [ "$attempt" -ge 60 ]; then
            "$runtime" logs "$container_name" >&2
            echo "Apache AGE $label did not become ready in time." >&2
            exit 1
        fi
        sleep 2
    done

    "$runtime" exec "$container_name" \
        psql -v ON_ERROR_STOP=1 -U postgres -d postgres \
        -c 'CREATE EXTENSION IF NOT EXISTS age' >/dev/null

    echo "Apache AGE $label is ready on port $host_port (image $image)."
    echo "export AGE_CONNECTION_STRING='Host=localhost;Port=$host_port;Username=postgres;Password=postgres;Database=postgres'"
}

stop_age() {
    local container_name="$1"

    if "$runtime" ps -a --format '{{.Names}}' | grep -Fqx "$container_name"; then
        "$runtime" rm --force "$container_name" >/dev/null
        echo "Removed container $container_name."
    else
        echo "Container $container_name does not exist."
    fi
}

age17_image="${AGE_CONTAINER_IMAGE:-apache/age:release_PG18_1.7.0}"
age17_name="${AGE_CONTAINER_NAME:-cvoya-age}"
age17_port="${AGE_PORT:-5455}"

age18_image="${AGE18_CONTAINER_IMAGE:-apache/age:release_PG18_1.8.0}"
age18_name="${AGE18_CONTAINER_NAME:-cvoya-age18}"
age18_port="${AGE18_PORT:-5456}"

start_all() {
    start_age "1.7.0" "$age17_image" "$age17_name" "$age17_port"
    start_age "1.8.0" "$age18_image" "$age18_name" "$age18_port"
}

stop_all() {
    stop_age "$age18_name"
    stop_age "$age17_name"
}

case "${1:-1.7}" in
    1.7|1.7.*)
        start_age "1.7.0" "$age17_image" "$age17_name" "$age17_port"
        ;;
    1.8|1.8.*)
        start_age "1.8.0" "$age18_image" "$age18_name" "$age18_port"
        ;;
    all)
        start_all
        ;;
    stop)
        case "${2:-all}" in
            1.7|1.7.*) stop_age "$age17_name" ;;
            1.8|1.8.*) stop_age "$age18_name" ;;
            all) stop_all ;;
            *)
                usage
                exit 64
                ;;
        esac
        ;;
    -h|--help)
        usage
        ;;
    *)
        usage
        exit 64
        ;;
esac
