#!/usr/bin/env bash
# workspace-a2ml — repeatable end-to-end gate for the Wire Copy EXTENSION (host-browser-as-renderer).
#
# Builds WireCopy.API + WireCopy.Web, starts the backend in extension mode (no server-side browser),
# then drives a real bot-heavy site with a HEADFUL Chromium (under Xvfb) that has the unpacked
# extension loaded, and asserts the user-visible behaviour: single window, no bot block, overlay docks,
# DOM captured, backend extracts links. Exits non-zero on failure. Screenshots in $VERIFY_OUT.
#
# Usage:
#   scripts/verify-extension.sh                 # default site: macleans.ca (Cloudflare)
#   VERIFY_SITE=https://www.nytimes.com/section/world scripts/verify-extension.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
export PATH="$ROOT:$PATH"

# Default to 5099 — the extension's built-in default backend — so the loaded extension reaches this
# gate's backend with no per-profile chrome.storage override (which can't be set from the page world).
PORT="${WIRECOPY_WEB_PORT:-5099}"
export WIRECOPY_WEB_URL="http://127.0.0.1:${PORT}"
CFG="${WIRECOPY_BUILD_CONFIG:-Release}"
API_EXE="$ROOT/src/WireCopy.API/bin/$CFG/net10.0/WireCopy.API"
export VERIFY_OUT="${VERIFY_OUT:-/tmp/verify-ext}"
export VERIFY_EXT_DIR="$ROOT/extension"
export VERIFY_LOG_DIR="$ROOT/logs"

# A pinned Chromium with extension support (Playwright cache); override with WIRECOPY_CHROMIUM.
if [ -z "${WIRECOPY_CHROMIUM:-}" ]; then
  WIRECOPY_CHROMIUM="$(find "$HOME/.cache/ms-playwright" -path '*chromium-*/chrome-linux/chrome' -type f 2>/dev/null | sort | tail -1)"
fi
export WIRECOPY_CHROMIUM

cleanup() {
  for p in $(ss -ltnp 2>/dev/null | awk "/:${PORT} /{print}" | grep -oE 'pid=[0-9]+' | cut -d= -f2 | sort -u); do
    kill "$p" 2>/dev/null
  done
  pkill -x WireCopy.API 2>/dev/null
  pkill -x WireCopy.Web 2>/dev/null
  pkill -f "load-extension=$ROOT/extension" 2>/dev/null
}
trap cleanup EXIT
cleanup
sleep 1

echo "== build =="
./dotnet build src/WireCopy.API/WireCopy.API.csproj -c "$CFG" -v quiet || exit 1
./dotnet build src/WireCopy.Web/WireCopy.Web.csproj -c "$CFG" -v quiet || exit 1
./dotnet build tools/verify-ext/verify-ext.csproj -c "$CFG" -v quiet || exit 1

# Isolate this run's evidence: the WireCopy.API child appends to logs/wirecopy-<date>.log (shared
# across same-day runs), and a disk page-cache HIT would short-circuit the extension load path with a
# stale entry. Clear both so the gate proves a FRESH load THROUGH the extension.
rm -f "$ROOT"/logs/wirecopy-*.log 2>/dev/null
if [ "${VERIFY_KEEP_CACHE:-0}" != "1" ]; then
  rm -rf "$HOME/.local/share/WireCopy/page-cache" 2>/dev/null
fi

echo "== launch backend (extension mode, no server browser) =="
# Extension mode: WireCopy.Web spawns the WireCopy.API child under a PTY; the child uses the extension
# as its renderer (WIRECOPY_BROWSER=extension) instead of launching Playwright. No DISPLAY needed here.
WIRECOPY_BROWSER=extension \
WIRECOPY_NO_OPEN=1 \
WIRECOPY_TERMINAL_APP="$API_EXE" \
WIRECOPY_TERMINAL_CWD="$ROOT" \
  ./dotnet run --project src/WireCopy.Web/WireCopy.Web.csproj -c "$CFG" --no-build >/tmp/verify-ext-web.log 2>&1 &
WEB_PID=$!

echo "   waiting for backend on $WIRECOPY_WEB_URL ..."
for i in $(seq 1 30); do
  if curl -fsS -o /dev/null "$WIRECOPY_WEB_URL/" 2>/dev/null; then break; fi
  if ! kill -0 "$WEB_PID" 2>/dev/null; then echo "backend exited early:"; tail -20 /tmp/verify-ext-web.log; exit 1; fi
  sleep 1
done

echo "== drive headful Chromium with the extension loaded =="
# The driver launches a HEADFUL Chromium (extensions need it); provide a virtual display when none.
DRIVER=( ./dotnet run --project tools/verify-ext/verify-ext.csproj -c "$CFG" --no-build )
if [ -z "${DISPLAY:-}" ] && command -v xvfb-run >/dev/null 2>&1; then
  xvfb-run -a --server-args="-screen 0 1600x1200x24" "${DRIVER[@]}"
  rc=$?
else
  "${DRIVER[@]}"
  rc=$?
fi

echo "== backend log tail =="
tail -25 /tmp/verify-ext-web.log 2>/dev/null

exit $rc
