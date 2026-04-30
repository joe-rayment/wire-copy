#!/usr/bin/env bash
# Test runner for WireCopy
# Usage:
#   ./scripts/test.sh                      # Run full suite (~90s)
#   ./scripts/test.sh --filter "PageLoader" # Run specific tests
#   ./scripts/test.sh --browser            # Browser-related tests only
#   ./scripts/test.sh --podcast            # Podcast-related tests only
#
# Uses TRX logger for reliable results even when test host crashes during shutdown.

set -uo pipefail

DOTNET="${DOTNET:-dotnet}"
PROJECT="tests/WireCopy.Tests/WireCopy.Tests.csproj"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
RESULTS_DIR=$(mktemp -d /tmp/wirecopy-test-XXXXXX)

cd "$ROOT_DIR"

cleanup() { rm -rf "$RESULTS_DIR"; }
trap cleanup EXIT

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

run_tests() {
    local label="$1"
    shift
    echo -e "${YELLOW}Running: ${label}${NC}"

    # Run tests with TRX logger (written by vstest runner, survives test host crash).
    # timeout --signal=KILL prevents indefinite hang if test host doesn't exit.
    timeout --signal=KILL 180 $DOTNET test "$PROJECT" --no-build \
        --results-directory "$RESULTS_DIR" \
        --logger "trx;LogFileName=results.trx" \
        -v q "$@" 2>&1 || true

    local trx="$RESULTS_DIR/results.trx"
    if [[ ! -f "$trx" ]]; then
        echo -e "${RED}✗ ${label}: no test results (TRX file not created)${NC}"
        return 1
    fi

    # Parse TRX counters
    local passed failed total
    total=$(grep -oP 'total="\K\d+' "$trx" | head -1)
    passed=$(grep -oP 'passed="\K\d+' "$trx" | head -1)
    failed=$(grep -oP 'failed="\K\d+' "$trx" | head -1)

    local status_line="Passed: ${passed:-0}, Total: ${total:-0}"
    [[ "${failed:-0}" != "0" ]] && status_line="Passed: ${passed:-0}, Failed: ${failed}, Total: ${total:-0}"

    if [[ "${failed:-0}" == "0" ]]; then
        echo -e "${GREEN}✓ ${label}: ${status_line}${NC}"
        return 0
    else
        # Show failing test names from TRX
        grep -oP 'testName="\K[^"]+(?="[^>]*outcome="Failed")' "$trx" | while read -r name; do
            echo "  FAIL: $name"
        done
        echo -e "${RED}✗ ${label}: ${status_line}${NC}"
        return 1
    fi
}

# Build once
echo -e "${YELLOW}Building...${NC}"
if ! $DOTNET build "$PROJECT" -v q 2>&1 | tail -3; then
    echo -e "${RED}Build failed${NC}"
    exit 1
fi
echo ""

case "${1:-}" in
    --filter)
        shift
        run_tests "Filter: $*" --filter "$@"
        ;;
    --browser)
        run_tests "Browser tests" --filter "FullyQualifiedName~Browser"
        ;;
    --podcast)
        run_tests "Podcast tests" --filter "FullyQualifiedName~Podcast"
        ;;
    --help|-h)
        echo "Usage: $0 [--filter EXPR|--browser|--podcast]"
        echo ""
        echo "  (default)    Full test suite (~90s)"
        echo "  --filter X   Pass filter to dotnet test"
        echo "  --browser    Browser-related tests"
        echo "  --podcast    Podcast-related tests"
        ;;
    *)
        run_tests "Full suite"
        ;;
esac
