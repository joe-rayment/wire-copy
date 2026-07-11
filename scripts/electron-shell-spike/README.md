# Single-window shell spike (Electron + owned terminal pane)

Proves the combined browser+terminal app premise: ONE real headful Chromium window (Electron)
hosting the **unmodified WireCopy TUI** in an owned terminal pane (xterm.js; ghostty-web variant
included) beside a **real-engine page pane** (WebContentsView). Never headless.

Result: **19/19 gates green** on 2026-07-11 (this box, linux-arm64, headful under Xvfb).
Evidence: `docs/qa/electron-shell-spike/` (screenshots + results.json).
Plan + findings: `docs/single-window-shell-plan.md`. Beads: epic `workspace-pdp8` (P1–P7 chained).

## What each gate proves

| Phase | Proof |
|---|---|
| A | TUI launcher renders inside our styled pane; boot is app-primary (terminal full-window) |
| B | Page pane reveals split with reader primary; live text.npr.org `innerWidth == pane width` (no clip); TUI truly reflows (xterm cols == pty cols) |
| C | Focus assault (autofocus + 300ms focus() loop + steal-on-load navigation): every keystroke lands in the TUI, thief pages see **zero** keys, TUI stays alive |
| D | Explicit browser mode: typing reaches the page input (login credentials work); Esc hands back; no key leakage either direction |
| E | .NET Patchright `ConnectOverCDPAsync` adopts the embedded pane and drives it (goto + evaluate) — the existing BrowserSession integration seam works |
| F | nytimes.com renders un-bot-walled (460 links); Google sign-in form renders with no "browser not secure" wall (Chrome UA policy) |
| GH | ghostty-web (libghostty-vt WASM core) renders the launcher via the same seam; 0.4.0 input capture still rough — xterm.js stays the default, seam kept swap-ready |

## Run it

```bash
cd scripts/electron-shell-spike
npm install                     # downloads Electron (~200MB once)
npx electron-rebuild -f -w node-pty   # node-pty must match Electron's ABI
../../dotnet build probe/probe.csproj -c Debug
# needs a Debug build of the TUI: ../../dotnet build src/WireCopy.API/WireCopy.API.csproj -c Debug
PROBE_BIN="$PWD/probe/bin/Debug/net10.0/probe.dll" ./run.sh   # Xvfb :77 + openbox + gates
# ghostty-web variant:
#   SPIKE_TERM_HTML=term-ghostty.html SPIKE_TERM_HTTP=1 ... node gate-ghostty.mjs (see git history of runs)
```

Notes for this container: `--no-sandbox` is required (no setuid sandbox) — packaged builds keep the
sandbox ON. xdotool + openbox must be installed (they inject REAL OS-level keys; CDP key dispatch
would bypass focus routing and fake the focus results).

## Focus doctrine implemented in `main.js` (port this, don't re-derive it)

1. **Bounce**: any page-side focus gain in reader mode → refocus terminal (incl. after
   `did-finish-load` / `did-navigate` — electron#42578 steals focus after load).
2. **Enforcement loop**: renderer-internal `focus()` calls emit no event — 150ms poll restores
   terminal focus while in reader mode.
3. **Forwarding (the load-bearing one)**: `before-input-event` on the page in reader mode is
   `preventDefault()`ed and the key is **re-sent to the terminal** via `sendInputEvent` — a
   keystroke is never lost even if the page wins a focus race for a tick. (First run without
   forwarding dropped keys and one stray `q` quit the TUI.)
4. **Browser mode** is an explicit toggle; Esc intercepts back to reader. The page only ever
   sees keys in browser mode.
