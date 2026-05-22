## workspace-99ve — AI Curated user-guidance prompt

Live tmux capture from `scripts/test_99ve_guidance_prompt.py` (in
`$CLAUDE_JOB_DIR`), 2026-05-22, Wikipedia main page on a 160×50 tmux
terminal.

### Flow

1. `01_chooser_open.txt` — Ctrl+L opens the workspace-ujxu modal with
   all three strategies selected.
2. `02_ai_curated_selected.txt` — after Enter (probe all) + Right
   (cycle), AI Curated row is highlighted. The row summary still
   shows the workspace-z5qz `I:guidance` hint.
3. `03_guidance_form_field.txt` — pressing Enter to apply AI Curated
   surfaces the new FormField anchored above the status bar:

       Anything you'd like to tell the AI? (optional)
       e.g. 'exclude opinion pieces', 'put COVID first', 'group by section'
       ╭──────────────────────────────────────────────────────────────────────────────╮
       │                                                                              │
       ╰──────────────────────────────────────────────────────────────────────────────╯

4. `04_after_esc_cancelled.txt` — pressing Esc from the FormField
   returns "Cancelled — strategy not applied" in the status line.

### Behaviour matrix

- **Empty submit (Enter on blank field)** — runs the analyzer with no
  guidance; cache key carries `UserGuidance = null` so behaves like a
  pre-99ve run.
- **Non-empty submit** — passes guidance to `AnalyzeCuratedAsync`'s
  new `userGuidance` parameter. `OpenAiHierarchyAnalyzer` appends it
  to the user prompt as:

       USER GUIDANCE — please weight this when ranking and excluding:
       <user text>

  The returned `AiCuratedResult` is stamped with `UserGuidance` so the
  next chooser invocation can detect a guidance change and re-run.
- **Esc** — returns null from `FormField.PromptAsync`, aborts the
  apply.

### Cache invalidation

`AiCuratedStrategy.BuildTreeAsync` compares `NormalizeGuidance(context.UserGuidance)`
against `NormalizeGuidance(cached.UserGuidance)`. Same → cache hit,
no analyzer call. Different (or one is null and other isn't) → cache
miss, re-runs and stamps the new guidance. Trim + null-empty
normalisation ensures " foo " and "foo" match.

Covered by two unit tests in
`tests/WireCopy.Tests/Browser/ScrapingStrategyTests.cs`:

- `AiCuratedStrategy_GuidanceChange_InvalidatesCacheAndPassesGuidance`
- `AiCuratedStrategy_SameGuidance_HitsCache`

### Code touched

- `src/WireCopy.Domain/ValueObjects/Browser/AiCuratedResult.cs` —
  new `UserGuidance` field.
- `src/WireCopy.Application/Interfaces/Browser/IScrapingStrategy.cs` —
  new `ScrapingStrategyContext.UserGuidance` field.
- `src/WireCopy.Application/Interfaces/Browser/IHierarchyAnalyzer.cs` —
  new `userGuidance` param on `AnalyzeCuratedAsync`.
- `src/WireCopy.Infrastructure/Browser/OpenAiHierarchyAnalyzer.cs` —
  appends guidance to the user prompt.
- `src/WireCopy.Infrastructure/Browser/ScrapingStrategies/AiCuratedStrategy.cs` —
  threads guidance, normalises it, invalidates cache on mismatch,
  stamps it onto the result.
- `src/WireCopy.Infrastructure/Browser/CommandHandlers/StrategyChooserHandler.cs` —
  `PromptForGuidanceAsync` helper renders the FormField on apply.

### Out of scope

- "Surface active prompt in row summary" (mentioned in bead) — deferred.
  The row's `I:guidance` suffix from workspace-z5qz still works as the
  discoverability hint.
- Multi-turn refinement / pre-baked clarifying questions — explicitly
  out of scope per the bead.
