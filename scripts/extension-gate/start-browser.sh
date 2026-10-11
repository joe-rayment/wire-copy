#!/usr/bin/env bash
# Starts a REAL, headful Chromium or Vivaldi for the extension gate. Never headless.
#
#   start-browser.sh <chromium|vivaldi> <display> <cdp-port> <proxy-port> [url...]
#
# Env in:  XDG_CONFIG_HOME and XDG_DATA_HOME  (both REQUIRED; exit 2 otherwise)
#          NOINET_LOG        default $XDG_DATA_HOME/noinet.log
#          BROWSER_PID_FILE  if set, the browser pid is written there
# The browser runs on the DEFAULT profile ($XDG_CONFIG_HOME/chromium or /vivaldi): no
# --user-data-dir, because the app registers its native-messaging host in that default location.
# That is why the temp-XDG guard exists: without it this would touch the real profile.
set -euo pipefail

usage() {
  echo "usage: $0 <chromium|vivaldi> <display> <cdp-port> <proxy-port> [url...]" >&2
  exit 2
}
[[ $# -ge 4 ]] || usage
kind="$1"; display="$2"; cdp="$3"; proxy="$4"; shift 4

if [[ -z "${XDG_CONFIG_HOME:-}" || -z "${XDG_DATA_HOME:-}" ]]; then
  echo "refusing to start: XDG_CONFIG_HOME and XDG_DATA_HOME must both be set to temp dirs" >&2
  echo "(this protects the real browser profile)" >&2
  exit 2
fi

case "$kind" in
  chromium) bin=/home/agent/.cache/ms-playwright/chromium-1194/chrome-linux/chrome ;;
  vivaldi)  bin=/home/agent/.local/opt/vivaldi/opt/vivaldi/vivaldi-bin ;;
  *) usage ;;
esac
[[ -x "$bin" ]] || { echo "browser binary not found: $bin" >&2; exit 2; }

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[[ -f "$here/noinet.so" ]] || "$here/make.sh" >&2
mkdir -p "$XDG_CONFIG_HOME" "$XDG_DATA_HOME"
log="${NOINET_LOG:-$XDG_DATA_HOME/noinet.log}"

# --no-sandbox is required here: this container has no unprivileged user namespaces, so
# Chromium aborts with "FATAL: No usable sandbox!" without it (observed).
args=(
  --no-sandbox
  --no-first-run --no-default-browser-check
  "--remote-debugging-port=$cdp"
  "--proxy-server=http://127.0.0.1:$proxy"
  --window-size=1400,900 --window-position=0,0
  --password-store=basic
  --disable-features=DisableLoadExtensionCommandLineSwitch
  # Without this, Vivaldi dies ~1 s after CDP comes up (measured, not guessed): the container's
  # /dev/shm is 64 MB and Vivaldi's UI renderer / font-data service fills it (df showed 63628 KB
  # used); the next allocation fails and the browser logs "FATAL ... font_data_service_impl.cc:373
  # Check failed: No space left on device (28)", then "UI Process abnormally terminates with
  # status 3" / "Quiting Vivaldi". Plain `vivaldi-bin` with NO shim, env or proxy dies the same way
  # (0/5 survived); with this flag /dev/shm stays at 0 KB and 6/6 survived. Not a noinet.so issue.
  # Harmless for Chromium (shared memory then lives under /tmp).
  --disable-dev-shm-usage
)

# GOOGLE_* = "no" silences Chromium's "Google API keys are missing" infobar, which would
# otherwise push the page down in every screenshot.
# Vivaldi on a fresh profile opens its full-window "Let's get you set up!" welcome tab and DROPS
# command-line URLs, so for Vivaldi the URLs are opened through CDP once it is up (see below).
urls=("$@")
[[ "$kind" != vivaldi ]] || set --

DISPLAY="$display" LD_PRELOAD="$here/noinet.so" NOINET_LOG="$log" \
  GOOGLE_API_KEY=no GOOGLE_DEFAULT_CLIENT_ID=no GOOGLE_DEFAULT_CLIENT_SECRET=no \
  "$bin" "${args[@]}" "$@" >"$XDG_DATA_HOME/browser-$kind.out" 2>&1 &
pid=$!
[[ -z "${BROWSER_PID_FILE:-}" ]] || echo "$pid" >"$BROWSER_PID_FILE"

for _ in $(seq 1 120); do
  if curl -fs --noproxy '*' -m 2 "http://127.0.0.1:$cdp/json/version" >/dev/null 2>&1; then
    if [[ "$kind" == vivaldi ]]; then
      # Wait for Vivaldi's own UI document, then open each URL as a new (active) tab. The welcome
      # tab stays behind it, in the background; nothing needs to be clicked through.
      node "$here/cdp.mjs" "$cdp" wait-target components/welcome 30000 || node "$here/cdp.mjs" "$cdp" wait-target main.html 30000 || true
      for u in "${urls[@]}"; do node "$here/cdp.mjs" "$cdp" new "$u" >/dev/null; done
    fi
    sleep 1.5
    kill -0 "$pid" 2>/dev/null || { echo "$kind died right after CDP came up; see $XDG_DATA_HOME/browser-$kind.out" >&2; exit 1; }
    echo "$kind up: pid=$pid cdp=$cdp proxy=$proxy display=$display noinet-log=$log"
    exit 0
  fi
  kill -0 "$pid" 2>/dev/null || { echo "$kind exited early; see $XDG_DATA_HOME/browser-$kind.out" >&2; exit 1; }
  sleep 0.25
done
echo "CDP on port $cdp did not answer within 30 s" >&2
exit 1
