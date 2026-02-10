# Terminal Browser UI Improvements Plan

## Overview
This plan addresses usability issues in the terminal browser mode, focusing on launch experience, navigation/rendering quality, and link hierarchy.

---

## Phase 1: Simple Launcher (No Arguments Required)

### Problem
Currently requires `--browse --browse-url <url>` flags. User wants to just run a command and be prompted.

### Steps

**1.1 Add a `browse` verb command**
- File: `src/NYTAudioScraper.API/CommandOptions.cs`
- Add a new verb class `BrowseOptions` that takes an optional positional URL argument
- Example: `dotnet run -- browse` or `dotnet run -- browse https://macleans.ca`

**1.2 Update Program.cs to handle the verb**
- File: `src/NYTAudioScraper.API/Program.cs`
- Add verb handling in the command parser
- If no URL provided, call `PromptForUrlAsync()` which already exists

**1.3 Verification**
- Run `dotnet run -- browse` with no URL
- Confirm it prompts "Enter URL:"
- Enter `https://macleans.ca` and verify page loads

---

## Phase 2: Fix Screen Flickering

### Problem
`Console.Clear()` on every render causes visible flicker.

### Steps

**2.1 Implement double-buffering or cursor repositioning**
- File: `src/NYTAudioScraper.Infrastructure/Browser/UI/TerminalPageRenderer.cs`
- Instead of `Console.Clear()`, use `Console.SetCursorPosition(0, 0)` and overwrite
- Only clear when content height changes significantly
- Use ANSI escape codes for efficient screen updates: `\x1b[H` (home) + `\x1b[J` (clear to end)

**2.2 Add selective re-rendering**
- Only re-render changed portions (selection indicator, scroll position)
- Track previous render state to minimize writes

**2.3 Verification**
- Run browser, navigate with j/k rapidly
- Screen should update smoothly without full-screen flash
- Compare before/after with screen recording if needed

---

## Phase 3: Fix Cursor Visibility Issues

### Problem
- Cursor not visible until pressing down twice
- Cursor disappears at bottom of list without scrolling

### Steps

**3.1 Ensure first item is selected and visible on load**
- File: `src/NYTAudioScraper.Domain/Entities/Browser/NavigationTree.cs`
- Verify `CurrentSelection` is set to first child in constructor (line 34) - already done
- File: `src/NYTAudioScraper.Infrastructure/Browser/UI/TerminalPageRenderer.cs`
- Ensure selected item highlighting is visible on first render

**3.2 Implement scroll-follow for selection**
- File: `src/NYTAudioScraper.Infrastructure/Browser/BrowserOrchestrator.cs`
- When `SelectNext()` moves selection past visible area, increment `ScrollOffset`
- When `SelectPrevious()` moves selection above visible area, decrement `ScrollOffset`
- Add helper method: `EnsureSelectionVisible(tree, context, maxVisible)`

**3.3 Fix the "two presses needed" issue**
- Check if initial render correctly shows the `→` indicator on first item
- May need to ensure `IsSelected = true` before first render

**3.4 Verification**
- Load page, first link should show `→` immediately
- Press `j` once, selection should move down one item
- Navigate to bottom of visible list, pressing `j` should scroll and keep selection visible
- Press `k` at top should not scroll past first item

---

## Phase 4: Fix Viewer Mode Issues

### Problem
- Starts at bottom of article instead of top
- Same scrolling/flickering issues as link view

### Steps

**4.1 Reset scroll offset when entering viewer mode**
- File: `src/NYTAudioScraper.Infrastructure/Browser/BrowserOrchestrator.cs`
- In `HandleCommandAsync` for `SwitchToReadable`, set `_navigationService.SetScrollOffset(0)`

**4.2 Reset scroll when navigating to new page**
- File: `src/NYTAudioScraper.Infrastructure/Browser/NavigationService.cs`
- In `NavigateTo()`, ensure `ScrollOffset = 0` for new pages

**4.3 Apply same flicker fixes from Phase 2**
- Use cursor repositioning instead of clear

**4.4 Verification**
- Load article page, press `v` to enter reader mode
- Should start at TOP of article (first paragraph visible)
- Press `j` to scroll down, no flicker
- Navigate to new link, reader view should start at top

---

## Phase 5: Default to Viewer Mode When Selecting Links

### Problem
User wants reader view by default when activating a link.

### Steps

**5.1 Change link activation behavior**
- File: `src/NYTAudioScraper.Infrastructure/Browser/BrowserOrchestrator.cs`
- In `HandleCommandAsync` for `ActivateLink`:
  - After navigating to new page, check if page has readable content
  - If yes, automatically switch to `ViewMode.Readable`
  - If no, stay in `ViewMode.Hierarchical`

**5.2 Add config option (optional)**
- Could add `Browser:DefaultToReaderView: true` in appsettings.json
- For now, just hardcode the behavior

**5.3 Verification**
- In link view, select an article link and press Enter
- Should load page AND automatically show reader view
- If page has no readable content, should show link view with message

---

## Phase 6: Improve Link Hierarchy Display

### Problem
- Navigation, content, and footer all shown flat
- Want: Navigation collapsed by default, main content expanded, footer collapsed

### Steps

**6.1 Update NavigationTreeBuilder to create proper groups**
- File: `src/NYTAudioScraper.Infrastructure/Browser/NavigationTreeBuilder.cs`
- Create parent nodes for each LinkType (Navigation, Content, External, Footer)
- Add links as children of their type node
- Set collapse state: Navigation=Collapsed, Content=Expanded, Footer=Collapsed

**6.2 Update LinkNode to support group headers**
- File: `src/NYTAudioScraper.Domain/Entities/Browser/LinkNode.cs`
- Add `IsGroupHeader` property
- Group headers show count and collapse indicator

**6.3 Update renderer for group headers**
- File: `src/NYTAudioScraper.Infrastructure/Browser/UI/TerminalPageRenderer.cs`
- Render group headers differently (bold, with expand/collapse indicator)
- Respect collapse state when rendering children

**6.4 Update selection logic to skip/handle group headers**
- Group headers should be selectable to expand/collapse
- Enter on group header should toggle collapse, not navigate

**6.5 Verification**
- Load macleans.ca
- Should see: "▶ Navigation (X links)" collapsed, "▼ Main Content (Y links)" expanded, "▶ Footer (Z links)" collapsed
- Press `l` on Navigation to expand, shows nav links indented
- Press `h` to collapse

---

## Phase 7: Suppress Build Warnings

### Problem
Long list of NU1608 package version warnings on every build.

### Steps

**7.1 Suppress NU1608 warnings in project files**
- Files: All `.csproj` files
- Add to PropertyGroup: `<NoWarn>$(NoWarn);NU1608</NoWarn>`

**7.2 Alternative: Pin package versions**
- Update Terminal.Gui or System.Text.Json to compatible versions
- May require testing for regressions

**7.3 Verification**
- Run `dotnet build --configuration Release`
- Should complete with 0 warnings (or only code analysis warnings)

---

## Verification Checklist

After all phases, run through this complete test:

1. [ ] Run `dotnet run -- browse` - should prompt for URL
2. [ ] Enter `https://macleans.ca` - should load without excessive warnings
3. [ ] First link "Carney Is the Crisis Manager..." should be selected with `→`
4. [ ] Press `j` once - selection moves down one, no flicker
5. [ ] Press `j` repeatedly to bottom - screen scrolls, selection stays visible
6. [ ] Press `k` back to top - works smoothly
7. [ ] Navigation section shows as collapsed `▶`
8. [ ] Main Content section shows as expanded `▼`
9. [ ] Press Enter on article - loads AND shows reader view
10. [ ] Reader view starts at TOP of article
11. [ ] Press `j` to scroll - smooth, no flicker
12. [ ] Press `v` to switch to link view - works
13. [ ] Press `b` to go back - returns to previous page
14. [ ] Press `q` to quit - exits cleanly

---

## File Summary

| File | Changes |
|------|---------|
| `CommandOptions.cs` | Add `BrowseOptions` verb |
| `Program.cs` | Handle browse verb, prompt for URL |
| `TerminalPageRenderer.cs` | Fix flicker (cursor reposition), group headers |
| `BrowserOrchestrator.cs` | Scroll-follow, default to reader, reset scroll |
| `NavigationService.cs` | Reset scroll on navigate |
| `NavigationTreeBuilder.cs` | Create hierarchical groups with collapse state |
| `NavigationTree.cs` | Support group nodes |
| `LinkNode.cs` | Add IsGroupHeader property |
| `*.csproj` | Suppress NU1608 warnings |
