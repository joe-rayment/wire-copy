#!/usr/bin/env bash
# G5 real-world verification gate for the browser-hosted shell.
#
# Builds the web host + API, launches the host with the unmodified WireCopy.API spawned under a PTY,
# drives the resulting single tab with a real headless Chromium, and asserts what the user sees:
# the TUI renders, the web pane follows the TUI's navigation (the API's own browser streamed in),
# and a forwarded click changes the live page. Exits non-zero on failure. Screenshots land in
# $SPIKE_OUT (default /tmp): gate-1-followed.png, gate-2-clicked.png.
#
# Designed to run headless in CI / a container: a pinned Chromium (WIRECOPY_CHROMIUM_EXECUTABLE or a
# build in PLAYWRIGHT_BROWSERS_PATH) avoids any network download.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
export PATH="$ROOT:$PATH"

PORT="${WIRECOPY_WEB_PORT:-5099}"
URL="http://127.0.0.1:${PORT}"
CHROMIUM="${WIRECOPY_CHROMIUM_EXECUTABLE:-/opt/pw-browsers/chromium-1194/chrome-linux/chrome}"
CFG="${WIRECOPY_BUILD_CONFIG:-Debug}"
API_EXE="$ROOT/src/WireCopy.API/bin/$CFG/net10.0/WireCopy.API"

cleanup() {
  for p in $(ss -ltnp 2>/dev/null | awk "/:${PORT} /{print}" | grep -oE 'pid=[0-9]+' | cut -d= -f2 | sort -u); do
    kill "$p" 2>/dev/null
  done
  # Kill stray app processes by exact process name (avoids self-matching this script's cmdline).
  pkill -x WireCopy.API 2>/dev/null
  pkill -x WireCopy.Web 2>/dev/null
  pkill -x chrome 2>/dev/null
  rm -f "$HOME/.local/share/WireCopy/browser-profile"/Singleton* 2>/dev/null
}

trap cleanup EXIT
cleanup
sleep 1

echo "== build =="
./dotnet build src/WireCopy.API/WireCopy.API.csproj -c "$CFG" -v quiet || exit 1
./dotnet build src/WireCopy.Web/WireCopy.Web.csproj -c "$CFG" -v quiet || exit 1

echo "== launch web host =="
# workspace-8ne3: the API's content browser now runs HEADFUL and never falls back to headless. It
# therefore needs an X server. When none is attached (CI / containers) provide a virtual display via
# xvfb-run, mirroring the Dockerfile entrypoint, so the headful Chromium can start. On a host that
# already has a display (DISPLAY set) — or macOS, where headful needs no X server — launch directly.
XVFB=()
if [ -z "${DISPLAY:-}" ] && command -v xvfb-run >/dev/null 2>&1; then
  XVFB=(xvfb-run -a --server-args="-screen 0 1280x2400x24")
  echo "   (no DISPLAY; running the web host under xvfb-run for the headful content browser)"
fi
WIRECOPY_WEB_URL="$URL" \
WIRECOPY_TERMINAL_APP="$API_EXE" \
WIRECOPY_TERMINAL_CWD="$ROOT" \
WIRECOPY_CHROMIUM_EXECUTABLE="$CHROMIUM" \
  "${XVFB[@]}" ./dotnet run --project src/WireCopy.Web/WireCopy.Web.csproj -c "$CFG" --no-build > /tmp/verify-webshell.log 2>&1 &
disown

for i in $(seq 1 20); do
  [ "$(curl -s -o /dev/null -w '%{http_code}' "$URL/" 2>/dev/null)" = "200" ] && break
  sleep 1
done
if [ "$(curl -s -o /dev/null -w '%{http_code}' "$URL/" 2>/dev/null)" != "200" ]; then
  echo "web host failed to start; log:"; tail -20 /tmp/verify-webshell.log; exit 1
fi

echo "== drive + assert =="
SPIKE_OUT="${SPIKE_OUT:-/tmp}" WIRECOPY_WEB_URL="$URL" WIRECOPY_CHROMIUM="$CHROMIUM" \
  ./dotnet run --project tools/spike-verify/spike-verify.csproj -c "$CFG"
RESULT=$?

exit $RESULT
