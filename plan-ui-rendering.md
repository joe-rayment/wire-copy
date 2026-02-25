# Plan: UI/Rendering Fixes (Problems 1-4)

> **Revision 2** -- Updated based on critic review feedback.

## Problem 1: Console Warnings on First Load

### Root Cause Analysis

When launching via `dotnet run --project src/TermReader.API -- browse https://macleans.ca`, warnings clutter the console before the browser UI renders. There are multiple sources:

1. **Serilog console sink**: `appsettings.json` (line 10-14) configures a Console WriteTo sink at `Information` level. All `Log.Information(...)` calls from `Program.cs` during startup (lines 57-58: "Starting TermReader", "Options: ..."), `PageLoader` (lines 40, 47, 54, 123), `BrowserSession` (line 67: "Creating new WebDriver session"), `ReadableContentExtractor` (lines 69-73), etc. are written directly to `stdout` before the TUI takes over.

2. **Host builder default logging**: `Host.CreateDefaultBuilder()` in `CreateBrowseHostBuilder()` (line 162) registers its own console logger by default. Combined with `.UseSerilog()`, there may be duplicate log output from Microsoft.Extensions.Logging providers too.

3. **ChromeDriver/Selenium stdout noise**: Even though `BrowserSession` sets `SuppressInitialDiagnosticInformation = true` and `HideCommandPromptWindow = true` on the driver service (lines 170-172), and passes `--log-level=3` and `--silent` (lines 147-148), ChromeDriver may still emit initialization messages. This especially happens on first run when the driver binary is being resolved.

4. **HttpClient factory logging**: The `AddHttpClient("BrowserPageLoader")` registration in `BrowserDependencyInjection.cs` (line 26) triggers Microsoft.Extensions.Http logging. While `appsettings.json` sets `Microsoft` override to `Warning`, the default host builder may add its own console provider at lower levels.

### Proposed Fix

**Approach**: Suppress all console output until the TUI takes control, then redirect logs to file-only.

**A. Create a browse-specific Serilog configuration** that excludes the Console sink when in browse mode:

In `Program.cs` `RunBrowseVerbAsync()`, before creating the host builder, reconfigure Serilog to file-only:

```csharp
// In RunBrowseVerbAsync, before CreateBrowseHostBuilder:
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.File("logs/termreader-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .CreateLogger();
```

**B. Suppress default host console logging** by calling `.ConfigureLogging(logging => logging.ClearProviders())` in `CreateBrowseHostBuilder()`, so only Serilog (file-only) is used.

**C. ChromeDriver output**: The existing `--log-level=3`, `--silent`, and `SuppressInitialDiagnosticInformation = true` should be sufficient once Serilog's console sink is removed. No need for `service.LogPath` redirect unless residual output is observed during testing. Keep the change minimal.

**D. No Console.Clear() needed**: Since Problem 4 Step 1 enters the alternate screen buffer before the first render, the alt screen starts empty. `Console.Clear()` would be redundant. The startup sequence is: reconfigure Serilog -> build host -> enter alt screen buffer -> render first page.

### Files to Modify

- `/workspace/src/TermReader.API/Program.cs` (lines 115-160, 162-175): Reconfigure Serilog logger for browse verb, clear console providers

---

## Problem 2: Reader View Starts at Bottom of Article

### Root Cause Analysis

When following a link to an article, the reader view opens at the wrong scroll position. The flow is:

1. `BrowserOrchestrator.HandleCommandAsync()` `CommandType.ActivateLink` case (line 244-267):
   - Calls `NavigateToAsync()` which calls `_navigationService.NavigateTo(page)`
   - `NavigationService.NavigateTo()` (line 54-75) sets `_scrollOffset = 0` and `_currentViewMode = ViewMode.Hierarchical`
   - Then back in `HandleCommandAsync` (line 259-263), it checks `HasReadableContent()` and if true, calls `_navigationService.SetViewMode(ViewMode.Readable)` which ALSO sets `_scrollOffset = 0` (line 189-196)

So the scroll offset IS being reset to 0. The bug is in the **rendering logic**, not the state management.

2. In `TerminalPageRenderer.RenderArticleContent()` (line 381-416):
   - `startParagraph = context.ScrollOffset` (line 384) -- this should be 0 on first load
   - The loop renders from `startParagraph` to `startParagraph + maxDisplay` (line 387)
   - **The issue**: `maxDisplay` is calculated from `maxLines` which comes from `remainingHeight` (line 74): `Math.Max(3, options.TerminalHeight - _linesWritten - 3)`

   The problem is that `_linesWritten` is tracked across the render, but `RenderArticleContent` renders **paragraphs**, not **lines**. Each paragraph is word-wrapped into multiple display lines via `WrapText()` (line 389), plus a blank line separator (line 402). So `maxDisplay` paragraphs could produce far more display lines than `maxLines`.

   When paragraphs render into more lines than the terminal has, the terminal scrolls down automatically. The content starts at paragraph 0 but because the rendered output exceeds the viewport, the terminal auto-scrolls and the user sees the bottom.

3. **The fundamental mismatch**: `RenderArticleContent` uses `maxDisplay` as a paragraph count limit, but it should be a **line** count limit. The method renders `maxDisplay` paragraphs regardless of how many terminal lines they consume. If 5 paragraphs wrap to 40 lines but the terminal only has 15 lines of space, the excess pushes past the viewport.

### Proposed Fix

**Skip the intermediate paragraph-capped fix. Go directly to line-based scrolling (Problem 4 Step 4).**

The root cause (paragraph-vs-line mismatch) is fully resolved by the line-based approach in Problem 4 Step 4, which pre-wraps all content into display lines and renders a fixed viewport window. Writing an intermediate paragraph-capped version would be throwaway code since Step 4 replaces the entire method with a new signature (`List<string> allLines` instead of `ReadableContent content`).

The implementation path (Problem 4 Steps 1-4) reaches the line-based fix quickly: Step 1 (alt screen buffer) and Step 2 (MaxContentWidth) are trivial, Step 3 (resize) is moderate, and Step 4 is the actual rendering fix. There are no intermediate states to ship separately.

### Files to Modify

- See Problem 4 below (all changes are unified there)

---

## Problem 3: Reader View j/k Navigation Broken

### Root Cause Analysis

In reader view, `j` and `k` are mapped correctly in `TerminalInputHandler.MapKeyToCommand()` (lines 110-111) to `CommandType.MoveDown` / `CommandType.MoveUp`.

In `BrowserOrchestrator.HandleCommandAsync()`:
- `MoveDown` in readable mode (line 209): `_navigationService.SetScrollOffset(_navigationService.CurrentContext.ScrollOffset + 1)` -- increments scroll offset by 1
- `MoveUp` in readable mode (line 223): `_navigationService.SetScrollOffset(Math.Max(0, _navigationService.CurrentContext.ScrollOffset - 1))` -- decrements by 1

This increments/decrements `_scrollOffset` by 1. The scroll offset is used as `startParagraph` in `RenderArticleContent` (line 384). So pressing `j` should advance by one **paragraph**.

**The bug**: There are three interacting issues:

1. **Overflow rendering (Problem 2)**: Because `RenderArticleContent` doesn't cap output at the viewport height, ALL paragraphs from `startParagraph` to `startParagraph + maxDisplay` render, causing the terminal to auto-scroll. When the user presses `j`, scroll offset goes from 0 to 1, and the page re-renders. But since the rendering overflows again, the user sees the same bottom-of-overflow. It appears as if j/k does nothing because the visual difference is negligible compared to the overflow.

2. **Scroll unit mismatch**: Scrolling by 1 paragraph at a time means a long paragraph (50+ words wrapped to 5-6 lines) causes a large visual jump, while a short paragraph barely changes anything. Users expect line-based scrolling like Helix.

3. **No upper bound clamping on ScrollOffset**: `NavigationService.SetScrollOffset()` (line 165-167) only clamps to `Math.Max(0, offset)` with no upper bound. The user can scroll the offset far past the end of content, rendering an empty viewport. This makes j appear to "stop working" at the end of articles because subsequent presses still increment the offset but show nothing.

### Proposed Fix

All three issues are resolved by the unified line-based scrolling approach in Problem 4:

- **Issue 1** (overflow): Solved by rendering a fixed viewport window of exactly `viewportHeight` lines.
- **Issue 2** (scroll unit): Solved by converting ScrollOffset to a line index. `j` scrolls 1 line, `k` scrolls 1 line.
- **Issue 3** (no upper bound): Solved by clamping ScrollOffset in the orchestrator:

```csharp
// In BrowserOrchestrator, when handling MoveDown in readable mode:
var maxScrollOffset = Math.Max(0, totalWrappedLines - viewportHeight);
_navigationService.SetScrollOffset(
    Math.Min(_navigationService.CurrentContext.ScrollOffset + 1, maxScrollOffset));
```

The `SetScrollOffset` method in `NavigationService` should also accept an optional `maxOffset` parameter for defensive clamping:

```csharp
public void SetScrollOffset(int offset, int maxOffset = int.MaxValue)
{
    _scrollOffset = Math.Max(0, Math.Min(offset, maxOffset));
}
```

### Files to Modify

- See Problem 4 below (all changes are unified there)

---

## Problem 4: Reader View Fundamentally Broken (Not a Managed Viewport)

### Root Cause Analysis

The reader view has three sub-issues:

#### 4a. Scrolling past article start (viewport not isolated)

The renderer uses `Console.SetCursorPosition(0, 0)` in `Clear()` (line 149) and then writes content line by line with `Console.WriteLine()`. The `ClearRemainingLines()` method (lines 175-193) writes empty lines from `_linesWritten` to `Console.WindowHeight`.

The problem: if `RenderArticleContent` outputs more lines than the terminal height (Problem 2), those lines go below the visible viewport. When the user scrolls up (via the terminal emulator, not app scrolling), they see raw previously-rendered content from the terminal's scroll buffer. The app does not own the terminal scroll buffer -- it just writes to stdout.

**Root cause**: The renderer appends to the terminal buffer rather than managing a fixed viewport. Real TUIs use ANSI escape sequences or a library like `Spectre.Console`/`Terminal.Gui` to own the screen buffer.

#### 4b. Line width ignores terminal width

In `RenderArticleContent` (line 389), text is wrapped using `WrapText(paragraphs[i], options.MaxContentWidth - 4)`. The `MaxContentWidth` comes from `RenderOptions` (line 23) which defaults to `80`. In `BrowserOrchestrator.RunAsync()` (lines 134-138), `RenderOptions` is created with `TerminalWidth = Console.WindowWidth` and `TerminalHeight = Console.WindowHeight`, but `MaxContentWidth` is never updated from its default of 80.

So text always wraps at 76 characters (80 - 4 margin) regardless of actual terminal width.

**Root cause**: `MaxContentWidth` is not derived from `TerminalWidth`. It should be.

#### 4c. Not a managed viewport (terminal resize, screen ownership)

The current renderer:
- Uses `Console.SetCursorPosition(0, 0)` to "reset" to top (line 149)
- Writes lines with `Console.WriteLine()`
- Clears remaining lines with spaces

This approach has fundamental flaws:
- Does not use alternate screen buffer (`\x1b[?1049h`) which real TUIs use to isolate the app's display
- Does not respond to terminal resize events (`Console.WindowWidth`/`Console.WindowHeight` are read once at startup, line 134-138)
- `Console.SetCursorPosition(0, 0)` only works if the terminal hasn't scrolled; if overflow causes scrolling, position (0,0) refers to a line above the viewport
- No use of ANSI escape sequences for cursor movement, line clearing, or scroll region management

### Proposed Fix

This requires the most significant refactoring. The approach is to make the renderer behave like a proper full-screen TUI.

#### Step 1: Use Alternate Screen Buffer

Enter alternate screen buffer at startup, exit on quit. This isolates the app's display from the shell's scroll buffer.

```csharp
// In BrowserOrchestrator.RunAsync, at the very top of the try block:
Console.Write("\x1b[?1049h");  // Enter alternate screen buffer
Console.CursorVisible = false;
```

**Critical: Handle unclean exits.** If the app crashes or receives SIGINT, the terminal must be restored. Three layers of protection:

```csharp
// 1. Console.CancelKeyPress handler (catches Ctrl+C before it becomes SIGINT):
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true; // Prevent immediate termination
    Console.Write("\x1b[?1049l");
    Console.CursorVisible = true;
};

// 2. try/finally around the entire RunAsync main loop:
try
{
    Console.Write("\x1b[?1049h");
    Console.CursorVisible = false;
    // ... main loop ...
}
finally
{
    Console.Write("\x1b[?1049l");
    Console.CursorVisible = true;
}

// 3. AppDomain.CurrentDomain.ProcessExit for SIGTERM:
AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
{
    Console.Write("\x1b[?1049l");
    Console.CursorVisible = true;
};
```

The `finally` block is the primary safeguard. The `CancelKeyPress` handler prevents the raw SIGINT from bypassing the main loop (the existing `Ctrl+C -> CommandType.Quit` mapping in the input handler only works if `ReadKey` catches it; a raw SIGINT could bypass this). The `ProcessExit` handler covers `kill` / SIGTERM.

This single change fixes 4a (scroll isolation) because the alternate screen buffer is independent of the main terminal scroll buffer. Users cannot scroll past the app's viewport.

#### Step 2: Set MaxContentWidth from Terminal Width

In `BrowserOrchestrator`, derive `MaxContentWidth` from `TerminalWidth`:

```csharp
var width = Console.WindowWidth;
var options = new RenderOptions
{
    TerminalWidth = width,
    TerminalHeight = Console.WindowHeight,
    MaxContentWidth = Math.Max(40, Math.Min(width - 2, 120))
};
```

This fixes 4b. The cap at 120 ensures readability on very wide terminals. The floor of 40 prevents rendering breakage on very narrow terminal panes (e.g., split-pane editors). The header box-drawing characters (`╔`, `║`, `╚`) need at least ~10 chars width; 40 provides comfortable margin.

#### Step 3: Handle Terminal Resize

Update `RenderOptions` on each render cycle by reading current terminal dimensions:

```csharp
// In BrowserOrchestrator, helper method:
private RenderOptions GetCurrentRenderOptions()
{
    var width = Console.WindowWidth;
    var height = Console.WindowHeight;
    return new RenderOptions
    {
        TerminalWidth = width,
        TerminalHeight = height,
        MaxContentWidth = Math.Max(40, Math.Min(width - 2, 120))
    };
}
```

Call this in `RenderCurrentPageAsync()` instead of using the static `options` variable. Also invalidate the wrapped content cache when width changes (see Step 4).

**Resize between keypresses**: If the terminal is resized while the app waits for a keypress, the old render remains at the wrong dimensions until the next keypress triggers a re-render. For v1, this is an acceptable limitation -- the next keypress will re-read dimensions and render correctly. Adding a SIGWINCH handler or background polling thread to trigger immediate re-renders is a v2 enhancement. Document this as a known limitation.

#### Step 4: Convert to Line-Based Scrolling (fixes Problems 2 and 3 properly)

Refactor `RenderArticleContent` to work with pre-wrapped lines instead of paragraphs:

1. **Pre-wrap content**: When entering reader view (or when content changes), wrap all paragraphs into a flat list of display lines:

```csharp
// In TerminalPageRenderer or as a helper:
private static List<string> WrapAllContent(ReadableContent content, int maxWidth)
{
    var allLines = new List<string>();
    foreach (var paragraph in content.Paragraphs)
    {
        var wrapped = WrapText(paragraph, maxWidth - 4); // 4 for margin
        foreach (var line in wrapped)
        {
            allLines.Add($"  {line}");
        }
        allLines.Add(""); // blank line between paragraphs
    }
    return allLines;
}
```

2. **Render a viewport window**: Render only `viewportHeight` lines starting at `scrollOffset`:

```csharp
private void RenderArticleContent(List<string> allLines, NavigationContext context, int viewportHeight, RenderOptions options)
{
    var startLine = context.ScrollOffset;
    var endLine = Math.Min(startLine + viewportHeight, allLines.Count);

    for (var i = startLine; i < endLine; i++)
    {
        if (!string.IsNullOrEmpty(context.SearchQuery))
            WriteLineWithHighlight(allLines[i], context.SearchQuery);
        else
            WriteLine(allLines[i]);
    }

    // Fill remaining viewport with empty lines (content shorter than viewport)
    for (var i = endLine - startLine; i < viewportHeight; i++)
    {
        WriteLine("");
    }
}
```

3. **Update scroll commands**: In `BrowserOrchestrator`, j/k now scroll by 1 line instead of 1 paragraph. Clamp scroll offset to `[0, totalLines - viewportHeight]`:

```csharp
// MoveDown in readable mode:
var maxScroll = Math.Max(0, cachedLines.Count - viewportHeight);
_navigationService.SetScrollOffset(
    Math.Min(_navigationService.CurrentContext.ScrollOffset + 1, maxScroll));

// MoveUp in readable mode:
_navigationService.SetScrollOffset(
    Math.Max(0, _navigationService.CurrentContext.ScrollOffset - 1));

// PageDown (Ctrl+D): scroll by half viewport
var halfPage = viewportHeight / 2;
_navigationService.SetScrollOffset(
    Math.Min(_navigationService.CurrentContext.ScrollOffset + halfPage, maxScroll));

// GoToBottom (G):
_navigationService.SetScrollOffset(maxScroll);
```

4. **Cache wrapped lines in the orchestrator**: The orchestrator holds the cache, NOT the domain entity `Page` (domain entities should not have mutable rendering state):

```csharp
// In BrowserOrchestrator, private fields:
private List<string>? _cachedWrappedLines;
private int _cachedContentWidth;
private string? _cachedPageUrl;

private List<string> GetOrBuildWrappedLines(ReadableContent content, int contentWidth, string pageUrl)
{
    // Invalidate cache if page or width changed
    if (_cachedWrappedLines == null ||
        _cachedContentWidth != contentWidth ||
        _cachedPageUrl != pageUrl)
    {
        _cachedWrappedLines = WrapAllContent(content, contentWidth);
        _cachedContentWidth = contentWidth;
        _cachedPageUrl = pageUrl;
    }
    return _cachedWrappedLines;
}
```

Cache invalidation triggers:
- **Navigation to new page**: `_cachedPageUrl` changes, cache rebuilds
- **Terminal resize**: `contentWidth` changes (detected in `GetCurrentRenderOptions()`), cache rebuilds
- **Same page, same width**: Cache reused (common case during scrolling)

5. **Update search scrolling**: `ScrollToSearchMatch` in `BrowserOrchestrator` (lines 508-573) currently searches paragraphs by index and sets scroll offset to a paragraph index. After the refactor, search should find the line index of matching content within the cached wrapped lines:

```csharp
// Find lines matching the query in the cached wrapped lines
var matches = new List<int>();
for (var i = 0; i < cachedLines.Count; i++)
{
    if (cachedLines[i].Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
        matches.Add(i);
}
// Set scroll offset to the line index of the match
if (matches.Count > 0)
{
    var wrappedIndex = matchIndex % matches.Count;
    _navigationService.SetScrollOffset(matches[wrappedIndex]);
}
```

#### Step 5: ANSI-Based Line Clearing

Replace the space-padding approach in `WriteLine()` with ANSI clear-to-end-of-line:

```csharp
private void WriteLine(string text = "")
{
    try
    {
        // Guard against writing past the terminal height
        if (_linesWritten >= Console.WindowHeight)
            return;

        Console.SetCursorPosition(0, _linesWritten);
        Console.Write(text);
        Console.Write("\x1b[K"); // Clear from cursor to end of line
        _linesWritten++;
    }
    catch (ArgumentOutOfRangeException)
    {
        // SetCursorPosition can throw if _linesWritten >= WindowHeight
        // (e.g., terminal was resized smaller between renders)
        // Silently stop writing -- we've filled the viewport
    }
    catch
    {
        // Fallback for non-standard console environments
        Console.WriteLine(text);
        _linesWritten++;
    }
}
```

Note: `Console.SetCursorPosition` can throw `ArgumentOutOfRangeException` if `_linesWritten >= Console.WindowHeight`. The explicit guard (`if (_linesWritten >= Console.WindowHeight) return`) prevents this in the normal case. The catch handles race conditions where the terminal is resized between the check and the call.

### Note on Hierarchical View (Link Tree)

The link tree view (`RenderLinkTree`) is NOT affected by the same overflow issues as reader view. Each link node renders exactly one `WriteLine` call, and the `maxLines` parameter correctly limits the number of nodes rendered (line 267: `for (var i = startIndex; i < Math.Min(startIndex + maxDisplay, visibleNodes.Count); i++)`). The alternate screen buffer (Step 1) provides scroll isolation for the link tree view as well. No additional changes needed for the hierarchical view.

### Files to Modify

- `/workspace/src/TermReader.Infrastructure/Browser/UI/TerminalPageRenderer.cs`: Major refactoring
  - Rewrite `RenderArticleContent` for line-based scrolling with viewport capping (new signature taking `List<string>`)
  - Add static `WrapAllContent` helper method
  - Replace space-padding in `WriteLine()` with ANSI `\x1b[K` escape sequence
  - Add `_linesWritten >= Console.WindowHeight` guard in `WriteLine()`
  - Update `RenderReadable` to accept and pass pre-wrapped lines

- `/workspace/src/TermReader.Infrastructure/Browser/BrowserOrchestrator.cs`:
  - Add alternate screen buffer entry/exit in `RunAsync()` with try/finally
  - Add `Console.CancelKeyPress` handler for SIGINT cleanup
  - Add `AppDomain.CurrentDomain.ProcessExit` handler for SIGTERM cleanup
  - Add `GetCurrentRenderOptions()` helper, call it per-render instead of static options
  - Derive `MaxContentWidth` from terminal width with floor of 40 and cap of 120
  - Add wrapped line cache (`_cachedWrappedLines`, `_cachedContentWidth`, `_cachedPageUrl`)
  - Add `GetOrBuildWrappedLines()` method
  - Change scroll increment from paragraph-based to line-based in MoveDown/MoveUp handlers
  - Clamp scroll offset to `[0, totalLines - viewportHeight]` in all scroll commands
  - Update `ScrollToSearchMatch` to search within cached wrapped lines

- `/workspace/src/TermReader.Application/DTOs/Browser/RenderOptions.cs`:
  - Ensure `MaxContentWidth` is properly settable (currently has default 80; no code change needed since it uses `init`)

- `/workspace/src/TermReader.Infrastructure/Browser/NavigationService.cs`:
  - Update `SetScrollOffset` to accept optional `maxOffset` parameter for defensive clamping

---

## Implementation Order

**Do NOT implement Problems 2 and 3 as separate intermediate fixes.** They are symptoms of the same root cause (paragraph-based rendering without viewport capping). The line-based approach in Problem 4 Step 4 fixes all three problems at once. Writing an intermediate paragraph-capped version would be throwaway code.

Recommended implementation order (all part of one PR):

1. **Problem 1 (warnings)** -- Quick, independent fix. Reconfigure Serilog for browse mode.
2. **Problem 4 Step 1 (alternate screen buffer)** -- Immediate high-impact improvement. Fixes scroll isolation. Must include unclean exit handlers.
3. **Problem 4 Step 2 (MaxContentWidth)** -- Quick fix. Derive from terminal width with floor/cap.
4. **Problem 4 Step 5 (ANSI line clearing)** -- Do this before Step 4 since the `WriteLine` method is used everywhere. Better to upgrade the primitive first.
5. **Problem 4 Step 4 (line-based scrolling)** -- The core fix. Rewrites `RenderArticleContent`, adds wrapping cache, updates all scroll commands, fixes search. This resolves Problems 2 and 3.
6. **Problem 4 Step 3 (resize handling)** -- Move `RenderOptions` creation to per-render, invalidate cache on width change. Note: immediate re-render on resize is a v2 enhancement (known limitation for v1).

### Coordination Notes

- **Architecture plan (Problem 5.1)**: Proposes making `browse` the default verb and restructuring `Program.cs`. Our changes to `RunAsync()` (alt screen buffer, per-render options) are compatible. The architect should modify `Program.cs` startup/verb handling, while we modify `BrowserOrchestrator.RunAsync()` internals. No conflict as long as merge order is: architecture cleanup first (or simultaneously), then our UI fixes.

- **Collections plan**: Will add collection rendering to `TerminalPageRenderer.cs` and `BrowserOrchestrator.cs`. Merge order should be: UI fixes first, then collections builds on the improved renderer. The line-based rendering approach provides a cleaner foundation for collection views.

---

## Risk Assessment

- **Alternate screen buffer**: Not all terminals support `\x1b[?1049h`. However, all modern terminals (iTerm2, Terminal.app, Windows Terminal, GNOME Terminal, Alacritty, kitty) do. The existing code already uses ANSI features like `Console.ForegroundColor` and `Console.SetCursorPosition`. Add a fallback for terminals that don't support it (detect via `TERM` environment variable). The unclean exit handlers (CancelKeyPress, ProcessExit, try/finally) are critical -- without them, a crash leaves the user in a broken terminal state.

- **Line-based scroll refactor**: Changes the semantic meaning of `ScrollOffset` from paragraph index to line index. This affects search scrolling (`ScrollToSearchMatch`), GoToBottom, GoToTop, PageUp/PageDown in `BrowserOrchestrator`. All call sites need updating -- the plan explicitly covers each one.

- **Scroll offset clamping**: Essential. Without upper-bound clamping, j/k will appear broken at article end. The plan adds both per-call clamping in the orchestrator and a defensive `maxOffset` parameter on `SetScrollOffset`.

- **Performance**: Pre-wrapping all content could be slow for very long articles. Mitigation: cache in the orchestrator, keyed by page URL + content width. Invalidate only on navigation or resize. During scrolling (the hot path), the cache is always reused.

- **Narrow terminals**: The `Math.Max(40, ...)` floor on MaxContentWidth prevents crashes on very narrow panes. Below 40 columns, the header box-drawing and content margins may look compressed but will not break.

- **No third-party TUI library**: The plan intentionally avoids adding a dependency like `Terminal.Gui` or `Spectre.Console`. Using raw ANSI escape sequences keeps the solution lightweight and avoids a large refactor to adopt a new framework. If more complex UI features are needed later (e.g., split panes, mouse support), adopting `Terminal.Gui` could be considered.
