#!/usr/bin/env bash
#
# Generate a test-coverage report for the SofaBuffers C# library using coverlet
# (the coverlet.collector package referenced by the test project) and, if
# available, an HTML report via ReportGenerator.
#
# Prerequisites (one-time, for the HTML report):
#   dotnet tool install -g dotnet-reportgenerator-globaltool
#
# Usage:
#   ./coverage.sh            # run tests with coverage -> Cobertura + terminal summary
#   ./coverage.sh --open     # also open the HTML report in a browser
#
set -euo pipefail
cd "$(dirname "$0")"

OUT_DIR="coverage"
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

echo ">> Running tests with coverage instrumentation ..."
# coverlet writes coverage.cobertura.xml under a per-run TestResults GUID dir.
dotnet test SofaBuffers.sln \
  --collect:"XPlat Code Coverage" \
  --settings coverlet.runsettings \
  --results-directory "$OUT_DIR/raw"

# Locate the freshly produced Cobertura report.
COBERTURA="$(find "$OUT_DIR/raw" -name 'coverage.cobertura.xml' | head -n1)"
cp "$COBERTURA" "$OUT_DIR/coverage.cobertura.xml"
echo ">> Cobertura: $OUT_DIR/coverage.cobertura.xml"

# Line-rate -> percentage for a quick terminal summary (no extra tooling needed).
RATE="$(grep -o 'line-rate="[0-9.]*"' "$OUT_DIR/coverage.cobertura.xml" | head -n1 | grep -o '[0-9.]*')"
PCT="$(awk "BEGIN { printf \"%.1f\", $RATE * 100 }")"
echo ">> line coverage: ${PCT}%"

# Optional detailed HTML report if ReportGenerator is installed.
if command -v reportgenerator >/dev/null 2>&1; then
  reportgenerator \
    -reports:"$OUT_DIR/coverage.cobertura.xml" \
    -targetdir:"$OUT_DIR/html" \
    -reporttypes:"Html;TextSummary" >/dev/null
  echo ">> HTML report: $OUT_DIR/html/index.html"
  if [[ "${1:-}" == "--open" ]]; then
    xdg-open "$OUT_DIR/html/index.html" >/dev/null 2>&1 || true
  fi
else
  echo ">> (install dotnet-reportgenerator-globaltool for an HTML report)"
fi
