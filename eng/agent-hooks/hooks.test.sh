#!/usr/bin/env bash
# Self-test for the PreToolUse hook and the pre-push gate. Run from the repo root:
#   bash eng/agent-hooks/hooks.test.sh
set -u
cd "$(dirname "$0")/../.." || exit 1
REPO_ROOT="$(pwd)"
export CLAUDE_PROJECT_DIR="$REPO_ROOT"

bash eng/agent-hooks/context-budget.test.sh || exit 1

pass=0 fail=0
check() { # description expected_exit json script
  local desc="$1" expected="$2" json="$3" script="$4"
  printf '%s' "$json" | bash "$script" >/dev/null 2>&1
  local actual=$?
  if [ "$actual" -eq "$expected" ]; then
    pass=$((pass+1)); echo "PASS: $desc"
  else
    fail=$((fail+1)); echo "FAIL: $desc (expected exit $expected, got $actual)"
  fi
}

P=eng/agent-hooks/protect-files.sh
check "blocks VERSION (absolute path)"        2 "{\"tool_input\":{\"file_path\":\"$CLAUDE_PROJECT_DIR/VERSION\"}}" "$P"
check "blocks .github workflow"               2 "{\"tool_input\":{\"file_path\":\"$CLAUDE_PROJECT_DIR/.github/workflows/tests.yml\"}}" "$P"
check "blocks Directory.Build.props"          2 "{\"tool_input\":{\"file_path\":\"$CLAUDE_PROJECT_DIR/Directory.Build.props\"}}" "$P"
check "blocks .claude settings"               2 "{\"tool_input\":{\"file_path\":\"$CLAUDE_PROJECT_DIR/.claude/settings.json\"}}" "$P"
check "blocks .codex config"                  2 "{\"tool_input\":{\"file_path\":\"$CLAUDE_PROJECT_DIR/.codex/config.toml\"}}" "$P"
CLAUDE_PROJECT_DIR="$REPO_ROOT/scripts" check "blocks from a nested project dir" 2 "{\"tool_input\":{\"file_path\":\"$REPO_ROOT/Directory.Build.props\"}}" "$P"
check "blocks a dot-segment traversal"        2 "{\"tool_input\":{\"file_path\":\"$REPO_ROOT/scripts/../Directory.Build.props\"}}" "$P"
check "allows normal source file"             0 "{\"tool_input\":{\"file_path\":\"$CLAUDE_PROJECT_DIR/src/Graph/IGraph.cs\"}}" "$P"
check "allows file merely containing VERSION" 0 "{\"tool_input\":{\"file_path\":\"$CLAUDE_PROJECT_DIR/docs/VERSIONING.md\"}}" "$P"
check "allows nested Directory.Build.props"   0 "{\"tool_input\":{\"file_path\":\"$CLAUDE_PROJECT_DIR/src/Directory.Build.props\"}}" "$P"
check "allows empty input"                    0 "{}" "$P"

# The pre-push gate skips work when every pushed ref is a deletion (all-zero
# local oid); a real update instead execs eng/ci/ci-local.sh, so only the
# deletion-skip path is exercised here (running the gate needs the toolchain).
H=.githooks/pre-push
Z=0000000000000000000000000000000000000000
check "pre-push skips a pure branch deletion"  0 "(delete) $Z refs/heads/deleted-branch a1b2c3d4e5f60718293a4b5c6d7e8f9012345678" "$H"

echo "----"
echo "$pass passed, $fail failed"
[ $fail -eq 0 ]
