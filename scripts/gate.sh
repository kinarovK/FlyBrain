#!/usr/bin/env bash
# Fast part of the FlyBrain gate (build, .NET tests, pytest) for Linux/CI.
# Real-brain tests need Windows + local FlyWire data: use scripts/gate.ps1.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PYTHON="${FLYBRAIN_PYTHON:-}"
if [[ -z "$PYTHON" ]]; then
  if [[ -x "$ROOT/.venv/bin/python" ]]; then PYTHON="$ROOT/.venv/bin/python"; else PYTHON="python3"; fi
fi

steps=(
  "build|dotnet build FlyBrain.sln -c Debug"
  "dotnet-test|dotnet test FlyBrain.sln --no-build"
  "pytest|$PYTHON -m pytest tests/python -q -p no:cacheprovider"
)

if [[ "${1:-}" == "--dry-run" ]]; then
  echo "FlyBrain gate (fast part, dry run)"
  for s in "${steps[@]}"; do echo "STEP ${s%%|*}: ${s#*|}"; done
  echo "STEP real-tests: skipped on Linux (use scripts/gate.ps1 on Windows)"
  exit 0
fi

cd "$ROOT"
for s in "${steps[@]}"; do
  name="${s%%|*}"; cmd="${s#*|}"
  echo "== $name: $cmd"
  if [[ "$name" == "build" || "$name" == "dotnet-test" ]] && ! command -v dotnet >/dev/null; then
    echo "   dotnet not found - skipped"; continue
  fi
  eval "$cmd"
done
echo "Gate (fast part) PASS"
