#!/usr/bin/env bash
# M13 Phase 4 (#110), the milestone's completion gate: proves the packed nupkg is actually
# installable and runnable, unattended, with no live vendor auth. Unlike M11/M12's gated
# smoke-claude/smoke-mixed-vendor runbooks (real subscription auth, permanently human-run per
# CLAUDE.md's "Live-vendor smoke tests"), nothing here needs a live vendor: the `claude` binary the
# installed `aer` shells out to is a local stub that just satisfies the declared output contract,
# so this can run unattended in default CI. Invoked as `pixi run verify-pack` (depends on `pack`).
set -euo pipefail

PACK_DIR="$(cd bin/pack && pwd)"
STUB_DIR="$(mktemp -d)"
TASK_ROOT="$(mktemp -d)"
TASK_DIR="$TASK_ROOT/task"

cleanup() {
  dotnet tool uninstall --global aer >/dev/null 2>&1 || true
  rm -rf "$STUB_DIR" "$TASK_ROOT"
}
trap cleanup EXIT

# A stub `claude` binary ahead of the real one (if any) on PATH: `ClaudeWorkerAdapter` shell-wraps
# every invocation and reads AER_OUTPUT_DIR from the real process environment (not just the shell-
# expanded prompt text), so this satisfies the declared output contract without touching the
# network or needing vendor auth -- proving the packaged aer_core dispatch + adapter shell-wrapping
# work end to end from the installed global tool, the same proof-of-dispatch goal Phases 1/3 used
# ExitCode:127 for, but this time settling the step Succeeded instead of Failed.
#
# Windows-only (#1405): the adapter spawns the literal name "claude", which process creation
# resolves against PATHEXT (VendorCliPresence.cs) -- an extension-less file is not one of those
# extensions and would not be found at all, so the stub is a `.cmd`, not a POSIX shell script.
cat > "$STUB_DIR/claude.cmd" <<'STUB'
@echo off
mkdir "%AER_OUTPUT_DIR%" 2>nul
echo stub greeting from the pack round-trip check> "%AER_OUTPUT_DIR%\greeting"
STUB

WORKFLOW_FILE="$TASK_ROOT/workflow.json"
BINDINGS_FILE="$TASK_ROOT/bindings.json"

cat > "$WORKFLOW_FILE" <<'EOF'
{
  "WorkflowTemplateId": "pack-roundtrip",
  "WorkflowTemplateVersion": 1,
  "Steps": [
    {
      "StepId": "greet",
      "Worker": "greeter",
      "Inputs": [],
      "Outputs": ["greeting"],
      "DependsOn": [],
      "RetryPolicy": { "MaxAttempts": 1 }
    }
  ]
}
EOF

cat > "$BINDINGS_FILE" <<'EOF'
{
  "greeter": {
    "Adapter": "claude",
    "Contract": {
      "WorkerName": "greeter",
      "RequiredInputs": [],
      "ProducedOutputs": [{ "Name": "greeting" }],
      "OptionalMetadata": []
    },
    "PromptTemplate": "Write a one-sentence greeting.",
    "Timeout": "00:02:00"
  }
}
EOF

dotnet tool uninstall --global aer >/dev/null 2>&1 || true
dotnet tool install --global --add-source "$PACK_DIR" aer

export PATH="$HOME/.dotnet/tools:$STUB_DIR:$PATH"

aer run "$WORKFLOW_FILE" --bindings "$BINDINGS_FILE" --room-dir "$TASK_DIR"

OUTPUT_FILE=$(find "$TASK_DIR/artifacts" -type f -name greeting -print -quit)
if [ -z "$OUTPUT_FILE" ] || [ ! -s "$OUTPUT_FILE" ]; then
  echo "Expected a non-empty 'greeting' output under $TASK_DIR/artifacts -- found none." >&2
  exit 1
fi

echo "Pack round-trip check passed: $OUTPUT_FILE"
