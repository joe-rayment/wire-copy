# WireCopy Desktop: single-window shell (real Chromium + owned terminal) — findings & plan

**Date:** 2026-07-11 · **Spike:** `scripts/electron-shell-spike/` · **Evidence:** `docs/qa/electron-shell-spike/` · **Status:** VIABLE — 19/19 outcome-asserted gates green on this box (headful Xvfb, linux-arm64) · **Beads:** epic `workspace-pdp8` (P1–P7: `workspace-ysmb`, `-4b14`, `-cnyl`, `-xjce`, `-y0bi`, `-vcpf`, `-mwer`)

## Verdict

The full-fledged-app premise holds **without violating any constraint**: one OS window that IS a
real headful Chromium (Electron), with the **unmodified WireCopy TUI** as the primary pane inside
an owned, fully styleable terminal, and the live page as a secondary real-engine pane. Focus
stealing is beaten in-process (proven under deliberate assault), the page pane renders at its
honest width, logins are typable, NYT/Google render un-walled, and the existing .NET browser
stack adopts the embedded pane via `ConnectOverCDPAsync` — so the app brain stays 100% .NET.

"Forked Chromium" literally is the wrong tool: a fork means building/patching Chromium forever for
zero benefit here. Electron IS the product-focused Chromium distribution — our UI, our keybindings,
no browser chrome — with the fork burden outsourced. (cmux didn't fork either; it embeds.)

## What the spike proved (all outcome-asserted, screenshots read)

| # | Claim | Evidence |
|---|---|---|
| A | TUI byte-for-byte in our styled pane; boot app-primary full-window | `a-boot.png`, buffer text asserts |
| B | Split keeps reader primary (58/42); page `innerWidth == pane width` — the "page wider than pane" bug class is structurally dead; TUI truly reflows (xterm cols == pty cols == 93) | `b-split-npr.png` |
| C | Focus assault (autofocus + `focus()` every 300ms + steal-on-load nav): 100% of keystrokes land in the TUI, thief pages log **zero** keys | `c-focus-assault.png` — URL bar `wm1x7`, page shows `keys: (none)` |
| D | Deliberate browser mode: page input receives typed credentials; Esc hands back; zero leakage both ways | `d-browser-mode.png` — page holds `user@example.com`, TUI bar `wm1x7k9` |
| E | Patchright .NET `ConnectOverCDPAsync` → adopts the pane, navigates it, evaluates JS | `e-dotnet-drove-pane.png`, probe stdout `TITLE=NPR…|MATH=42` |
| F | nytimes.com full render (460 links, no bot wall); Google sign-in form renders, no "browser not secure" wall (Chrome UA policy) | `f-nytimes.png`, `g-google.png` |
| GH | ghostty-web (real libghostty-vt WASM core) renders the launcher through the same seam | `h-ghostty.png` |

Never-headless is intact by construction: the shell IS a headful browser window. Hidden
fetch/prefetch contents are background tabs of a headful browser (same fingerprint, same profile —
exactly what the app does today with quiet background tabs), not a headless browser.

## Research answers (the user's questions)

- **cmux's approach:** cmux is a native Swift/AppKit macOS app using **libghostty** as its
  rendering library (it did not fork Ghostty and is not Electron). macOS-only — copying it
  literally would violate our platform-agnostic / verifiable-on-Linux rule.
- **libghostty status (mid-2026):** only `libghostty-vt` (VT parser/state, C API, cross-platform,
  API still unstable) is shipped; the full embeddable terminal view (GPU renderer, input, widgets)
  remains roadmap. `electron-libghostty` (native NSView overlay in Electron) exists but is a
  6-commit macOS-only preview.
- **The cross-platform realization of "cmux's approach" is `ghostty-web`** (Coder, 2.6k★, v0.4.0):
  libghostty-vt compiled to WASM with an xterm.js-compatible API — Coder's Mux (browser+terminal
  desktop app, macOS+Linux, released 2026-07-10) ships on it. Our spike renders the launcher
  through it (GH gate). 0.4.0 input-capture is still rough (manual textarea focus needed; keys
  dropped around focus churn), so **xterm.js is the Phase-1 terminal; the pane sits behind an
  xterm-shaped seam so ghostty-web is an import swap when it matures.**
- **Electron focus-steal bugs** (#42578 steal-after-load, #42922 no `focusable:false`): real, and
  beaten in-process with the three-layer doctrine (bounce + enforcement loop + **key forwarding**);
  the assault gate pins it forever. Forwarding is load-bearing: without it, keys drop during focus
  races (first spike run lost keys and a stray `q` quit the TUI).
- **Google sign-in in embedded browsers:** blocked when the UA advertises Electron; fixed with an
  app-wide Chrome UA (`app.userAgentFallback` + per-session), which the spike ships and F2 verifies
  at the form level. Cat-and-mouse risk stays on the risk register; fallback is site-native logins
  (NYT etc. all have them) — never headless, never a hidden window.

## Why this is not a rerun of the June failure

Every June casualty fought the platform; this inverts the direction of embedding:

| June attempt | Grave | This architecture |
|---|---|---|
| WebView.Avalonia native webview | uncatchable macOS abort, Linux GTK hang | no native webviews anywhere |
| CEF dlopen'd into the .NET/Avalonia Shell | static-TLS exhaustion on this box; mac packaging unproven | Electron is its own process; runs on this box today (proven) |
| PNG image-mirror pane | latency/fidelity ceiling | real WebContentsView — actual pixels, actual engine |
| X11 XReparentWindow embed | Linux-only | cross-platform by construction |
| Chrome extension overlay / side panel | Chrome caps the panel (~360px, no app-primary); overlay blanks on nav | we own the window; panel caps don't exist |
| Two OS windows (today's terminal + parked Chromium) | focus theft, macOS park clamps, window juggling (`CornerParker`, `TerminalRefocus`, osascript fights) | one window; "parking" and OS-level focus theft cease to exist as concepts |

The TUI is hosted **unmodified** (same binary as `./run`), so reader features can't regress by
construction, and the plain-terminal mode remains a supported product surface throughout.

## Architecture (target)

```
┌─ WireCopy Desktop (Electron, ONE window, real headful Chromium) ─────────────┐
│ ┌─ terminal pane (xterm.js seam; ghostty-web-ready) ─┐ ┌─ page pane ────────┐ │
│ │ node-pty → UNMODIFIED WireCopy.API TUI (all logic) │ │ WebContentsView    │ │
│ │ our font/theme/padding — zero user-terminal deps   │ │ (lens, visible)    │ │
│ └────────────────────────────────────────────────────┘ └────────────────────┘ │
│   hidden WebContents: fetch + prefetch (background tabs of a headful browser) │
└───────────────────────────────────────────────────────────────────────────────┘
        ▲ control channel (UDS JSON, WIRECOPY_DISPLAY_CHANNEL pattern —        ▲ CDP
          revive dormant WireCopy.DisplayChannel): reveal/hide/bounds/mode,    │
          create-hidden-page, CDP-port handshake                               │
   .NET WireCopy.API: BrowserOrchestrator drives panes via Patchright ConnectOverCDPAsync
   (BrowserSession gains an "attached" mode next to LaunchPersistentContextAsync)
```

- Focus doctrine as in the spike README (bounce + enforcement + forwarding + explicit browser
  mode with Esc handback). Reader-primary layout doctrine unchanged (`app-panel-primary`,
  boot full, `O` peek semantics carry over).
- One profile: Electron persistent partition replaces the Playwright profile dir (login once).
- Shell-mode retires `CornerParker`/`ParkPlanner`/`TerminalRefocus`/window juggling; terminal
  mode keeps them (both modes ship from the same tree).
- Packaging: electron-builder (mac dmg arm64/x64, linux) bundling the self-contained .NET API;
  sandbox ON in packages. The ~270MB in-app Chromium download disappears in shell mode — the
  shell IS the browser (installer-size win vs today).

## Phased plan (filed as beads — epic + dependency-chained children)

1. **P1 Shell skeleton in-repo** — window, term pane, pty of the Release TUI, resize, lifecycle,
   single-instance lock. Gate: launcher renders + typing + reflow (ported spike gates A/B).
2. **P2 Control channel** — revive `WireCopy.DisplayChannel` UDS JSON; CDP-port handshake;
   `O`-peek reveal/hide driven by `BrowserOrchestrator` (boot-full, first-list reveal-once
   semantics). Gate: O-peek parity headful.
3. **P3 BrowserSession attach mode** — `ConnectOverCDPAsync` to the shell; visible pane = lens;
   fetch/prefetch as hidden WebContents via channel; profile = persistent partition; UA policy.
   Gate: sidecar-e2e equivalent (navigate → reader + live pane + spotlight follows selection).
4. **P4 Focus doctrine port** — three layers + browser-mode + Esc handback + status-bar hint.
   Gate: focus-assault (ported spike C/D) in CI forever.
5. **P5 Login/session flows** — NYT in-pane login (browser mode), SSO popup routing
   (`setWindowOpenHandler` → temp pane), session survives restart. Gate: logged-in reader
   equivalent of `nytreader`.
6. **P6 Parity sweep + styling pass** — bundled font (kill the tofu class: braille/emoji coverage),
   theme/padding, frameless titlebar, wizard-screenshot path parity (CDP captureBeyondViewport);
   re-evaluate ghostty-web swap. Gate: full live-gate sweep green in shell mode + terminal mode.
7. **P7 Packaging** — electron-builder mac/linux artifacts bundling self-contained API; sandbox ON;
   `./run` terminal mode untouched. Gate: packaged app boots on a clean profile and passes P3 gate.

**Kill criteria (agreed up front):** any phase that cannot keep the focus-assault gate green, or
that would force headless, a dumbed-down reader, or feature loss → stop and reassess at that
phase boundary; the terminal app remains the shipping product throughout, so nothing is bet.

## Risks

- **Electron focus regressions across upgrades** — pinned by the assault gate in CI; doctrine is
  our code, not Electron behavior.
- **Google SSO heuristics drift** — UA policy today; site-native logins as fallback; revisit only
  if a real site's only login path breaks.
- **ghostty-web youth** — xterm.js default; seam kept swap-ready; revisit at their ≥0.5/1.0.
- **macOS specifics unverifiable here** — the doctrine is in-process and cross-platform (no
  osascript, no OS window juggling left); macOS validation happens by dogfooding the packaged
  app, not by parking beads on the user.

## Rejected (do not reopen without new facts)

Headless anything (hard law: `no-headless-browser-ever`); literal Chromium fork; CEF into the
.NET process; native OS webviews; Chrome extension overlay/side panel as app-primary; PNG mirror
panes; X11 reparenting as the cross-platform story; Tauri/wry & friends (system webviews — not
the real-Chrome engine, and the whole point is engine fidelity).
