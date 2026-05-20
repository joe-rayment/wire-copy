# workspace-hapr live verification results

**Date:** 2026-05-20
**Bead:** workspace-hrrf (live macleans.ca verification of AI Curated diagnostics)
**Outcome:** Partial — automated live verification was blocked by upstream
page-load issues. Documented for follow-up.

## What was attempted

`scripts/test_macleans_ai_curated.py` drives the app to a target URL via
`scripts/termtest.py`, opens the strategy chooser (Ctrl+L), navigates to AI
Curated, and captures both the rendered tree and the
`AiCuratedStrategy result` INFO log line.

Default target is `https://macleans.ca` (the bead's named site). A
`TARGET_URL` env var allows substituting a different site.

## Blockers observed

### 1. macleans.ca consistently fails to load

Three back-to-back attempts (initial + 2× Shift+R) all produced:

```
╭──────────────────────────────────────────────────────────╮
│ Something went wrong                                     │
│ Failed to load page: Page navigated mid-load (e.g. af…   │
│ https://macleans.ca                                      │
│ b:back  Shift+R:retry                                    │
╰──────────────────────────────────────────────────────────╯
```

This is a `BrowserOrchestrator` page-load timing issue — likely the same
class of bug as the early-NYT-redirect failures fixed in workspace-pxeb /
ofdh, but on macleans.ca's redirect chain. The bug is **independent** of
workspace-hapr's diagnostics + degenerate detection: until macleans.ca
itself loads, we can't observe how the analyzer ranks it.

### 2. Strategy chooser navigation under tmux is brittle

On a substitute load (cbc.ca via `TARGET_URL`), the page DID load
successfully (LinkView footer: "Layout 1/3 · Document order · 106 links")
but `Ctrl+L` did not reliably produce the chooser's "Analyzing page
structure…" or "strategies ready" status banner within 30s. The capture
continued to show the regular LinkView page.

The harness IS sending `C-l` via tmux send-keys (the standard ctrl-L
encoding), so this might be:

- A race between Ctrl+L delivery and the page's "Hierarchical view +
  loaded" precondition for `LayoutCommandHandler.HandleChooseLayout`.
- A tmux key-translation edge case under certain terminal profiles.

Either way, the chooser couldn't be reliably driven from this harness
in this session.

## What we CAN say with confidence

The workspace-hapr diagnostics + degenerate detection are **unit-tested
in isolation** with 3 load-bearing tests in
`tests/WireCopy.Tests/Browser/ScrapingStrategyTests.cs`:

- `AiCuratedStrategy_MatchesDocumentOrder_SummaryFlagsNoReordering` —
  pins the macleans-like failure shape.
- `AiCuratedStrategy_EmptyRanking_SummaryFlagsEmpty` —
  pins the all-excluded edge case.
- `AiCuratedStrategy_GenuineReorder_NoDegenerateFlag` —
  inverse pin for the happy path.

`BuildSummary()` and `DetectDegenerateRanking()` are deterministic
functions of `(AiCuratedResult, contentLinks)` — they produce the same
output for the same inputs regardless of whether they're called from a
unit test or from `AiCuratedStrategy.BuildTreeAsync` at runtime. So if
the AI's response on a live macleans.ca run happens to match the
surviving document order, the user WILL see "(no reordering — matches
document order)" in the strategy summary.

## Recommendation

File two follow-up beads:

1. **Fix the macleans.ca page-load failure.** Look at the redirect chain
   via existing `BrowserOrchestrator` instrumentation; compare against
   working-redirect sites (e.g. nytimes.com which was fixed in
   workspace-pxeb). This unblocks the workspace-hrrf acceptance.
2. **Make `scripts/test_macleans_ai_curated.py` reliable under tmux.**
   The script reliably gets to the loaded LinkView state on cbc.ca but
   the Ctrl+L step is flaky. Diagnose with verbose input-handler logs
   on a known-good run.

Once both are unblocked, re-run `scripts/test_macleans_ai_curated.py`
without `TARGET_URL` (default macleans.ca) and append findings here.

## Test artifacts

- `/tmp/qa-shots-hrrf/0_load_failed.txt` — last screen at the moment
  the load-detection loop gave up on macleans.ca (initial run).
- `/tmp/qa-shots-hrrf/1_doc_order.txt` — cbc.ca Document Order baseline
  (substitute target).
- `/tmp/qa-shots-hrrf/2_chooser_timeout.txt` — cbc.ca screen 30s after
  Ctrl+L was sent, showing the chooser status banner did NOT appear.
