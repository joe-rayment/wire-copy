## workspace-z5qz — AI Curated chooser visibility verification

Live tmux captures from `scripts/test_z5qz_ai_visibility.py` (in
`$CLAUDE_JOB_DIR`), 2026-05-22. The chooser is `Ctrl+L` →
`StrategyChooserHandler.HandleOpenChooserAsync`.

**Result:** no regression of workspace-33jw. AI Curated is visible in
every state; the user's "I don't see the AI option anymore" report is
not reproducible against this build. The likely cause is the single-line
status-bar rendering — the AI Curated row only appears when the user
cycles ▶ past Document Order, and the row's status-bar label is easily
swallowed by other badges at narrower terminal widths. workspace-ujxu
addresses this by replacing the status-bar with an anchored modal that
shows all rows at once.

### Captures (Wikipedia main page unless stated)

- `A_key_set_many_links.txt` — initial chooser frame, OpenAI key set,
  348 content links. Layout 1/3 = Document order.
- `A_key_set_ai_curated_selected.txt` — after one ▶: Layout 2/3 =
  AI Curated · will run on selection · **I:guidance** (workspace-z5qz
  hint suffix; workspace-99ve will wire the actual prompt).
- `B_key_wiped_initial.txt` — same page, OpenAI key wiped. Layout 1/3
  still Document order.
- `B_key_wiped_ai_curated_disabled.txt` — after one ▶: Layout 2/3 =
  **✗ AI Curated · No OpenAI API key (press c on the launcher to open
  Setup)**. Discoverable disabled row, workspace-33jw still in effect.
- `C_few_links_initial.txt` — `example.com` (1 content link), key set.
  Layout 1/3 = Document order · 1 links.
- `C_few_links_ai_curated_disabled.txt` — after one ▶: Layout 2/3 =
  **✗ AI Curated · Page has too few content links for AI curation**.
- `D_ai_selected_with_hint.txt` — Wikipedia, AI Curated row showing
  the `I:guidance` suffix.
- `D_after_shift_i_coming_soon.txt` — after pressing Shift+I on the
  AI Curated row: status bar shows
  `AI guidance — coming soon` (pointer at workspace-99ve).

### Code touched

- `src/WireCopy.Infrastructure/Browser/NavigationService.cs` — adds
  `GetCurrentPreviewCandidate()` so the preview-mode dispatch in the
  orchestrator can vary behaviour per-strategy.
- `src/WireCopy.Infrastructure/Browser/BrowserOrchestrator.cs` —
  intercepts `CommandType.InteractiveRefresh` (Shift+I) while in
  preview mode and routes to `StrategyChooserHandler.HandleGuidanceRequestAsync`.
- `src/WireCopy.Infrastructure/Browser/CommandHandlers/StrategyChooserHandler.cs` —
  - `AppendGuidanceHintIfAi` adds the `· I:guidance` suffix to the
    AI Curated row's summary so the affordance is discoverable.
  - `HandleGuidanceRequestAsync` emits the short "AI guidance — coming
    soon" status message when Shift+I fires on an AI Curated preview.
