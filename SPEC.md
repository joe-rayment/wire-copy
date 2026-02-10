# Project Specification

## Context

NYT Audio Scraper is a .NET 9 C# application with a terminal browser feature. The terminal browser allows users to navigate websites using vim-style keybindings, extract links, and read article content in a clean reader view.

The terminal browser is functionally complete (104+ unit tests passing), but has several UX issues that need addressing:
- Requires verbose CLI flags (`--browse --browse-url <url>`)
- Screen flickering on navigation due to `Console.Clear()` calls
- Cursor visibility issues (not visible on first load, disappears at bottom)
- Reader view starts at bottom instead of top
- Links displayed flat instead of hierarchical grouping
- Build warnings from package version conflicts

## Requirements

1. Simple launcher: Run `dotnet run -- browse` to start, optionally with URL argument
2. Fix screen flickering by using cursor repositioning instead of `Console.Clear()`
3. Fix cursor visibility - selection visible immediately on first load
4. Fix scroll-follow - selection stays visible when navigating past screen bounds
5. Reader view starts at top of article, not bottom
6. Default to reader view when activating article links
7. Group links hierarchically (Navigation collapsed, Content expanded, Footer collapsed)
8. Suppress NU1608 package version warnings

## Technical Constraints

```
Build:  dotnet build --configuration Release
Test:   dotnet test
Lint:   dotnet format --verify-no-changes
```

## Architecture Notes

- Clean Architecture: Domain → Application → Infrastructure → API
- Terminal browser code is in `src/TermReader.Infrastructure/Browser/`
- UI rendering is in `Browser/UI/TerminalPageRenderer.cs`
- Command handling is in `Browser/BrowserOrchestrator.cs`
- Link grouping is in `Browser/NavigationTreeBuilder.cs`
- CLI parsing uses CommandLineParser library with verb-based commands

Key files:
- `src/TermReader.API/CommandOptions.cs` - CLI options/verbs
- `src/TermReader.API/Program.cs` - Entry point
- `src/TermReader.Infrastructure/Browser/UI/TerminalPageRenderer.cs` - Rendering
- `src/TermReader.Infrastructure/Browser/BrowserOrchestrator.cs` - Main loop
- `src/TermReader.Infrastructure/Browser/NavigationTreeBuilder.cs` - Link grouping

## Completion Criteria

When ALL of these are true, the task is complete:

- [ ] All requirements implemented
- [ ] All tests pass (`dotnet test`)
- [ ] No lint errors (`dotnet format --verify-no-changes`)
- [ ] Code committed to git

## Current Task

See PLAN.md for the implementation plan and current task.

---

**INSTRUCTIONS FOR CLAUDE:**

This spec is the source of truth. Each iteration starts with fresh context.

1. Read PLAN.md to find the next unchecked task
2. Complete that ONE task
3. Mark it as complete in PLAN.md (change `- [ ]` to `- [x]`)
4. Run tests/lint to verify
5. Exit

If all tasks in PLAN.md are complete and all completion criteria above are
met, output "TASK COMPLETE" before exiting.
