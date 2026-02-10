# Implementation Plan

## Current Phase

Phase 1: Simple Launcher & Core UX Fixes

## Tasks

Complete these in order, one per iteration:

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
- [ ] Task 11: Suppress NU1608 warnings by adding `<NoWarn>$(NoWarn);NU1608</NoWarn>` to all .csproj files
- [ ] Task 12: Run full test suite and fix any regressions
- [ ] Task 13: Final verification - run through the complete test checklist in browser-ui-improvements.md
- [ ] Task 14: Update README with `browse` command usage and examples

## Completed

- [x] Terminal browser implementation complete (104+ tests passing)
- [x] CLI integration with `--browse` and `--browse-url` options
- [x] Link extraction and categorization working
- [x] Reader view content extraction working
- [x] Vim-style keybindings implemented

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
```

---

**INSTRUCTIONS:**

- Complete ONE task per iteration
- Mark task complete when done (change `[ ]` to `[x]`)
- Keep tasks atomic - if a task is too big, break it down
- When all tasks are done, check SPEC.md completion criteria
