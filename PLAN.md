# Implementation Plan

## Current Phase

Phase 1B: Reliability & Performance Fixes

## Completed (Phase 1A: Simple Launcher & Core UX)

- [x] Task 1: Add `BrowseOptions` verb class to `CommandOptions.cs` with optional positional URL argument
- [x] Task 2: Update `Program.cs` to handle the browse verb, prompt for URL if not provided
- [x] Task 3: Fix flickering in `TerminalPageRenderer.cs` - use `Console.SetCursorPosition(0, 0)` instead of `Console.Clear()`
- [x] Task 4: Fix initial cursor visibility - ensure first item is selected and `→` indicator shows on first render
- [x] Task 5: Implement scroll-follow in `BrowserOrchestrator.cs` - selection stays visible when navigating past bounds
- [x] Task 6: Reset scroll offset to 0 when entering reader view or navigating to new page
- [x] Task 7: Default to reader view when activating article links (if page has readable content)
- [x] Task 8: Create hierarchical link groups in `NavigationTreeBuilder.cs` - Navigation (collapsed), Content (expanded), Footer (collapsed)
- [x] Task 9: Add `IsGroupHeader` property to `LinkNode.cs` and update renderer for group headers
- [x] Task 10: Update selection logic to handle group headers (toggle collapse on Enter, not navigate)

## Phase 1B: Reliability & Performance Fixes

Priority: fix user-facing bugs and remove tech debt before adding features.

- [x] Task 11: Fix Selenium fallback performance in `PageLoader.cs` — `WaitForPageLoadAsync` (line 301) hangs ~2min on ad-heavy sites. Root cause: pending-network-requests check (line 335) burns full timeout on sites with endless ad/tracker requests. Fix: cap total wait at 3-5s, skip scroll-for-lazy-loading on article pages, add configurable timeout. Also add progress messages to the renderer ("Trying fast fetch...", "Falling back to browser...")
- [x] Task 12: Fix reader view starting at bottom of article — `TerminalPageRenderer.RenderArticleContent` (line 369) uses `options.ContentHeight - 6` for `maxDisplay`, but `RenderArticleHeader` consumes a variable number of lines (title wrapping + metadata + borders = 8-12+ lines) that aren't subtracted. Fix: track actual lines consumed by header and pass remaining height to content renderer
- [x] Task 13: Remove debug code from `PageLoader.cs` — hardcoded `/tmp/macleans_http_debug.html` and `/tmp/macleans_debug.html` file writes (lines 121-126, 173-180)
- [x] Task 14: Add WebDriver session reuse — currently `BrowserFetchAsync` (line 151) creates and disposes a ChromeDriver per page load (~2-5s startup overhead each time). Add a `BrowserSession` or driver pool that persists across navigations and disposes on app exit
- [x] Task 15: Simplify `IsJavaScriptRequired` heuristic (line 354) — current checks for "react"/"angular"/"vue" as substrings match content text, not just framework usage. Low anchor count check causes false positives on simple pages. Simplify to: Cloudflare challenges + explicit "enable JavaScript" messages only
- [x] Task 16: Suppress NU1608 warnings by adding `<NoWarn>$(NoWarn);NU1608</NoWarn>` to all .csproj files

## Phase 1C: Core Test Coverage

Add P0 tests to prevent regressions before further changes.

- [ ] Task 17: Add `PageLoader` tests — mock WebDriver to verify `WaitForPageLoadAsync` completes within expected time; verify driver disposal on error paths; test `IsJavaScriptRequired` with real-world HTML fixtures (Cloudflare challenge page, normal site mentioning "react" in content, sparse page with few links)
- [ ] Task 18: Add `TerminalPageRenderer` scroll tests — verify first paragraph is visible when `scrollOffset=0` with various content heights and header sizes; verify progress indicator accuracy
- [ ] Task 19: Add `BrowserOrchestrator` navigation flow tests — verify ActivateLink resets scroll to 0 and activates reader view for content pages; verify `AdjustScrollForSelection` boundary conditions at top/bottom of tree
- [x] Task 20: Add `NavigationService` state tests — verify scrollOffset/viewMode reset on `NavigateTo`, `GoBack`, `GoForward`; verify history stack consistency
- [x] Task 21: Add edge case tests — `LinkExtractor` with zero links, 1000+ links, malformed HTML, relative URLs; `ReadableContentExtractor` with empty article, very long article (100+ paragraphs), article with no `<p>` tags
- [ ] Task 22: Run full test suite and fix any regressions
- [ ] Task 23: Final verification — run through the complete test checklist in `browser-ui-improvements.md`

## Phase 2: UX Improvements (Helix-like experience)

- [x] Task 24: Add URL navigation command — `:` or `g` keybinding opens a command line to enter a URL (Helix-style command mode). Currently no way to navigate to a new URL without restarting
- [x] Task 25: Add page caching for back/forward — going back currently reloads the page from scratch. Cache previously loaded `Page` objects in memory for instant back/forward navigation
- [x] Task 26: Add in-page search — `/` keybinding to search within both link view and reader view. Highlight matches and jump between them with `n`/`N`
- [ ] Task 27: Improve `ReadableContentExtractor` robustness — handle paywalled content, cookie consent overlays, "continue reading" buttons. Extract `<article>` or `[role="main"]` more aggressively
- [ ] Task 28: Update README with `browse` command usage, keybindings reference, and examples

## Phase 3: Rebrand to General-Purpose Terminal Browser

Rename the project from "NYT Audio Scraper" to a general-purpose terminal browser. NYT remains a supported site but is no longer the sole focus.

- [ ] Task 29: Choose a new project name and update solution/project files — rename `NYTAudioScraper.sln`, all `.csproj` files, and directory names under `src/` and `tests/`
- [ ] Task 30a: Rename C# namespaces — update all `namespace NYTAudioScraper.*` declarations across `.cs` files
- [ ] Task 30b: Rename C# using statements — update all `using NYTAudioScraper.*` references across `.cs` files
- [ ] Task 30c: Verify build compiles and all project references resolve after namespace rename
- [ ] Task 31: Update `CLAUDE.md` — rewrite project overview, remove NYT-specific legal notices, frame as general-purpose browser with audio features. Keep NYT as one example use case
- [ ] Task 32: Update `Dockerfile`, `docker-compose.yml`, and any CI/CD references to use new project name
- [ ] Task 33: Update configuration — rename NYT-specific config keys (e.g., `NYT:Email`) to generic equivalents (e.g., `Auth:Email`), update `appsettings.json` and user secrets references. Add migration note for existing users
- [ ] Task 34: Run full test suite and fix any breakage from the rename

## Notes

Reference files for implementation details:
- `browser-ui-improvements.md` - Detailed implementation steps for each phase
- `browser-task.md` - Testing results and known issues

Key patterns to follow:
- Use existing `ITerminalRenderer` interface for rendering changes
- Navigation state is managed in `NavigationService.cs`
- Command handling follows Command pattern in `BrowserCommands` enum

Test commands:
```bash
# Test simple launcher
dotnet run --project src/NYTAudioScraper.API -- browse
dotnet run --project src/NYTAudioScraper.API -- browse https://example.com

# Test with sites
dotnet run --project src/NYTAudioScraper.API -- browse https://news.ycombinator.com
dotnet run --project src/NYTAudioScraper.API -- browse https://macleans.ca
```

---

**INSTRUCTIONS:**

- Complete ONE task per iteration
- Mark task complete when done (change `[ ]` to `[x]`)
- Keep tasks atomic - if a task is too big, break it down
- When all tasks are done, check SPEC.md completion criteria
- Prioritize reliability (Phase 1B) before features (Phase 2) before rename (Phase 3)
