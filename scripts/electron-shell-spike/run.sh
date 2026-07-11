#!/bin/bash
# Spike gate orchestrator: fresh Xvfb + openbox + the Electron shell + gate.mjs.
# Never headless — the shell is a real headful window on a real (virtual) display.
set -uo pipefail
cd "$(dirname "$0")"
D="${SPIKE_DISPLAY:-:77}"
PORT="${SPIKE_CDP_PORT:-9333}"
PY_SLEEP='python3 -c "import time,sys;time.sleep(float(sys.argv[1]))"'

mkdir -p out
rm -f out/*.png out/results.json

# Fresh display of our own; kill only what we started.
Xvfb "$D" -screen 0 1600x1000x24 >/dev/null 2>&1 &
XV=$!
python3 -c "import time;time.sleep(1.0)"
DISPLAY="$D" openbox >/dev/null 2>&1 &
OB=$!
python3 -c "import time;time.sleep(0.8)"

DISPLAY="$D" ELECTRON_DISABLE_SECURITY_WARNINGS=1 SPIKE_CDP_PORT="$PORT" \
  ./node_modules/.bin/electron --no-sandbox . >out/electron.log 2>&1 &
EL=$!

SPIKE_DISPLAY="$D" SPIKE_CDP_PORT="$PORT" PROBE_BIN="${PROBE_BIN:-}" node gate.mjs
RC=$?

kill "$EL" "$OB" "$XV" 2>/dev/null
wait 2>/dev/null
exit $RC
