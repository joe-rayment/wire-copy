# Link-list pane: 1 column when docked, and links vanishing below the fold

**Status:** diagnosed and reproduced; fix not yet applied. Written 2026-07-26.
**Companion:** the launcher half of this same defect is already fixed (see the bottom of this doc).

## What the user sees, reproduced

Running `shell/gates/gate-linktree-html.mjs` headful and looking at `gates/out/ltree-a-docked.png`
reproduces both symptoms exactly, with the browser sidecar docked:

- The grid is **one column**, not two.
- The pane shows **3 of 15 links** — its own footer says `3 of 15 shown` and `▼ 13 more below` — and
  the bottom of the pane is a dead band.

That is the "links in the bottom 50% disappear": the pane isn't hiding links it drew, it is only ever
*admitting* three of them into a pane with room for more.

**The gate passes 50/50 while this is on screen.** It asserts the wrong contract in its own header:
"the pane shows over the DOCKED terminal split as ONE wide column". This is the failure mode
`CLAUDE.md` describes — a check that can pass while the feature is visibly broken.

## Root causes

### 1. A responsive column count that the project already banned

`LinkTreeRenderer.ShellPaneColumns` (`LinkTreeRenderer.cs:179`) computes columns from the pane's
estimated pixel width against a `clamp(420px, 40vw, 700px)` minimum track. At the docked width
(~790px measured) that arithmetic yields **1**.

This is not a subtle regression. `ResponsiveGrid`'s own class remarks (`ResponsiveGrid.cs:5-14`)
already record the user rejecting exactly this, in these words:

> workspace-ehon briefly made the column count responsive to window width … but the user rejected
> both extremes it produced: 3–5 skinny columns on a wide desktop window, and **a collapse to 1
> column when the docked browser sidecar narrows the terminal**.

The HTML-pane epics (workspace-vingi, workspace-h2caq) reintroduced the responsive count for the
projected panes, and with it both rejected extremes — 3 columns on the launcher, 1 column docked on
the link tree.

### 2. A vertical budget that under-admits

`ComputeLayout` (`LinkTreeRenderer.cs:265`) derives the admitted window as
`min(availableHeight, PaneBudgetLines) / cellHeight`, where:

- `ShellPaneCellHeight` (`:236`) **quantises** the pane's 177px card stride (165px card + 12px
  separator) into whole 21px terminal lines. Rounding 177 → 8 lines wastes up to 20px per row, and
  the waste compounds — which is precisely what `workspace-55jdj` warns about: *"Cell-granularity
  approximations do NOT work … The loop has to count px."*
- `ShellPaneChromePx` (`:207`) is a **CSS-derived** estimate — a sum of the stylesheet's `clamp()`s.
  Today I measured the equivalent estimate on the launcher pane against the live DOM and it was
  **~55px light**. A guessed chrome model is not a budget.

With 1 column, both errors land on the same axis and the result is 3 admitted items where the pane
has room for more.

### 3. The link-tree grid clips; the launcher's scrolls

`#grid` is the only shrinkable child of `#column` and clips its own overflow (`linktree.css:159-162`).
So on the link tree an over-estimate *slices a card* — there is no scroll safety net, unlike the
launcher, whose stage scrolls. That asymmetry is why the link-tree budget was made pessimistic in the
first place, and why it now under-fills.

## Plan

Decision taken with the user: **the 2-column contract wins over the design's 420px minimum track.**
Docked cards will be ~365px wide and the headline type scales down with them via the existing
`clamp()`s.

### L1 — Columns: always 2 (S)

Make `LinkTreeRenderer.ShellPaneColumns` return `ResponsiveGrid.FixedColumns`, exactly as the
launcher's now does. `linktree.css:155` already lays out `repeat(var(--cols), minmax(0, 1fr))`, so
nothing in the stylesheet forces the 420px floor — the floor lives only in the C# count.

**Verify:** dock the sidecar, look at the pane — two columns of cards, and the same two columns
after undocking and re-docking.

### L2 — Vertical budget in measured px, not quantised lines (M) — the actual "disappearing links" fix

Replace the line-quantised stride and the CSS-derived chrome with a px budget calibrated against the
live DOM:

- Drop `ShellPaneCellHeight`'s rounding; admit `floor(gridPx / 177)` rows directly in px.
- Replace `ShellPaneChromePx`'s clamp-sum with **measured** constants, the way
  `LauncherRenderer.ShellPaneChromePx` now works. Measure with a throwaway probe that reads
  `#column.scrollHeight − #grid.height` off the real pane at several window sizes, docked and
  undocked, and record the numbers in the doc comment so the next person re-measures instead of
  re-deriving.

This supersedes `workspace-55jdj`, which already specifies the px chrome model in detail — but note
its model is *also* derived from CSS rather than measured. Measure first, then compare.

### L3 — Decide what happens when the estimate is wrong (S)

The budget will never be exact. Pick the failure direction deliberately:

- **Keep clipping** and stay a few px pessimistic — costs a small dead band (this is
  `workspace-po3mz`).
- **Let `#grid` scroll** like the launcher's stage — costs a scrollbar, but an over-estimate then
  hides nothing.

Recommend the second: a link the user cannot reach is worse than a scrollbar, and it removes the
whole class of bug.

### L4 — Fix the gate so it asserts the real contract (S) — do this with L1, not after

`gate-linktree-html.mjs` currently encodes the bug as the expected result. Change:

- The docked leg: assert **2 columns**, not one wide column.
- The `D…` width sweep: assert 2 columns at *every* width, including 700px and 2320px.
- Delete `D…c2` ("every card is at least the 420px minimum track wide") — that check now contradicts
  the contract.
- **Add the check that was missing**: assert *items shown == items the pane has room for*, and that
  the footer's `N of M shown` count matches the admitted window. Under-filling must fail the gate.
  Today nothing measures it, which is why `3 of 15` shipped green.

### L5 — One calibrated px model, shared (M)

The launcher and link-tree panes now each carry their own cell/line/chrome constants, derived
independently and drifting apart. Fold them into one module with one probe that re-measures both. Do
this after L1–L4 land, so the shared model is calibrated against two working panes rather than one
working and one broken.

## Already fixed: the launcher half

Same root cause, fixed and verified today:

- `LauncherRenderer.ShellPaneColumns` returned `Math.Clamp((terminalWidth - 4) / 36, 1, 5)` — three
  columns on a 15" MacBook. It now returns `ResponsiveGrid.FixedColumns`.
- `PaneRowsPerPage` was a hard-coded `3`, which left ~25% of the pane empty once the grid was two
  columns. It is now a **measured** px budget: chrome measured off the live pane at 480px (1280 wide)
  / 485px (1360) / 494px (1680), card stride 122px.
- `gate-launcher-html.mjs` asserted the old 1→2→3→4 flow; it now asserts exactly 2 columns at every
  width including 1680px.

Verified by driving the real pane headful under Xvfb and **looking at the screenshot**: 2 columns ×
4 rows = 8 tiles at 1680×1000, `showing 1-8 of 10`, nothing clipped, no dead band. 46/46 gate checks
and 4734 unit tests pass.
