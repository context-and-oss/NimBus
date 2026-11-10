#!/usr/bin/env bash
# Run one audit (docs/spec/2026-11-10-audit-loop-design.md) and file its findings on the
# AgentFactory board as backlog tasks in the `nimbus` workspace.
#
#   tools/audit.sh <area> <lens> [--dry-run]
#   tools/audit.sh resolver bugs --dry-run
#
# Needs an AgentFactory MCP config pinned to the nimbus workspace; set AGENTFACTORY_MCP_CONFIG
# or keep the default path. Runs against the checkout it is started in, so run it on a clean
# checkout of master, not a branch you are editing.
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "usage: tools/audit.sh <area> <lens> [--dry-run]" >&2
  exit 2
fi

repo="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
mcp="${AGENTFACTORY_MCP_CONFIG:-$HOME/.config/agentfactory/mcp-nimbus.json}"
logs="${AUDIT_LOG_DIR:-$HOME/.config/agentfactory/logs}"
[[ -f "$mcp" ]] || { echo "AgentFactory MCP config not found: $mcp" >&2; exit 1; }
mkdir -p "$logs"

cd "$repo"
if [[ -n "$(git status --porcelain)" ]]; then
  echo "warning: the working tree has uncommitted changes; findings will reflect them" >&2
fi

log="$logs/audit-$(date +%F-%H%M)-$1-$2.json"
claude -p "/audit $*" \
  --model claude-fable-5-2 --effort high \
  --mcp-config "$mcp" \
  --allowedTools "Read" "Grep" "Glob" "Agent" \
    "Bash(git log:*)" "Bash(git show:*)" "Bash(git blame:*)" "Bash(git rev-parse:*)" \
    "Bash(dotnet build:*)" "Bash(dotnet test:*)" \
    "mcp__agentfactory__list_tasks" "mcp__agentfactory__get_task" "mcp__agentfactory__create_task" \
  --output-format json > "$log"

node -e 'const r=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8"));console.log(r.result??r);console.error(`\ncost $${(r.total_cost_usd??0).toFixed(2)}, log ${process.argv[1]}`)' "$log"
