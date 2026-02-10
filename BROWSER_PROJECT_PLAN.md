# Terminal Web Browser - Project Implementation Plan

## Overview

Transform NYT Audio Scraper into a dual-mode application:
1. **Audio Mode** (existing): Batch scrape articles and generate audio
2. **Browser Mode** (new): Interactive terminal-based web browser with vim-like navigation

## Vision

A terminal-based web browser that displays pages as hierarchical, keyboard-navigable link trees with a clean "reader view" for articles. Navigation uses Helix editor-style vim keybindings for muscle memory building.

## Architecture: Clean Separation (Plan A)

**Selected approach:** Maintain Clean Architecture with new browser domain entities, reuse existing scraping/parsing infrastructure, add Terminal.Gui for interactive UI.

**Why this approach:**
- Maximum code reuse (ScraperService for fetching, ArticleParser for parsing)
- Clean domain boundaries
- Both modes share DI container and configuration
- Single executable, two modes
- Low risk to existing functionality

## Technology Stack

### New Dependencies
- **Terminal.Gui v2** (alpha): Cross-platform TUI framework with TreeView, TextView, flexible keyboard bindings

### Reused Components
- **Selenium WebDriver**: Existing ScraperService for HTML fetching
- **HtmlAgilityPack**: Existing ArticleParser for content extraction
- **Serilog**: Shared logging infrastructure
- **Microsoft.Extensions.DependencyInjection**: Shared DI container

## Project Structure

```
src/
├── TermReader.Domain/
│   ├── Entities/
│   │   ├── Article.cs                    [EXISTING]
│   │   ├── AudioChapter.cs              [EXISTING]
│   │   ├── ScrapingSession.cs           [EXISTING]
│   │   └── Browser/                      [NEW]
│   │       ├── Page.cs                   Web page entity
│   │       ├── LinkNode.cs               Tree node with collapse state
│   │       ├── NavigationTree.cs         Full tree structure
│   │       └── ReadableContent.cs        Cleaned article content
│   └── ValueObjects/
│       ├── ArticleContent.cs            [EXISTING]
│       └── Browser/                      [NEW]
│           ├── LinkInfo.cs               URL, text, category, importance
│           ├── PageMetadata.cs           Title, description, canonical URL
│           └── NavigationContext.cs      Current page, history, position
│
├── TermReader.Application/
│   ├── Interfaces/
│   │   ├── IScraperService.cs           [EXISTING - REUSED]
│   │   ├── IArticleParser.cs            [EXISTING - REUSED]
│   │   └── Browser/                      [NEW]
│   │       ├── IBrowserService.cs        High-level orchestration
│   │       ├── IPageLoader.cs            Fetch & load pages
│   │       ├── ILinkExtractor.cs         Extract & classify links
│   │       ├── INavigationTreeBuilder.cs Build hierarchy
│   │       ├── IReadableContentExtractor.cs Clean article extraction
│   │       ├── IPageRenderer.cs          Render to Terminal.Gui
│   │       ├── INavigationService.cs     History management
│   │       └── IInputHandler.cs          Keyboard input
│   └── Services/
│       └── Browser/
│           └── BrowserOrchestrator.cs    Coordinates all services
│
├── TermReader.Infrastructure/
│   ├── Browser/
│   │   ├── ScraperService.cs            [EXISTING - REUSED]
│   │   ├── PageLoader.cs                [NEW] Wraps ScraperService
│   │   ├── LinkExtractor.cs             [NEW] Extract & categorize links
│   │   ├── NavigationTreeBuilder.cs     [NEW] Build tree structure
│   │   └── UI/                           [NEW] Terminal.Gui components
│   │       ├── TerminalPageRenderer.cs   Main renderer
│   │       ├── TerminalInputHandler.cs   Keyboard handling
│   │       ├── ViewRenderers/
│   │       │   ├── HierarchicalViewRenderer.cs  Link tree display
│   │       │   └── ReadableViewRenderer.cs      Article text display
│   │       └── Components/
│   │           ├── TreeNodeRenderer.cs   Collapsible tree nodes
│   │           ├── StatusBarRenderer.cs  Bottom status bar
│   │           ├── ScrollManager.cs      Viewport management
│   │           └── KeyBindingMap.cs      Vim-like mappings
│   └── Parsing/
│       ├── ArticleParser.cs             [EXISTING - REUSED]
│       └── ReadableContentExtractor.cs  [NEW] Wraps ArticleParser
│
└── TermReader.API/
    ├── Program.cs                       [MODIFIED] Add browser mode
    └── Commands/
        ├── AudioCommand.cs              [EXTRACT] Existing functionality
        └── BrowserCommand.cs            [NEW] Browser mode

tests/
└── TermReader.Tests/
    └── Browser/                         [NEW]
        ├── Unit/
        │   ├── LinkExtractorTests.cs
        │   ├── NavigationTreeBuilderTests.cs
        │   ├── ReadableContentExtractorTests.cs
        │   └── NavigationServiceTests.cs
        └── Integration/
            ├── BrowserOrchestratorTests.cs
            └── PageLoaderTests.cs
```

## Key Design Decisions

### 1. Link Categories (Determines Initial Collapse State)

```csharp
public enum LinkType
{
    Content,      // Article links - START EXPANDED
    Navigation,   // Nav menus - START COLLAPSED
    Footer,       // Footer links - START COLLAPSED
    External      // Off-site links - START COLLAPSED
}
```

**Classification Logic:**
- Check parent elements: `<nav>`, `<header>`, `<footer>`, `<aside>`
- Check parent classes: `.navigation`, `.menu`, `.sidebar`
- Check content area: `<article>`, `<main>`, `.content`
- Default: Short text (< 50 chars) = Navigation, longer = Content

### 2. Navigation Tree Structure

```
▼ Main Content (auto-expanded)
  → Breaking: Major News Event
  → Analysis: Economic Trends
  → Opinion: Political Commentary

▶ Navigation (collapsed, 15 links)

▶ Sidebar (collapsed, 8 links)

▶ Footer (collapsed, 12 links)
```

### 3. Keyboard Bindings (Helix/Vim-style)

| Key | Action | Description |
|-----|--------|-------------|
| `j` | Move down | Next link in tree |
| `k` | Move up | Previous link in tree |
| `h` | Collapse/Back | Collapse node or go back |
| `l` | Expand/Forward | Expand node or go forward |
| `Enter` | Select link | Navigate to selected URL |
| `Space` | Toggle collapse | Expand/collapse current section |
| `v` or `Tab` | Toggle view | Switch between Link/Reader view |
| `r` | Reader view | Force switch to reader view |
| `t` | Tree view | Force switch to tree view |
| `b` or `Backspace` | Back | Navigate history back |
| `Ctrl+d` | Page down | Scroll down half page |
| `Ctrl+u` | Page up | Scroll up half page |
| `gg` | Top | Jump to top of page |
| `G` | Bottom | Jump to bottom of page |
| `/` | Search | Search within page (future) |
| `q` | Quit | Exit browser |

### 4. View Modes

**Link View (Hierarchical):**
- Shows all links as collapsible tree
- Content links expanded, navigation collapsed
- Cursor highlights current selection
- Shows link count for collapsed sections

**Reader View (Article):**
- Clean article text (title, author, date, content)
- No ads, navigation, or clutter
- Word-wrapped at 80 characters
- Paragraphs separated by blank lines
- Only available if page is detected as article

### 5. Data Flow

```
User launches: dotnet run browse
    ↓
Prompt: "Enter URL: "
    ↓
User enters: https://www.nytimes.com/2024/01/22/article.html
    ↓
BrowserOrchestrator.LoadPageAsync(url)
    ├→ PageLoader.LoadAsync()
    │   └→ ScraperService.GetPageSourceAsync() [REUSES EXISTING]
    ├→ LinkExtractor.ExtractLinksAsync()
    │   └→ ClassifyLink() for each link (Content/Navigation/Footer)
    ├→ NavigationTreeBuilder.BuildTree()
    │   └→ Set initial collapse state based on category
    └→ ReadableContentExtractor.ExtractAsync()
        └→ ArticleParser.ParseArticle() [REUSES EXISTING]
    ↓
TerminalPageRenderer.RenderHierarchicalView()
    ├→ Display TreeView with links
    ├→ Highlight first content link
    └→ Show status bar with keybindings
    ↓
Main event loop:
    while (true) {
        var key = InputHandler.WaitForInput();
        switch (key) {
            case 'j': tree.SelectNext(); break;
            case 'k': tree.SelectPrevious(); break;
            case Enter: NavigateTo(selectedLink.Url); break;
            case 'v': ToggleViewMode(); break;
            case 'q': return;
        }
        Render();
    }
```

## Implementation Plan (6 Weeks)

### Week 1: Domain Layer & Interfaces
**Goal:** Establish browser domain model without touching existing code

**Tasks:**
1. Create `Domain/Entities/Browser/` folder
2. Implement `Page`, `LinkNode`, `NavigationTree`, `ReadableContent` entities
3. Implement value objects: `LinkInfo`, `PageMetadata`, `NavigationContext`
4. Implement enums: `LinkType`, `ViewMode`, `NodeCollapseState`
5. Define all application interfaces in `Application/Interfaces/Browser/`
6. Write unit tests for domain logic (tree manipulation, collapse/expand)

**Deliverables:**
- ✅ New domain entities with full test coverage
- ✅ All interfaces defined
- ✅ Zero impact on existing audio scraper
- ✅ All existing tests still pass

### Week 2: Infrastructure - Link Extraction & Page Loading
**Goal:** Implement services that build on existing infrastructure

**Tasks:**
1. Install Terminal.Gui: `dotnet add package Terminal.Gui --version "2.0.0-alpha.*"`
2. Implement `LinkExtractor.cs` (uses HtmlAgilityPack)
3. Implement `NavigationTreeBuilder.cs`
4. Implement `PageLoader.cs` (wraps existing ScraperService)
5. Implement `ReadableContentExtractor.cs` (wraps existing ArticleParser)
6. Write unit tests with HTML fixtures

**Deliverables:**
- ✅ Link extraction working with category classification
- ✅ Tree builder creates proper hierarchy
- ✅ Integration tests with real HTML samples
- ✅ Reuses ScraperService without modification

### Week 3: Terminal UI - Rendering
**Goal:** Build Terminal.Gui rendering components

**Tasks:**
1. Implement `TerminalPageRenderer.cs` (main coordinator)
2. Implement `HierarchicalViewRenderer.cs` using Terminal.Gui TreeView
3. Implement `ReadableViewRenderer.cs` using Terminal.Gui TextView
4. Implement `StatusBarRenderer.cs`
5. Implement `TreeNodeRenderer.cs` (custom tree node display)
6. Implement `ScrollManager.cs` (viewport calculation)
7. Test rendering with static HTML fixtures

**Deliverables:**
- ✅ Both views render correctly in terminal
- ✅ Tree nodes show collapse indicators (▼/▶)
- ✅ Status bar shows current keybindings
- ✅ Proper color scheme and formatting

### Week 4: Input Handling & Navigation
**Goal:** Connect keyboard input to navigation logic

**Tasks:**
1. Implement `TerminalInputHandler.cs`
2. Implement `KeyBindingMap.cs` with vim-like bindings
3. Implement `NavigationService.cs` (history stack)
4. Implement `BrowserOrchestrator.cs` (coordinates all services)
5. Wire up main event loop
6. Test full navigation flow

**Deliverables:**
- ✅ Keyboard navigation working (j/k movement)
- ✅ Link selection and navigation working
- ✅ Back/forward history working
- ✅ View mode switching working
- ✅ End-to-end browser functionality

### Week 5: CLI Integration
**Goal:** Add browser mode to existing CLI

**Tasks:**
1. Create `Commands/BrowserCommand.cs` (no URL parameter, interactive prompt)
2. Modify `Program.cs` to support two verbs: `audio` and `browse`
3. Create `DependencyInjection/BrowserServicesExtension.cs`
4. Ensure audio mode still works unchanged
5. Test both modes

**Deliverables:**
- ✅ `dotnet run browse` launches browser
- ✅ `dotnet run audio` still works (default)
- ✅ Both modes share configuration
- ✅ Clean separation in DI

### Week 6: Polish, Testing & Documentation
**Goal:** Production-ready browser mode

**Tasks:**
1. Test with various websites (NYT, Wikipedia, GitHub, news sites)
2. Refine link classification heuristics
3. Add loading indicators during page fetch
4. Add error handling for failed requests
5. Add help panel (`?` key shows all shortcuts)
6. Update README.md with browser quickstart
7. Write comprehensive tests (unit + integration)
8. Create demo GIF/video

**Deliverables:**
- ✅ Robust link classification
- ✅ Graceful error handling
- ✅ Complete documentation
- ✅ Test coverage > 80%
- ✅ Production-ready browser mode

## User Experience

### Startup (Interactive URL Prompt)

```bash
$ dotnet run browse

NYT Audio Scraper - Browser Mode
================================

Enter URL: https://www.nytimes.com/2024/01/22/technology/ai-article.html

Loading page...
```

### Link View Display

```
╔══════════════════════════════════════════════════════════════╗
║ AI Transforms Software Development - The New York Times      ║
║ https://www.nytimes.com/2024/01/22/technology/ai-article... ║
╚══════════════════════════════════════════════════════════════╝

▼ Main Content (4 articles)
  → [1] How AI Is Changing Coding Forever
    [2] Tech Giants Bet Big on Machine Learning
    [3] The Ethics of Automated Development
    [4] Related: Future of Programming Jobs

▶ Navigation (15 links)

▶ Sidebar (8 links)

▶ Footer (12 links)

────────────────────────────────────────────────────────────────
[LinkView] j/k:move h:collapse l:expand Enter:select v:reader q:quit
```

### Reader View Display

```
╔══════════════════════════════════════════════════════════════╗
║ How AI Is Changing Coding Forever                            ║
╚══════════════════════════════════════════════════════════════╝

By Jane Doe
Published Jan 22, 2024

Artificial intelligence is fundamentally transforming how software
developers write code. From autocomplete suggestions to entire
function generation, AI assistants are becoming indispensable tools
in modern development workflows.

The rise of large language models has enabled a new generation of
coding assistants that understand context, suggest improvements, and
even catch bugs before they reach production. GitHub Copilot,
OpenAI's Codex, and similar tools are now used by millions of
developers worldwide.

However, this transformation raises important questions about the
future of the profession...

────────────────────────────────────────────────────────────────
[ReaderView] j/k:scroll v:links b:back q:quit  [2 min read]
```

### Navigation History

```
Session History:
1. NYT Homepage → 2. Technology Section → 3. AI Article (current)

Press 'b' to go back to Technology Section
Press 'h' twice to return to Homepage
```

## CLI Commands

### Browser Mode (New)

```bash
# Launch browser with interactive URL prompt
dotnet run browse

# The application will prompt:
# Enter URL: _
```

### Audio Mode (Existing - Unchanged)

```bash
# Default mode (audio scraping)
dotnet run

# Explicit audio mode
dotnet run audio --count 5

# Show help
dotnet run --help
```

## Configuration

### appsettings.json (New Section)

```json
{
  "Browser": {
    "Headless": false,
    "DefaultUserAgent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36",
    "PageLoadTimeoutSeconds": 30,
    "Theme": {
      "SelectionBackground": "Blue",
      "SelectionForeground": "White",
      "HeaderColor": "Cyan",
      "StatusBarColor": "Yellow",
      "ContentLinkColor": "Green",
      "NavigationLinkColor": "Gray"
    }
  }
}
```

## Testing Strategy

### Unit Tests
- `NavigationTree`: Tree manipulation, selection, collapse/expand
- `LinkExtractor`: Category classification, URL normalization
- `NavigationTreeBuilder`: Hierarchy construction
- `ReadableContentExtractor`: Content extraction

### Integration Tests
- `BrowserOrchestrator`: Full page load flow
- `PageLoader`: Real HTML fetching (with fixtures)
- End-to-end navigation flow

### Manual Testing Checklist
- [ ] Navigate NYT articles
- [ ] Navigate Wikipedia pages
- [ ] Navigate GitHub repositories
- [ ] Handle broken links gracefully
- [ ] Handle timeout errors
- [ ] Handle non-article pages
- [ ] Test all keyboard shortcuts
- [ ] Test view mode switching
- [ ] Test back/forward navigation
- [ ] Test on different terminals (iTerm2, Terminal.app, Windows Terminal)

## Success Criteria

### Functional Requirements
- ✅ Browser launches with simple command: `dotnet run browse`
- ✅ URL entered interactively after launch (not as CLI arg)
- ✅ Hierarchical link view with proper categorization
- ✅ Main content links start expanded
- ✅ Navigation/footer links start collapsed
- ✅ Vim-like keyboard navigation (j/k/h/l)
- ✅ Link selection with Enter key
- ✅ View mode switching (v key)
- ✅ Back/forward navigation (b key)
- ✅ Readable article view (clean text)
- ✅ Graceful error handling

### Non-Functional Requirements
- ✅ Zero breaking changes to audio scraper
- ✅ Clean separation of concerns
- ✅ Test coverage > 80%
- ✅ Documentation complete
- ✅ Performance: Page load < 5 seconds
- ✅ Memory: < 200MB for typical browsing session

## Risks & Mitigation

### Risk 1: Terminal.Gui v2 Alpha Stability
**Mitigation:** Use stable v1 if v2 has issues; v2 is recommended for new projects

### Risk 2: Complex Link Classification
**Mitigation:** Start with simple heuristics, iterate based on testing

### Risk 3: Breaking Audio Scraper
**Mitigation:** All new code in separate namespaces, continuous testing of audio mode

### Risk 4: Poor Performance on Large Pages
**Mitigation:** Implement pagination, limit initial link display to 100

## Future Enhancements (Post-MVP)

### Phase 2 Features
- [ ] Search within page (`/` key)
- [ ] Bookmarks system
- [ ] History persistence (SQLite)
- [ ] Download page as markdown
- [ ] Multiple tab support
- [ ] URL history suggestions
- [ ] Custom CSS reader mode themes

### Phase 3 Features
- [ ] JavaScript execution support
- [ ] Image preview in terminal (Sixel/iTerm2)
- [ ] Form submission
- [ ] Cookie management UI
- [ ] Download manager
- [ ] RSS feed reader integration

## README.md Updates

Add new section after "Quick Start":

```markdown
## Browser Mode (Terminal-Based Web Browser)

Navigate the web with keyboard-only controls in your terminal.

### Quick Start

```bash
# Launch browser
dotnet run browse

# Enter URL when prompted
Enter URL: https://wikipedia.org
```

### Navigation

- `j/k`: Move up/down through links
- `Enter`: Follow selected link
- `v`: Toggle between link tree and article reader view
- `b`: Go back
- `Space`: Expand/collapse sections
- `q`: Quit

### Features

- Hierarchical link display (main content expanded, navigation collapsed)
- Clean article reader mode (no ads or clutter)
- Vim-like keyboard navigation
- History tracking (back/forward)
- Works with any website

### Example Session

```bash
$ dotnet run browse
Enter URL: https://news.ycombinator.com

# Navigate with j/k, press Enter on story
# Press 'v' to switch to reader view
# Press 'b' to go back
```
```

## Rollback Plan

If issues arise:
1. All browser code is in `Domain/Entities/Browser/` and `Application/Interfaces/Browser/`
2. Remove `BrowserCommand` from Program.cs
3. Remove `services.AddBrowserServices()` call
4. Audio scraper continues working unchanged
5. Can pause browser development without impact

## Approval Checklist

Before proceeding with implementation:
- [ ] Architecture approved (Clean Separation)
- [ ] Technology choice approved (Terminal.Gui)
- [ ] Startup flow approved (interactive URL prompt, not CLI arg)
- [ ] 6-week timeline acceptable
- [ ] Success criteria agreed upon
- [ ] README updates planned

## Next Steps After Approval

1. Create feature branch: `git checkout -b feat/terminal-browser`
2. Week 1: Domain entities and interfaces
3. Install Terminal.Gui and build proof-of-concept
4. Weekly demos of progress
5. Merge to main after Week 6 testing complete

---

**Project Lead:** Claude Code
**Created:** 2026-01-22
**Status:** Awaiting Approval
**Estimated Completion:** 6 weeks from approval
