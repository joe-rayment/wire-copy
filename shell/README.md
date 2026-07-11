# WireCopy Desktop — single-window shell

One real headful Chromium window (Electron) hosting the **unmodified WireCopy TUI** in an owned,
fully styleable terminal pane (xterm.js; ghostty-web-ready seam), with live pages as secondary
`WebContentsView` panes. Never headless. Plan/evidence: `docs/single-window-shell-plan.md`,
epic `workspace-pdp8`.

## Run

```bash
./run --desktop         # builds the TUI (Release), installs the shell on first run, launches
```

Container/CI (no setuid sandbox): `WIRECOPY_SHELL_NO_SANDBOX=1 ./run --desktop` — packaged and
normal desktop runs keep the Chromium sandbox ON.

Env knobs: `WIRECOPY_SHELL_CDP_PORT` (default 9223, loopback CDP for the .NET driver and gates),
`WIRECOPY_SHELL_TUI_DLL`, `WIRECOPY_SHELL_DOTNET`.

## Design contracts (do not regress)

- **Reader-primary**: boots full-window TUI; the page pane is revealed on demand (`|` semantics
  arrive with the control channel) at a fixed minority fraction — never dominant.
- **Focus doctrine, three layers** (empirically derived — see `scripts/electron-shell-spike/README.md`):
  bounce, enforcement loop, and **key forwarding** (a keystroke that lands on a page in reader
  mode is re-sent to the terminal, never dropped). Click into the pane = deliberate → browser
  mode; Esc returns to reader. Page JS cannot fake the click (no synthetic mouseDown arrives via
  `input-event`).
- **One instance** (`requestSingleInstanceLock`): the TUI owns a shared profile + wirecopy.db.
- **TUI exit ⇒ app exit**: the TUI is the app; the shell is a display surface.

## Gates

`gates/` — outcome-asserted, headful under Xvfb, OS-level keys via xdotool (CDP key dispatch
would bypass focus routing and fake results). Each gate builds what it needs and owns its
display/port. Run e.g. `node gates/gate-p1.mjs` from `shell/` (see each gate's header).
