#!/usr/bin/env bash
# workspace-yqt5.2 / yqt5.3 / opb2 — headful gate for the LEGACY browser-hosted web shell launcher.
#
# Starts WireCopy.Web in legacy mode (no extension), then drives the served split-pane SPA
# (http://127.0.0.1:5099) with a HEADFUL Chromium under Xvfb and asserts what the user sees on the
# launcher home screen:
#   1. the web pane is COLLAPSED (never-empty law — nothing to show yet);  [yqt5.2]
#   2. the launcher shows the ASCII-art wordmark, not plain text.          [yqt5.3]
# A screenshot lands in $VERIFY_OUT for the opb2 stale-cells visual check. Exits non-zero on failure.
#
# Headful only (the user rejects headless): the driver Chromium runs under xvfb-run when no DISPLAY.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
export PATH="$ROOT:$PATH"

PORT="${WIRECOPY_WEB_PORT:-5099}"
export WIRECOPY_WEB_URL="http://127.0.0.1:${PORT}"
CFG="${WIRECOPY_BUILD_CONFIG:-Release}"
API_EXE="$ROOT/src/WireCopy.API/bin/$CFG/net10.0/WireCopy.API"
export VERIFY_OUT="${VERIFY_OUT:-/tmp/verify-webshell}"
export VERIFY_MODE="webshell"
export VERIFY_SITE="$WIRECOPY_WEB_URL/"

if [ -z "${WIRECOPY_CHROMIUM:-}" ]; then
  WIRECOPY_CHROMIUM="$(find "$HOME/.cache/ms-playwright" -path '*chromium-*/chrome-linux/chrome' -type f 2>/dev/null | sort | tail -1)"
fi
export WIRECOPY_CHROMIUM

XVFB_PID=""
cleanup() {
  for p in $(ss -ltnp 2>/dev/null | awk "/:${PORT} /{print}" | grep -oE 'pid=[0-9]+' | cut -d= -f2 | sort -u); do
    kill "$p" 2>/dev/null
  done
  pkill -x WireCopy.API 2>/dev/null
  pkill -x WireCopy.Web 2>/dev/null
  [ -n "$XVFB_PID" ] && kill "$XVFB_PID" 2>/dev/null
}
trap cleanup EXIT
cleanup
sleep 1

# Headful-only (the user rejects headless): give BOTH the backend's server-side content browser and the
# driver Chromium a shared virtual display, so the legacy screencast pane actually works — the realistic
# `./run --web` scenario, not a browser-less reconnect loop.
if [ -z "${DISPLAY:-}" ] && command -v Xvfb >/dev/null 2>&1; then
  Xvfb :99 -screen 0 1600x1200x24 >/tmp/verify-webshell-xvfb.log 2>&1 &
  XVFB_PID=$!
  export DISPLAY=:99
  sleep 1
fi

echo "== build =="
./dotnet build src/WireCopy.API/WireCopy.API.csproj -c "$CFG" -v quiet || exit 1
./dotnet build src/WireCopy.Web/WireCopy.Web.csproj -c "$CFG" -v quiet || exit 1
./dotnet build tools/verify-ext/verify-ext.csproj -c "$CFG" -v quiet || exit 1

rm -f "$ROOT"/logs/wirecopy-*.log 2>/dev/null

echo "== launch backend (LEGACY web shell, no extension) =="
# Legacy mode: no WIRECOPY_BROWSER=extension. Warmup is skipped in headed (web) mode, so the launcher
# renders with no server-side browser launch — exactly what we need for the launcher checks.
WIRECOPY_NO_OPEN=1 \
WIRECOPY_TERMINAL_APP="$API_EXE" \
WIRECOPY_TERMINAL_CWD="$ROOT" \
  ./dotnet run --project src/WireCopy.Web/WireCopy.Web.csproj -c "$CFG" --no-build >/tmp/verify-webshell-web.log 2>&1 &
WEB_PID=$!

echo "   waiting for backend on $WIRECOPY_WEB_URL ..."
for i in $(seq 1 30); do
  if curl -fsS -o /dev/null "$WIRECOPY_WEB_URL/" 2>/dev/null; then break; fi
  if ! kill -0 "$WEB_PID" 2>/dev/null; then echo "backend exited early:"; tail -20 /tmp/verify-webshell-web.log; exit 1; fi
  sleep 1
done

echo "== drive headful Chromium against the legacy SPA launcher =="
DRIVER=( ./dotnet run --project tools/verify-ext/verify-ext.csproj -c "$CFG" --no-build )
if [ -z "${DISPLAY:-}" ] && command -v xvfb-run >/dev/null 2>&1; then
  xvfb-run -a --server-args="-screen 0 1600x1200x24" "${DRIVER[@]}"
  rc=$?
else
  "${DRIVER[@]}"
  rc=$?
fi

echo "== backend log tail =="
tail -15 /tmp/verify-webshell-web.log 2>/dev/null
exit $rc
