#!/usr/bin/env bash
# Keep always-loaded agent instructions within the repository context budget.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fail=0

check_size() {
  local rel="$1" limit="$2" bytes
  if [ ! -f "$ROOT/$rel" ]; then
    printf 'FAIL: missing %s\n' "$rel" >&2
    fail=1
    return
  fi
  bytes="$(wc -c <"$ROOT/$rel" | tr -d '[:space:]')"
  if [ "$bytes" -gt "$limit" ]; then
    printf 'FAIL: %s is %s bytes; context budget is %s bytes\n' "$rel" "$bytes" "$limit" >&2
    fail=1
  else
    printf 'PASS: %s context budget (%s/%s bytes)\n' "$rel" "$bytes" "$limit"
  fi
}

check_size AGENTS.md 8192
check_size CLAUDE.md 512

if ! grep -Fxq '@AGENTS.md' "$ROOT/CLAUDE.md"; then
  printf 'FAIL: CLAUDE.md must import the canonical AGENTS.md with @AGENTS.md\n' >&2
  fail=1
fi

legacy=""
if [ -d "$ROOT/.claude/rules" ]; then
  legacy="$(grep -R -n --include='*.md' '^globs:' "$ROOT/.claude/rules" 2>/dev/null || true)"
fi
if [ -n "$legacy" ]; then
  printf 'FAIL: Claude rules must use lazy paths: frontmatter, not legacy globs:\n%s\n' "$legacy" >&2
  fail=1
fi

[ "$fail" -eq 0 ]
