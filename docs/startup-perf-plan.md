# WireCopy startup performance — measurement and plan

**Status:** diagnosis complete, no code changed. Written 2026-07-25.

## TL;DR

Startup is ~2.5–3.5 s from `./run` to a painted launcher, then a further ~1.5 s of **frozen input**
the instant you press Enter on your first bookmark. Nothing in the app is pathologically slow; the
cost is spread across four serial phases, and the largest single item — a full Release build on
every launch — is pure waste on the common case where nothing changed.

The four items worth doing recover ~2.5–3 s of the ~4–5 s. Ranked, they are:
skip the build when nothing changed; attach Playwright in the background at boot; delete a
350 ms `Thread.Sleep` chain from the input loop; spawn the .NET child earlier.

## Boot timeline

Measured on the real Mac where the log allows it, in this Linux container otherwise. `./run` emits
no timestamps of its own, so the build phase is the one segment with **no macOS measurement** — the
figure below is the container's, and closing that hole is work item 0.

| # | Phase | Cost | Basis |
|---|---|---|---|
| 1 | `./run` prelude (uname, `command -v node`, `node -p`, two `mkdir`, `date`) | < 25 ms | MEASURED (container) |
| 2 | **`./dotnet build -c Release`, unconditional** (`run:55`) | **1.0–1.8 s** warm no-op<br>1.1–4.4 s when it recompiles<br>18.5 s on a cold page cache | MEASURED (container) — **not measured on macOS** |
| 3 | `exec electron` → .NET child's first log line | ~0.65 s mean (min ≤0.55 s, max ≤2.72 s) | LOG-DERIVED, 19 real Mac boots |
| 4 | .NET spawn → launcher painted | 0.89–1.09 s | LOG-DERIVED, real Mac boots |
| 4a |  └ of which `AppDbContext.InitializeDatabaseAsync` | 358–770 ms, median ~426 ms | LOG-DERIVED, 9 Mac boots |
| 4b |  └ of which host build + DI + DataProtection + cookie warmup, before the first log line | 190–300 ms | LOG-DERIVED |
| 4c |  └ of which synchronous page-cache deserialize | 15–152 ms (scales ~9–12 ms/MB) | LOG-DERIVED, 6 Mac boots |
| | **Subtotal — `./run` to a usable launcher** | **~2.5–3.5 s** | |
| 5 | First Enter on a bookmark: input loop frozen | 1510–1575 ms | LOG-DERIVED, 4 Mac boots |
| 5a |  └ of which Playwright CDP attach (`BrowserSession.cs:1078`) | 1.15 s cold / 0.47 s warm | LOG-DERIVED |
| 5b |  └ of which `DecryptRevealAnimation` — 7 × `Thread.Sleep(50)` on the loop | ~350 ms | derived from constants |

Corroborating measurements taken directly (container, arm64):

- Bare .NET process floor, before the host builder: **32–50 ms**. The runtime is not the problem.
- Host build + EF init + all hosted services, run headless-free via `run-recipe`: **0.71–0.81 s**.
- `./run` end to end to channel-connect: **2.15 s**. Launching Electron directly with **no build**:
  **0.60 s**. The delta is the build.

### Two things that look like startup cost and are not

**The 4.58 s "gap" in the 17:39 boot is you reading the launcher, not the app stalling.** The main
loop parks in `Task.WhenAny(pendingInput, Task.Delay(500))` (`BrowserOrchestrator.cs:541`) and the
next line it logs carries `input=true` (`BrowserOrchestrator.cs:681`) — that branch was woken by a
keystroke. The same gap across other boots is 4.6 s, 8.7 s, 14.6 s, 17.1 s, 23.2 s, and on nine
boots it never ends at all. The app's own work in that window has a log-derived ceiling of 87 ms.

**The 18.5 s build was a cold page cache, once.** A genuine source edit rebuilds in 1.1 s (change
private to an assembly) to ~4.4 s (change that forces the whole 5-project graph to recompile).
Do not plan against 18 s.

## Work items, ranked

### 0. Make `./run` emit a boot timeline — do this first (S)

The build phase has no macOS number, and every item below needs a before/after. Stamp
`date +%s.%N` in `./run` at entry, after the build, and before `exec electron`; log
`process.hrtime` in `shell/main.js` at module scope, at `whenReady`, at `term:ready`, and at
`spawnTui`; and log one line in `Program.cs` at `Main` entry. Four numbers, and the timeline stops
being guesswork.

**Verification:** run `./run` on the Mac twice and read the four phase deltas off the console.

### 1. Skip the build when nothing changed (M) — saves 1.0–1.8 s on nearly every launch

`run:55` runs a full Release build unconditionally. On a launch where you changed nothing, that is
1.0–1.8 s of pure waste, and on a cold page cache far more. Replace it with a source-mtime probe
against a stamp file in `bin/Release/net10.0/`, measured at **27–39 ms** for a full walk.

Design points that are load-bearing:

- Compare against a dedicated stamp, **not** `WireCopy.API.dll` — `PreserveNewest` content
  (`demo/site/**` at `WireCopy.API.csproj:53`, `assets/podcast-cover.png` at `:29`, `appsettings.json`)
  is copied without relinking the dll, so a dll comparison would rebuild forever.
- Directories must be probe candidates, not just files: deleting a `.cs` changes only the parent
  directory's mtime.
- `touch` the stamp only after a **successful** build.
- Keep `WIRECOPY_FORCE_BUILD=1` as the escape hatch for an SDK swap under `.dotnet/`.

The failure mode that matters is a false negative — running a stale binary while you believe your
edit is live — so verify it as a correctness change, not a speed one.

**Verification:** change a string that is visible in the TUI, `./run`, and **read the new string on
screen**. Then `git mv` a `.cs` file and confirm the build runs. Then two consecutive no-edit
launches: the second must not print `wirecopy: building the app (Release)...`.

### 2. Attach Playwright in the background at boot (M) — saves ~1.15 s of frozen input

On the shell path there is no browser launch at all: `LaunchBrowserAsync` delegates to
`AttachToShellAsync` (`BrowserSession.cs:891-898`), a pure CDP attach to the Electron window that is
already open. It costs 1.15 s cold and is awaited **on the TUI input loop** during the first
navigation — which is exactly why four Mac boots all show a ~1.5 s freeze on the first keypress.

Start that attach as a background task at boot and await the (already-completed) task at first use.
This is **not** the warmup `Program.cs:327-331` bans: that comment is about the *headed launch* path,
which is unreachable while a shell is attached, and the shell creates adopted pages with
`setVisible(false)` first. No window pops. Nothing headless is involved.

**Verification:** boot, wait for the launcher, press Enter on a bookmark, and assert the TUI accepts
a second keystroke immediately — plus `[loop-timing] branch held the loop` for that navigation drops
from ~1500 ms to under 400 ms.

### 3. Get `DecryptRevealAnimation` off the input loop (S) — saves ~350 ms per navigation

`DecryptRevealAnimation` runs 7 × `Thread.Sleep(50)` synchronously on the input loop
(`BrowserOrchestrator.cs:918`, `DecryptRevealAnimation.cs:14,127`). That is ~22 % of the measured
1575 ms freeze, and it repeats on **every** navigation, not just the first. Make it async, or make
it interruptible by a pending keystroke.

**Verification:** type a key during the reveal and watch it register immediately instead of being
swallowed; `[loop-timing]` for a warm navigation drops by ~350 ms.

### 4. Spawn the .NET child at `whenReady`, not at `term:ready` (M) — saves ~250–450 ms

`spawnTui()` has exactly one call site: `main.js:1168`, inside the `ipcMain.on('term:ready')`
handler. (Note: `term:ready`, not `term:resize` — the resize handler can never spawn.) So the .NET
process cannot start until Chromium boots a renderer, parses 488 KB of xterm.js, and resolves an
explicit `document.fonts.load` gate over 677 KB of TrueType (`term.html:80-90`). All of that is
serial in front of a ~0.9 s .NET boot that shares none of its dependencies.

Spawn from the channel's `listen` callback at the top of `whenReady` instead, at a **persisted**
grid size (store cols/rows next to the existing fontSize), and resize when the real dims arrive.
Requires buffering pty output until `termView` is ready to receive it.

**Verification:** with the item-0 timestamps in place, `spawnTui` fires within ~50 ms of `whenReady`
instead of after `term:ready`, and `./run` → first painted TUI frame drops by ≥200 ms on the Mac.

### 5. Overlap the EF database init with host start (M) — saves 150–250 ms

`Program.cs:321-325` awaits `InitializeDatabaseAsync()` after `host.StartAsync()`. It runs an EF
first-use model build plus `Database.MigrateAsync()` on **every** launch even when the schema is
current — 358–770 ms, the single largest item in the .NET boot. Start it as a task before
`StartAsync()` and await it only where the DB is genuinely needed.

Note the precondition is already satisfied: three hosted services
(`PodcastJobLifecycleService.cs:57`, `ScheduledRunLifecycleService.cs:32`) already query the DB
concurrently with the migration today, so the serial await is not protecting them.

**Verification:** `./run` → launcher painted drops by ≥150 ms on the Mac; a fresh-install boot
(delete the DB) still reaches a working launcher with no schema errors.

### 6. Fix the offline launch failure (S) — availability, not latency

`NuGetAuditMode=all` (`Directory.Build.props:17`) plus Release `TreatWarningsAsErrors` (`:26`) means
that when `api.nuget.org` is unreachable **and** a real restore is needed, NU1900 becomes three
build errors, `run:55`'s `|| exit 1` fires, and the app never launches. Reproduced: rc=1 after
6.69 s. Add `<WarningsNotAsErrors>$(WarningsNotAsErrors);NU1900</WarningsNotAsErrors>` to the Release
PropertyGroup. Use `WarningsNotAsErrors`, not `NoWarn` — NU1901–NU1904 (real advisories) stay fatal,
and you still see that the audit did not run.

Good news from the same investigation: the everyday no-op launch does **not** touch the network.

### 7. Perceived startup: stop showing a black rectangle (M)

The window appears immediately (`main.js:871`) but the shell paints nothing of its own, so the user
stares at solid black for the whole build + Electron + .NET chain. Painting app chrome into
`term.html` before the TUI's first frame converts several seconds of "did it launch?" into ~0.2 s.
Zero wall-clock saving; likely the largest *felt* improvement per hour spent.

### 8. Smaller, real, do-them-while-you're-there (S each)

- **Defer the four non-critical panes.** Six `WebContentsView`s are built in one boot tick
  (`main.js:881,884,897,910,921,935`); four are invisible for minutes yet load in contention with the
  one that gates the spawn. ~100–250 ms (inferred).
- **Ship the terminal font as WOFF2.** 677 KB of raw TTF behind the font gate → ~50 KB. ~30–80 ms.
- **Cache headers on the loopback asset server** (`main.js:1080-1118`), which currently does
  synchronous `fs.existsSync`/`statSync` per request on the main process and sets no cache headers.
- **Delete the stale 267 MB `linux-arm64` RID tree** left in `bin/Release/net10.0/` by
  `shell/build-desktop.sh:38`. Reclaims disk on a shared volume. I do **not** credit the ~0.65 s
  build saving one agent attributed to it — only 5 of 1296 lines in the file list mention that RID.

### Considered and rejected

- **Debug instead of Release** — 1.28 s vs 1.19 s on the no-op path. Inside noise.
- **Adding `-p:` flags to `run:55`** (`--no-restore`, `-p:NuGetAudit=false`) — buys ≤0.4 s and each
  change to the global property set forces a one-time full recompile.
- **Disabling analyzers** — saves 2.2 s but only on launches that recompile, and costs Release
  warning enforcement. Item 1 makes it moot.
- **ReadyToRun** — plausible 100–300 ms, but entirely inferred. Worth trying *after* item 0 makes it
  measurable; the packaged path (`build-desktop.sh:38`) already publishes with a RID, so it is a
  one-line change there.

## What you can do today

Skip the build and launch Electron directly — this is `./run`'s own last line minus the build:

```bash
cd /workspace && shell/deps/darwin-arm64/node_modules/.bin/electron shell
```

Measured at **0.60 s** to a connected app versus **2.15 s** through `./run` (container numbers).
Rebuild with `./dotnet build src/WireCopy.API/WireCopy.API.csproj -c Release` only when you have
actually changed .NET code. Item 1 makes this the default behaviour.

## Open questions

- **The build's cost on your Mac is unmeasured.** Every build number here is from the Linux
  container, whose `/workspace` is a `fakeowner` bind mount of the Mac volume — I/O-slower than APFS
  for some operations and page-cache-cold in ways your Mac may not be. Item 0 closes this.
- **When the launcher first becomes visible is not logged on either side of the channel.** It paints
  exactly once and the process goes quiescent, so 0.2–2 s of the "black rectangle" window is
  unattributed. This is the honest hole.
- **ReadyToRun's real saving is unknown** — publish both ways and diff.
- **The verification phase of this investigation did not run** (session limit). The fixes above are
  designed and cross-checked against the code, but no independent adversarial pass reviewed them.
  Treat each `expectedSaving` as a hypothesis to confirm with item 0's timestamps.
