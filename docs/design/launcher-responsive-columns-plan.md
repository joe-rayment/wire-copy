# Launcher & list layout: responsive columns for the desktop shell

**Date:** 2026-07-15 · **Branch:** `feat/desktop-shell` · **Trigger:** user report — "the app
sizing has been off vs. how it looked in my terminal … the tiles are skinny and long now. I liked
the old proportions." · **Decision:** responsive fill (user-selected), story list included.

**Status (2026-07-17): IMPLEMENTED & VERIFIED.** Both grids derive their column count from
`ResponsiveGrid` (target 52, clamp 1..5). Verified end-to-end headful: `gate-columns.mjs` — launcher
renders 2/3/4 columns at 900/1360/2100px with the selection highlight stepping column 0→1→2, and the
reader story list renders ≥3 columns with cross-column nav; `gate-d4-highlight.mjs` 36/36 (pixel-level
bar integrity across 1/2/3 cols × dsf{1,2}); `gate-o-pane.mjs` 9/9. Screenshots read (launcher 3-up;
story list Dispatch 1/4/7 | 2/5/8 | 3/6/9 with the middle-column selection correct). Root-caused and
fixed a latent gate-infra bug (`buildTui` incremental Release build silently ran STALE code). Adversarial
code review: no correctness defects; one confirmed test-adequacy gap (render-level last-column width),
now closed with render-invoking unit tests for both grids.

## Problem (identified, evidence-backed)

The launcher and story-list grids hardcode a **maximum of 2 columns**:

- `LauncherRenderer.ComputeLayout` → `columns = width >= 40 ? 2 : 1` (`LauncherRenderer.cs:210`)
- `LinkTreeRenderer.ComputeLayout` → `columns = width >= 50 ? 2 : 1` (`LinkTreeRenderer.cs:163`)

That was tuned for an ~80–120-column terminal, where 2 columns land at ~55 chars each — the
readable proportion the user liked. The Electron window defaults to **85% of the work area**
(`main.js:113`), which on the user's display is ~140–166 columns. Two columns then stretch to
~70–80 chars each, so each tile becomes a long, thin horizontal ribbon: in `a-boot.png` the title
"BOOKMARK NUMBER 01 NEWS SITE" (~28 chars) floats on the left with ~45 chars of dead space before
its `[3]` badge. That is the "skinny and long" — thin height (2 text lines) stretched across a huge
width. Confirmed in the boot screenshot and by `gate-d4` already running at 1360px (~139 cols).

Compounding it: the wordmark header is capped at 93 cols and centered, and the URL bar at 70 cols
and centered (`LauncherRenderer.cs:797,903`), while the grid spans full width — so a centered header
floats above a full-width stretched grid.

**This is a TUI-layout problem, not a window-sizing problem.** The fix does NOT shrink the window
(the user wants the real estate *used*); it makes the TUI adapt to fill it well. No `main.js` change.

## Decision

**Responsive fill** (user-selected over cap-and-center): derive the column count from a target tile
width so tiles keep the readable ~50-char proportion at every window size, and the grid grows to
more columns to fill the width instead of stretching two.

- Default window (~160 cols) → **3 columns** at ~52 wide each — the exact old proportion.
- Ultra-wide (~210 cols) → 4 columns. Narrow (~90) → 2. Very narrow → 1.

Applies to **both** the launcher grid and the story list (they deliberately share card vocabulary).

## Recommended formula (starting point — tune against the real render)

```
w        = terminalWidth - 2          // inner width
TARGET   = 50                         // readable tile width (the "old proportion")
MAX_COLS = 5                          // honors "fill"; only bites past ~250 cols, avoids tiny tiles
columns  = clamp(round(w / TARGET), 1, MAX_COLS)
cellWidth = columns <= 1 ? w : (w - (columns - 1)) / columns   // subtract inter-column dividers
                                                               // last column takes the remainder
```

Check: 88→2, 128→3, 158→3, 208→4, 38→1. **The Definition of Done is how it looks, not the number** —
`TARGET` is a knob to turn while looking at the real launcher, per the Verification Doctrine.

## Work breakdown (see beads)

1. **Launcher responsive columns (render).** New formula in `LauncherLayout.ComputeLayout`; generalize
   `BuildGridRowLine` from the `if (Columns == 2)` special-case to an N-column loop (follow the
   proven `BuildCompactRowLine` pattern — dividers `│`/`┼` between every column, trailing empty
   cells fill, separator rule spans full width, last-column width = remainder). Unit tests for the
   column count at representative widths and the last-column remainder. Nav is already generic
   (`MoveInGrid` takes `columns`; handler passes live `cols`; digit-jump is row-major) — add a nav
   test at 3 columns to prove arrow/hjkl and 1–9 land on the right tile.

2. **Story list (LinkTreeRenderer) responsive columns.** Same formula in `LinkTreeRenderer.ComputeLayout`;
   generalize `LinkTreeGridMapper.MapToGrid` to N columns (group headers stay full-width rows; regular
   links group into runs of N *within* a section); generalize the `Columns == 2` row-builder to N;
   **audit and fix the selection-index ↔ grid mapping for N columns** (the risk lives here). Tests.

3. **Composition + real-window verification (epic DoD).** Confirm the centered wordmark + URL bar read
   as intentional focal elements over the now-full-width grid (adjust only if it looks misaligned).
   Then drive BOTH the real launcher and a real story list **headful at the default window size**,
   screenshot, and assert per the Verification Doctrine: 3 readable columns; tiles ~50 wide with no
   stretched ribbons/dead space; digit-jump and arrow nav select the correct tile across every column;
   no clipped or overlapping cells; still degrades to 2/1 as the window narrows. Re-run
   `gate-d4-highlight` (now 3 cols) and the launcher gates (`gate-d2`, `gate-p3`).

## Non-goals / notes

- Not shrinking the default window or changing the font — the grid adapts instead.
- The wordmark ASCII art is fixed at 87 chars, so the header box can't grow past ~93; a centered logo
  over a full-width grid is a normal, acceptable composition (verify, don't over-engineer).
- `docs/design/` previously held only a `.DS_Store`; this is the first real design file there.
