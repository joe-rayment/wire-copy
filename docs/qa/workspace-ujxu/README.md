## workspace-ujxu — anchored chooser modal

Live tmux captures from `scripts/test_ujxu_modal.py` (in `$CLAUDE_JOB_DIR`),
2026-05-22, Wikipedia main page on a 160×50 tmux terminal.

The chooser opens via Ctrl+L and now drives three phases through one
anchored modal pinned to the bottom of the screen (link tree visible above):

### Phase 1 — Pre-flight

`01_preflight_all_selected.txt` — initial state, all three strategies
have `[x]` checkboxes. Footnote below the rows: "RSS probe can take up
to 5s on sites without an advertised feed." Hint line:
`Space:toggle  Enter:probe selected  Esc:cancel`.

`02_preflight_ai_unchecked.txt` — after Down + Space, AI Curated shows
`[ ]`. The user is about to skip AI in this run.

### Phase 2 → Phase 3

On fast / cached pages the probing phase completes in <1s and the
capture often catches the Preview phase directly. The probing UI is the
same anchored box with per-row spinners (⠋) flipping to ✓/✗ as each
probe settles.

### Phase 3 — Preview

`03_preview_phase_doc_order_active.txt` — initial Preview frame. The
▶ marker is on row 1 (Document Order). Footnote: "3 candidates — ◀/▶
to compare, Enter to save the highlighted one." Hint:
`◀/▶:cycle  Enter:save  Esc:cancel  Shift+I:guidance (AI)`.

`04_preview_ai_curated_active.txt` — after ▶, row 2 (AI Curated)
shows the ▶ marker; the linked tree above also shifts to the AI
candidate's preview (no AI cache yet, so the tree is the stub).

`05_preview_rss_active.txt` — after another ▶, row 3 (RSS Feed) shows
the ▶ marker. The RSS row is unavailable on Wikipedia ("No RSS/Atom
feed found"); its detail column carries the reason and the Preview tree
falls back to the current document order.

### Phase exit

`06_after_cancel_overlay_gone.txt` — Esc dismissed the modal. The link
tree is back to its pre-chooser state, no overlay visible.

### Code touched

- New: `src/WireCopy.Infrastructure/Browser/UI/Components/StrategyChooserOverlay.cs`.
- New orchestrator field `_activeOverlayPainter` + render hook in
  `BrowserOrchestrator.RenderCurrentPageAsync`.
- New `CommandContext.SetOverlayPainter` delegate (`Action<Action<RenderOptions>?>`).
- `StrategyChooserHandler.HandleOpenChooserAsync` refactored into three
  phases: pre-flight (RunPreflightAsync), probing (ProbeSelectedAsync),
  preview (existing EnterPreviewMode).
- `LayoutCommandHandler.HandleCancel` and
  `StrategyChooserHandler.HandleApplyAsync` both call
  `StrategyChooserHandler.ClearOverlay(ctx)` so the modal goes away
  when the user exits preview mode.
- `NavigationService.GetCurrentPreviewIndex()` exposes the active
  candidate index so the overlay painter can keep its ▶ marker in
  sync with the underlying preview cycle.
