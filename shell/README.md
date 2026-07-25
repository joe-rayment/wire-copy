# WireCopy Desktop — single-window shell

One real headful Chromium window (Electron) hosting the **unmodified WireCopy TUI** in an owned,
fully styleable terminal pane (xterm.js; ghostty-web-ready seam), with live pages as secondary
`WebContentsView` panes. Never headless. Plan/evidence: `docs/single-window-shell-plan.md`,
epic `workspace-pdp8`.

## Run

```bash
./run                   # builds the TUI (Release), installs the shell on first run, launches
```

Container/CI (no setuid sandbox): `WIRECOPY_SHELL_NO_SANDBOX=1 ./run` — packaged and
normal desktop runs keep the Chromium sandbox ON.

Env knobs: `WIRECOPY_SHELL_CDP_PORT` (default 9223, loopback CDP for the .NET driver and gates),
`WIRECOPY_SHELL_TUI_DLL`, `WIRECOPY_SHELL_DOTNET`.

## Design contracts (do not regress)

- **Reader-primary**: boots full-window TUI; the page pane is revealed on demand (`|` semantics
  arrive with the control channel) at a fixed minority fraction — never dominant.
- **Focus doctrine, three layers** (empirically derived — see `docs/qa/electron-shell-spike/`):
  bounce, enforcement loop, and **key forwarding** (a keystroke that lands on a page in reader
  mode is re-sent to the terminal, never dropped). Click into the pane = deliberate → browser
  mode; Esc returns to reader. Page JS cannot fake the click (no synthetic mouseDown arrives via
  `input-event`).
- **One instance** (`requestSingleInstanceLock`): the TUI owns a shared profile + wirecopy.db.
- **TUI exit ⇒ app exit**: the TUI is the app; the shell is a display surface.

## Projected HTML panes (`launcher/`, `linktree/`)

In shell mode the launcher and the story list are also drawn as real `WebContentsView` panes
laid over the terminal region. They are **display-only projections**: the TUI keeps every
piece of logic and all keyboard handling, and each page just paints whatever the last pushed
state message contained (`window.__renderLauncher` / `window.__renderLinkTree`, fed by the
`launcherState` / `linkTreeState` shell-channel methods). No key handlers, no links, no
focusable elements. Open either with `?demo=1` to render its fixture standalone.

Designs are imported from claude.ai design project `153674f7`; colours live in
`launcher/tokens.css`:

| Pane | Design |
|------|--------|
| `launcher/launcher.html` | `Wire Copy Launcher.dc.html` + `Launcher Responsive.dc.html` |
| `linktree/linktree.html` | `Link Tree.dc.html` (workspace-h2caq — replaced the denser `Link Tree Responsive.dc.html`) |

**The px-mirror contract — the one thing to get right when restyling.** Each pane's responsive
`clamp()`s exist twice: once in CSS, once as px arithmetic in C# that converts terminal cells to
design px (`PaneCellPx` 11.2 / `PaneLinePx` 21.0 in `LinkTreeRenderer`). Change one without the
other and the pane silently disagrees with the TUI:

- **Columns** — `LauncherRenderer.ShellPaneColumns` / `LinkTreeRenderer.ShellPaneColumns` mirror
  the grid's `auto-fill` track. The pushed count *is* the rendered track count, and it is the
  same count arrow keys move through, so a mismatch sends the selection to a card the user isn't
  looking at. The link tree's track is `clamp(420px,40vw,700px)`: one wide column in the docked
  split, two on a laptop window, three only past ~2250px.
- **Rows** — `ShellPaneCardStridePx` / `ShellPaneChromePx` / `ShellPaneCellHeight` /
  `ShellPaneGridLines` feed `LinkTreeLayout.PaneBudgetLines`, the ceiling both admission loops
  honour. The pane spends 240–320px on its CRT masthead, rules and footer where the terminal
  spends five lines, and its cards are a fixed 165–242px, so a window budgeted in terminal lines
  alone admits a row the pane then slices in half at its clipping edge.

`gate-linktree-html.mjs` pins all of it against the running app — including that no card is
clipped (A11, D*d) and that the C# px estimate still tracks the real pane (A12).

## Gates

`gates/` — outcome-asserted, headful under Xvfb, OS-level keys via xdotool (CDP key dispatch
would bypass focus routing and fake results). Each gate builds what it needs and owns its
display/port. Run e.g. `node gates/gate-p1.mjs` from `shell/` (see each gate's header).

Host packages the harness shells out to: `Xvfb`, `openbox`, `xdotool`, and ImageMagick's
`import` (screenshots) — `apt install xvfb openbox xdotool imagemagick`.

## Packaging (workspace-mwer)

```bash
shell/build-desktop.sh          # full artifacts for THIS host (AppImage/tar.gz on linux, unsigned zip on mac)
shell/build-desktop.sh --dir    # fast unpacked dir target (what gate-p7-packaged verifies)
```

The script stages into a throwaway `/tmp` dir (the repo is a shared volume — platform
binaries never persist here), publishes the **self-contained** WireCopy.API for the host
RID (folder publish, untrimmed — the Playwright node driver is loose content), installs
runtime deps, and runs electron-builder. The package:

- bundles the API under `resources/api` (no .NET on the machine), spawned as its own apphost;
- needs **no Playwright browser download** — shell mode attaches to the app's own Chromium;
- keeps the **Chromium sandbox ON** (a bare launch in a sandbox-less container aborts; the
  `--no-sandbox` argv is strictly container/CI launch input, never baked);
- writes logs + TUI state under the OS userData dir instead of the repo.

macOS artifacts (zip, arm64/x64, unsigned per the v1 install plan) build with the same
script **on a mac host** — cross-building from linux can't produce the native node-pty
or app bundle. The `./run` dev flow is untouched.
