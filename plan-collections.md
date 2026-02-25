# Plan: Collections / Read-Later Feature & ElevenReader Integration

## Problem 8: Read-Later List with Collections

### Overview

Add a persistent collections system that lets users save links while browsing, manage named collections, and navigate saved content using the same Helix-style keybindings as the rest of the app. Collections should feel like "just another page" in the navigation flow.

### Incremental Delivery Strategy

This feature is designed for **incremental delivery in two phases**, not all-or-nothing:

**Phase 1 (MVP):** Single "Read Later" list. `s` saves, `:readlater` views it. Stored in SQLite. Rendered as a simple link list with j/k/Enter/d. This covers the primary use case ("single keypress save") and can ship independently.

**Phase 2 (Full):** Named collections, `S` for collection picker, `:collections` view, reordering, export, default-collection tracking. Builds on Phase 1's persistence and UI.

The plan below describes the full feature. Phase 1 is a strict subset: implement steps 1-3, 4-8 with only `SaveToCollection` and `OpenCollections` commands, and a single hardcoded "Read Later" collection.

### Cross-Plan Coordination

**Build order matters.** This plan modifies `BrowserOrchestrator.cs`, `TerminalPageRenderer.cs`, `TerminalInputHandler.cs`, `NavigationService.cs`, and `BrowserDependencyInjection.cs`. These are also modified by the UI renderer plan (Problems 1-4) and the architecture plan (Problems 5-7).

**Required merge order:**
1. Architecture cleanup (Problem 7) -- removes old audio/podcast entities, may restructure persistence layer
2. UI rendering fixes (Problems 1-4) -- alternate screen buffer, ANSI clearing, fixed viewport
3. **Collections feature (Problem 8)** -- builds on top of the improved renderer and cleaned-up persistence

**Persistence coordination:** The architecture plan may remove `Article`, `AudioChapter`, `ScrapingSession` entities and their migrations. This plan adds new `Collection`/`CollectionItem` entities. The cleanest approach:
1. Architect removes old entities and deletes old migrations
2. Collections plan adds new entities
3. Create a single fresh `InitialCreate` migration with only collections tables
4. Since the app is being refactored from scraper to browser, blowing away the old schema is acceptable -- no existing user data needs preservation

---

### Data Model

#### Domain Entities (`TermReader.Domain/Entities/Collections/`)

```csharp
// Collection.cs
public class Collection
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public int SortOrder { get; private set; }      // For ordering collections in the list
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public List<CollectionItem> Items { get; private set; } = new();

    public static Collection Create(string name, int sortOrder = 0);
    public void Rename(string newName);
    public CollectionItem AddItem(string url, string title);
    public void RemoveItem(Guid itemId);
    public void MoveItemUp(Guid itemId);
    public void MoveItemDown(Guid itemId);
    public void Clear();
}

// CollectionItem.cs
public class CollectionItem
{
    public Guid Id { get; private set; }
    public Guid CollectionId { get; private set; }
    public string Url { get; private set; }          // The saved link URL
    public string Title { get; private set; }        // Display text from the link
    public DateTime SavedAt { get; private set; }
    public bool IsRead { get; private set; }         // "Read from collection" -- see note below

    public void MarkAsRead();
}
```

**Design decisions:**

- `Collection` is the aggregate root. Items are always accessed through their collection.
- **No `SourceUrl` field.** The critic correctly noted the original plan included it but never displayed it. Removed to avoid dead metadata. Can be added later if a use case emerges.
- **No `Position` field.** Default ordering is by `SavedAt DESC` (newest first). This avoids the N-update problem: saving an item to a 100-item collection previously required updating all 100 positions. Now, save is always O(1). Position-based reordering (MoveUp/MoveDown) uses swap logic on `SavedAt` timestamps of adjacent items -- simple and avoids bulk updates. If we later need true arbitrary reorder, we can add a gap-based position scheme (positions 1000, 2000, 3000; only recompute on collision).
- **`IsRead` semantics:** This means "opened from the collection view," NOT "ever read by the user." A user who reads an article, goes back, then saves it will have `IsRead = false`. This is correct and intentional -- the collection is a "to process" queue, and `IsRead` tracks whether the user has processed it from the collection. Clarified to prevent confusion.
- **`Name` uniqueness:** Case-insensitive. The repository normalizes names on lookup (`GetByNameAsync` uses case-insensitive comparison). The EF Core configuration uses a case-insensitive collation on the unique index (SQLite default is case-sensitive for `TEXT`, so we use `COLLATE NOCASE`). This means "Tech" and "tech" resolve to the same collection.

#### Why not reuse `Article` or `LinkInfo`?

`Article` is for scraped NYT content with full text bodies (and is likely to be removed in the architecture cleanup). `LinkInfo` is an in-memory value object for extracted links. Neither maps well to "a URL the user wants to read later." A dedicated `CollectionItem` is simpler and decoupled from browser state.

---

### Persistence (`TermReader.Infrastructure/Persistence/`)

Extend the EF Core `AppDbContext` with two new `DbSet`s:

```csharp
// In AppDbContext.cs - add:
public DbSet<Collection> Collections => Set<Collection>();
public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();
```

#### EF Core Configurations

```csharp
// CollectionConfiguration.cs
builder.ToTable("Collections");
builder.HasKey(c => c.Id);
builder.Property(c => c.Name).IsRequired().HasMaxLength(200);
builder.Property(c => c.SortOrder).IsRequired();
builder.HasMany(c => c.Items)
    .WithOne()
    .HasForeignKey(i => i.CollectionId)
    .OnDelete(DeleteBehavior.Cascade);
builder.HasIndex(c => c.Name).IsUnique();
// SQLite case-insensitive: builder.Property(c => c.Name).UseCollation("NOCASE");

// CollectionItemConfiguration.cs
builder.ToTable("CollectionItems");
builder.HasKey(i => i.Id);
builder.Property(i => i.Url).IsRequired().HasMaxLength(2000);
builder.Property(i => i.Title).IsRequired().HasMaxLength(500);
builder.HasIndex(i => new { i.CollectionId, i.SavedAt });
```

#### Repository (`ICollectionRepository`)

```csharp
public interface ICollectionRepository
{
    Task<List<Collection>> GetAllAsync(CancellationToken ct = default);
    Task<Collection?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Collection?> GetByNameAsync(string name, CancellationToken ct = default);  // case-insensitive
    Task<Collection> GetOrCreateDefaultAsync(CancellationToken ct = default);
    Task AddAsync(Collection collection, CancellationToken ct = default);
    Task UpdateAsync(Collection collection, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<Guid?> GetLastUsedCollectionIdAsync(CancellationToken ct = default);
    Task SetLastUsedCollectionIdAsync(Guid id, CancellationToken ct = default);
}
```

**"Last used" tracking:** Store as a simple key-value pair in a `Settings` table (single row: `Key = "LastUsedCollectionId"`, `Value = <guid>`). The last collection a user saved to becomes the default for next save. This avoids prompting on every save.

#### Migration

Coordinate with architecture plan (see Cross-Plan Coordination above). Create a fresh migration after old entities are removed. The existing `AppDbContext.InitializeDatabaseAsync()` already calls `Database.MigrateAsync()`, so new tables will be created automatically on first run.

---

### Application Layer (`TermReader.Application/`)

#### Service Interface

```csharp
// ICollectionService.cs
public interface ICollectionService
{
    // Quick save (single keypress path)
    Task<CollectionItem> SaveToDefaultCollectionAsync(string url, string title, CancellationToken ct = default);

    // Save to specific collection (by name -- creates if it doesn't exist)
    Task<CollectionItem> SaveToCollectionByNameAsync(string collectionName, string url, string title, CancellationToken ct = default);

    // Collection CRUD
    Task<List<Collection>> GetAllCollectionsAsync(CancellationToken ct = default);
    Task<Collection> CreateCollectionAsync(string name, CancellationToken ct = default);
    Task RenameCollectionAsync(Guid id, string newName, CancellationToken ct = default);
    Task DeleteCollectionAsync(Guid id, CancellationToken ct = default);
    Task ClearCollectionAsync(Guid id, CancellationToken ct = default);

    // Item management
    Task RemoveItemAsync(Guid collectionId, Guid itemId, CancellationToken ct = default);
    Task MoveItemUpAsync(Guid collectionId, Guid itemId, CancellationToken ct = default);
    Task MoveItemDownAsync(Guid collectionId, Guid itemId, CancellationToken ct = default);
    Task MarkItemAsReadAsync(Guid collectionId, Guid itemId, CancellationToken ct = default);

    // Default collection tracking
    Task SetDefaultCollectionAsync(Guid collectionId, CancellationToken ct = default);
    Task<Collection> GetDefaultCollectionAsync(CancellationToken ct = default);
}
```

---

### Keybindings & Input Handling

#### New Command Types (add to `CommandType` enum)

```csharp
// Add to CommandType enum:
SaveToCollection,       // 's' - save highlighted link to default collection
SaveToSpecific,         // 'S' - prompt for collection name, then save
OpenCollections,        // triggered by ':collections' or ':readlater' command
DeleteItem,             // 'd' - remove item/collection (context-dependent)
ReorderUp,              // 'K' (Shift+k) - move item up in collection
ReorderDown,            // 'J' (Shift+j) - move item down in collection
```

**Note:** `ReorderUp`/`ReorderDown` are separate command types from `MoveUp`/`MoveDown`. The input handler emits them based on the Shift modifier. The **orchestrator** decides what to do based on current ViewMode -- in CollectionItems view, it reorders; in other views, these commands could be ignored or treated as regular movement. This keeps the input handler stateless.

#### Key Mappings (in `TerminalInputHandler`)

**Implementation detail:** `s`, `S`, and `d` must be handled in the **character-based section** of `WaitForInputAsync` (using `keyInfo.KeyChar`), NOT in `MapKeyToCommand`. This matches how `n`/`N`/`:`/`/` are already handled. `J`/`K` (Shift) must be added to the Shift modifier block in `MapKeyToCommand` (lines 86-93), alongside the existing `G -> GoToBottom` mapping.

```csharp
// In WaitForInputAsync, character-based section (before MapKeyToCommand call):
if (keyInfo.KeyChar == 's')
    return Task.FromResult(new NavigationCommand { Type = CommandType.SaveToCollection });
if (keyInfo.KeyChar == 'S')
    return Task.FromResult(new NavigationCommand { Type = CommandType.SaveToSpecific });
if (keyInfo.KeyChar == 'd')
    return Task.FromResult(new NavigationCommand { Type = CommandType.DeleteItem });

// In MapKeyToCommand, Shift modifier block:
ConsoleKey.J => new NavigationCommand { Type = CommandType.ReorderDown },
ConsoleKey.K => new NavigationCommand { Type = CommandType.ReorderUp },
ConsoleKey.G => new NavigationCommand { Type = CommandType.GoToBottom },
```

| Key | Where Handled | Command Type |
|-----|---------------|-------------|
| `s` | `WaitForInputAsync` (keyChar) | `SaveToCollection` |
| `S` | `WaitForInputAsync` (keyChar) | `SaveToSpecific` |
| `d` | `WaitForInputAsync` (keyChar) | `DeleteItem` |
| `J` (Shift+j) | `MapKeyToCommand` (Shift block) | `ReorderDown` |
| `K` (Shift+k) | `MapKeyToCommand` (Shift block) | `ReorderUp` |

**Helix convention note:** In Helix, `s` means "select" (enter selection mode). In this app, there's no text editing or selection, so using `s` for save is a conscious and reasonable deviation. The app is a read-only browser, not a text editor.

#### Interaction Flow for `s` (Quick Save)

1. User presses `s` while a link is highlighted in hierarchical view.
2. `BrowserOrchestrator.HandleCommandAsync` receives `CommandType.SaveToCollection`.
3. Orchestrator checks context: only acts in `ViewMode.Hierarchical` (in `CollectionList` view, `s` sets default collection instead -- see UI section).
4. Gets the currently selected `LinkNode` from the tree.
5. Calls `ICollectionService.SaveToDefaultCollectionAsync(node.Link.Url, node.Link.DisplayText)`.
6. Shows brief confirmation in status bar: `"Saved to 'Read Later'"` (clears on next keypress).
7. No view change. User stays exactly where they were.

#### Interaction Flow for `S` (Save to Specific Collection)

1. User presses `S`.
2. Prompt appears at bottom: `"Save to collection: "` (using existing `PromptForInputAsync` pattern).
3. User types collection name. Tab-completion of existing names is a Phase 2 enhancement.
4. On Enter: calls `SaveToCollectionByNameAsync` which creates the collection if it doesn't exist (case-insensitive match).
5. On Escape: cancel, return to current view.

---

### UI Views

#### Collections are a ViewMode

Add new `ViewMode` values to the existing enum:

```csharp
public enum ViewMode
{
    Hierarchical,
    Readable,
    CollectionList,    // NEW: shows list of all collections
    CollectionItems    // NEW: shows items in a specific collection
}
```

This is the cleanest approach because:
- The existing rendering pipeline (`BrowserOrchestrator.RenderAsync`) already dispatches on `ViewMode`.
- Navigation state (scroll offset, selection) is already per-view via `NavigationService`.
- It makes collections feel native -- same j/k, Enter, Backspace flow.

**Renderer changes:** Add `RenderCollectionList` and `RenderCollectionItems` methods to `IPageRenderer`. Add corresponding cases to the `RenderAsync` switch in `BrowserOrchestrator`. The new methods will need access to collection data, so the renderer interface gains overloads that accept collection data (or the orchestrator pre-packages the data into a render-friendly DTO).

#### CollectionList View

Renders like a hierarchical link tree, but the nodes are collections instead of extracted links:

```
 ╔═══════════════════════════════════════════════╗
 ║ Collections                                   ║
 ╚═══════════════════════════════════════════════╝

 → Read Later (12 items)           ★ default
   Tech (5 items)
   Weekend Reads (3 items)
   Politics (8 items)

 ─────────────────────────────────────────────────
 [Collections] j/k:move Enter:open s:set-default d:delete :new <name> q:quit
```

- `j/k` moves selection (same as link tree)
- `Enter` opens the collection (switches to `CollectionItems` view)
- `d` deletes the collection (**with confirmation prompt** -- "Delete 'Tech'? y/n" -- since this is destructive and affects multiple items)
- `s` sets as default collection (shows star indicator)
- `:new <name>` creates a new collection (command mode)
- `:rename <name>` renames the selected collection
- `Backspace/b` goes back to previous view

#### CollectionItems View

Renders like a hierarchical link tree, but items are `CollectionItem`s:

```
 ╔═══════════════════════════════════════════════╗
 ║ Read Later (12 items)                         ║
 ╚═══════════════════════════════════════════════╝

 → The Future of AI Regulation - nytimes.com
   How Terminal UIs Are Making a Comeback - arstechnica.com
   Understanding Rust Lifetimes - blog.rust-lang.org
   ● New JavaScript Frameworks in 2026 - smashingmagazine.com

 ─────────────────────────────────────────────────
 [ReadLater] j/k:move Enter:open d:remove J/K:reorder b:back :export q:quit
```

- `j/k` moves selection
- `Enter` navigates to the URL (loads the page, pushes to browser history). Auto-marks the item as read.
- `d` removes item from collection (**no confirmation** -- single item removal is low-stakes and should be instant)
- `J/K` (Shift+j/k) reorders items up/down (swaps `SavedAt` with adjacent item)
- `Backspace/b` goes back to collection list
- `:clear` removes all items (with confirmation)
- `:export` triggers export (see Problem 9 section)
- `●` marker indicates unread items (items not yet opened from the collection)

#### Launcher Home Screen -- DEFERRED

~~When the app starts without a URL argument, show a home screen with recent URLs and collections.~~

**This is deferred entirely.** The core collection feature (save with `s`, view with `:collections`/`:readlater`) is valuable without a home screen. Adding a home screen changes the app's startup flow, which overlaps with the architecture plan's Problem 5 (launcher rework). These should be solved together, not independently.

---

### Navigation Integration

#### How Collections Fit into the Navigation Stack

Collections views are NOT pushed onto the browser history stack (`_backHistory`/`_forwardHistory` in `NavigationService`). Instead, they are a parallel "mode" that the user can enter and exit.

**Implementation approach:** Add collection context to `NavigationService`:

```csharp
// In NavigationService -- new fields:
private Collection? _activeCollection;        // Currently viewed collection (for CollectionItems view)
private bool _inCollectionsMode;              // Whether we're in collections mode
private ViewMode _preCollectionsViewMode;     // View mode before entering collections
private int _preCollectionsScrollOffset;      // Scroll offset before entering collections
private int _collectionScrollOffset;          // Scroll offset within collection list
private int _collectionItemScrollOffset;      // Scroll offset within collection items
private int _collectionSelectedIndex;         // Selected index in collection list
private int _collectionItemSelectedIndex;     // Selected index in collection items

// New methods:
public void EnterCollections();               // Save current state, switch to CollectionList
public void EnterCollection(Collection c);    // Switch to CollectionItems for specific collection
public void ExitCollections();                // Restore previous state
public void ExitToCollectionList();           // Go from CollectionItems back to CollectionList
```

#### Back Button Behavior -- The Tricky Part

The critic correctly identified this as the trickiest navigation issue. Here's the detailed behavior:

| Current State | Back Action | Implementation |
|---|---|---|
| `CollectionItems` | Return to `CollectionList`, preserving scroll position | `ExitToCollectionList()` -- restores `_collectionScrollOffset` and `_collectionSelectedIndex` |
| `CollectionList` | Return to previous page/view (exit collections) | `ExitCollections()` -- restores `_preCollectionsViewMode`, `_preCollectionsScrollOffset`, and current page |
| Page loaded from collection item | Return to `CollectionItems`, preserving scroll position | See below |

**The "back from article to collection" problem:**

When the user opens a link from `CollectionItems`, the orchestrator calls `NavigateToAsync(url)` which pushes the current page onto `_backHistory` and resets scroll/view to `Hierarchical`. This destroys the collection context.

**Solution: Push a collection context marker before navigating.**

Before calling `NavigateToAsync`, the orchestrator saves the collection state:

```csharp
case CommandType.ActivateLink when _navigationService.CurrentContext.ViewMode == ViewMode.CollectionItems:
    // Save collection context before navigating
    _navigationService.SaveCollectionReturnPoint();
    // Navigate to the URL
    await NavigateToAsync(selectedItem.Url, options, cancellationToken);
    // Mark as read
    await _collectionService.MarkItemAsReadAsync(collectionId, itemId, ct);
    break;
```

`SaveCollectionReturnPoint()` stores `_activeCollection`, `_collectionItemScrollOffset`, and `_collectionItemSelectedIndex` in a dedicated field (`_collectionReturnPoint`). When `GoBack` is called and `_collectionReturnPoint` is set, instead of the normal back behavior, it restores the collection view:

```csharp
public Page? GoBack()
{
    // If we have a collection return point, go back to collection view
    if (_collectionReturnPoint != null)
    {
        RestoreCollectionReturnPoint();  // Restores collection state, sets ViewMode to CollectionItems
        _collectionReturnPoint = null;
        // Still pop the back history to get the previous page back
        if (_backHistory.Count > 0)
            _currentPage = _backHistory.Pop();
        return _currentPage;
    }

    // Normal back behavior...
}
```

This ensures the user's scroll position and selected item in the collection are preserved when they return.

---

### Implementation Order

1. **Domain entities** - `Collection`, `CollectionItem` (no dependencies)
2. **Persistence** - EF Core config, migration, repository (depends on #1; coordinate with architect on migration timing)
3. **Application service** - `ICollectionService` + implementation (depends on #2)
4. **Command types** - Add new commands to `CommandType` enum
5. **Input handling** - Add `s`, `S`, `d` as character-based handlers; add Shift+J/K to Shift block in `MapKeyToCommand`
6. **Navigation** - Add collection state tracking, `EnterCollections`/`ExitCollections`, `SaveCollectionReturnPoint`/`RestoreCollectionReturnPoint` to `NavigationService`
7. **Renderer** - Add `RenderCollectionList` and `RenderCollectionItems` to `IPageRenderer` and `TerminalPageRenderer` (builds on improved renderer from UI plan)
8. **Orchestrator** - Handle new commands in `BrowserOrchestrator.HandleCommandAsync`, including context-dependent behavior (e.g., `s` = save in Hierarchical, `s` = set default in CollectionList)
9. **Command mode** - Handle `:collections`, `:readlater`, `:save <name>`, `:new <name>`, `:rename <name>`, `:clear`, `:export`
10. **Tests** - Unit tests for service, repository, command parsing, navigation state transitions

---

### Testing Strategy

- **CollectionService tests**: Mock `ICollectionRepository`, verify CRUD operations, default collection tracking, case-insensitive name matching.
- **Input handler tests**: Verify `s` and `S` map to correct command types via `keyChar` detection (not `MapKeyToCommand`). Verify Shift+J/K emit `ReorderDown`/`ReorderUp`.
- **Orchestrator tests**: Verify `SaveToCollection` command calls service correctly in Hierarchical view and is context-dependent. Verify view mode transitions.
- **Navigation tests**: Verify `EnterCollections`/`ExitCollections` preserves and restores state. Verify `SaveCollectionReturnPoint` roundtrip (enter collection -> open article -> back -> collection scroll position preserved).
- **Repository integration tests**: Use in-memory SQLite to test actual persistence, case-insensitive name lookup, cascade delete (marked with Skip for CI).
- **Renderer tests**: Verify collection list and item views render correctly given mock data. Verify new methods exist on `IPageRenderer`.

---

## Problem 9: Export Collections to ElevenReader

### Research Findings

#### Does ElevenReader Have a Public API?

**No.** ElevenReader (elevenreader.io) does not expose a public API for creating collections, adding content, or managing a user's library programmatically.

**Evidence:**
- The ElevenLabs API (api.elevenlabs.io) covers TTS, voice cloning, dubbing, STT, music generation, and conversational AI. There are no endpoints for the Reader app's content library.
- The API documentation (elevenlabs.io/docs) lists 12 product areas. None relate to ElevenReader content management.
- The Knowledge Base API (`/v1/convai/knowledge-base/*`) is for conversational AI agent knowledge bases, not the Reader app library.
- No third-party libraries, GitHub repos, or community tools exist for programmatic ElevenReader integration.

#### How Does the Chrome Extension Work?

The official ElevenReader Chrome extension imports content by:
1. Capturing the full HTML of the current page (including behind paywalls if logged in).
2. Sending it to ElevenReader's backend via the user's authenticated session.
3. The extension requires the user to be signed into their ElevenReader account.

The extension does NOT expose an API. It uses internal endpoints that require browser-based authentication (likely cookies/session tokens from elevenreader.io). The extension source code is not open-source.

#### Undocumented API?

There are likely undocumented API endpoints behind elevenreader.io (the web app makes network requests to add/manage content). However:
- These are internal/private endpoints, not designed for third-party use.
- They would require reverse-engineering the web app's network traffic.
- Authentication likely uses session cookies tied to the elevenlabs.io account.
- Endpoints could change at any time without notice.
- Using undocumented APIs may violate ElevenLabs' Terms of Service.

#### Deep Link / URL Scheme?

No evidence of a public URL scheme (e.g., `elevenreader://add?url=...`) was found. The app uses standard web-based sharing links for published content (elevenreader.io links), but these are for consuming content, not adding it programmatically.

### Feasibility Assessment

| Approach | Feasibility | Risk | Effort |
|----------|-------------|------|--------|
| Official public API | Not feasible | N/A | N/A |
| Chrome extension protocol | Not feasible (closed source, no API) | N/A | N/A |
| Reverse-engineer web app API | Technically possible | High (breaking changes, ToS violation, auth complexity) | High |
| Use ElevenLabs TTS API directly | Feasible but different scope | Low | Medium |
| Share via URL/clipboard | Feasible (workaround) | Low | Low |

### Recommendation

**Do not implement direct ElevenReader integration at this time.** The risk/reward is unfavorable:
- No stable API exists.
- Reverse-engineering is fragile and potentially violates ToS.
- The feature request assumes an API that doesn't exist yet.

**Instead, implement export formats** that let the user manually import into ElevenReader or any other reader app:

1. **Export collection as URL list to file.** Default export (`:export` or `:export urls`). Writes one URL per line to a file in the output directory. The user can then paste these into ElevenReader's URL import.

2. **Export collection as OPML.** Standard format that many reader apps support. Future-proofs the export feature.

3. **Export collection as HTML bookmarks file.** Standard Netscape bookmarks format importable by every browser and many apps.

4. **Export collection as JSON.** For programmatic use or piping to other tools.

**Clipboard note:** .NET's `Clipboard` class only works on Windows with WinForms/WPF. On Linux/macOS, you'd need `xclip`/`xsel`/`pbcopy`. **Default to file output.** Clipboard is a platform-specific enhancement that can be added later with a platform-detection wrapper. Do not attempt cross-platform clipboard in Phase 1.

#### Export Scope

- `:export` commands work on the **currently viewed collection** (in `CollectionItems` view).
- **Exporting from CollectionList view** (e.g., export all collections) and **CLI export without entering the app** are deferred as future work.
- **Monitor for official API.** If ElevenLabs adds Reader endpoints, the `ICollectionExporter` interface makes it trivial to add an `ElevenReaderExporter` implementation.

#### Export Interface Design

```csharp
// ICollectionExporter.cs
public interface ICollectionExporter
{
    string Format { get; }  // "urls", "opml", "html", "json"
    Task<string> ExportAsync(Collection collection, ExportOptions options, CancellationToken ct = default);
}

public record ExportOptions
{
    public string OutputPath { get; init; }          // File path (always writes to file)
    public bool IncludeDatestamp { get; init; } = true;
    public string? CustomName { get; init; }         // Override collection name in export
}
```

#### Command Mode Integration

- `:export` or `:export urls` -- export current collection as URL list to file
- `:export opml` -- export as OPML file
- `:export html` -- export as bookmarks HTML
- `:export json` -- export as JSON

All export commands work on the currently viewed collection (in `CollectionItems` view). Output path defaults to `~/.termreader/exports/<collection-name>-<date>.<ext>`.

---

## File Changes Summary

### New Files

| File | Layer | Purpose |
|------|-------|---------|
| `Domain/Entities/Collections/Collection.cs` | Domain | Collection aggregate root |
| `Domain/Entities/Collections/CollectionItem.cs` | Domain | Saved link within a collection |
| `Application/Interfaces/ICollectionService.cs` | Application | Collection business logic interface |
| `Application/Interfaces/ICollectionExporter.cs` | Application | Export interface |
| `Infrastructure/Persistence/Configurations/CollectionConfiguration.cs` | Infrastructure | EF Core mapping |
| `Infrastructure/Persistence/Configurations/CollectionItemConfiguration.cs` | Infrastructure | EF Core mapping |
| `Infrastructure/Persistence/Repositories/CollectionRepository.cs` | Infrastructure | Data access |
| `Infrastructure/Collections/CollectionService.cs` | Infrastructure | Service implementation |
| `Infrastructure/Collections/UrlListExporter.cs` | Infrastructure | URL list export |
| `Infrastructure/Collections/OpmlExporter.cs` | Infrastructure | OPML export |
| `Tests/Collections/CollectionServiceTests.cs` | Tests | Unit tests |
| `Tests/Collections/CollectionRepositoryTests.cs` | Tests | Integration tests |

### Modified Files

| File | Change |
|------|--------|
| `Domain/Enums/Browser/ViewMode.cs` | Add `CollectionList`, `CollectionItems` |
| `Application/DTOs/Browser/NavigationCommand.cs` | Add `SaveToCollection`, `SaveToSpecific`, `OpenCollections`, `DeleteItem`, `ReorderUp`, `ReorderDown` to `CommandType` |
| `Infrastructure/Browser/UI/TerminalInputHandler.cs` | Add `s`, `S`, `d` as character-based handlers in `WaitForInputAsync`; add Shift+J/K to Shift block in `MapKeyToCommand`; update help text |
| `Infrastructure/Browser/BrowserOrchestrator.cs` | Handle new command types with context-dependent behavior; collection view rendering dispatch |
| `Infrastructure/Browser/NavigationService.cs` | Add collection state fields, `EnterCollections()`, `ExitCollections()`, `SaveCollectionReturnPoint()`, `RestoreCollectionReturnPoint()` |
| `Infrastructure/Browser/UI/TerminalPageRenderer.cs` | Add `RenderCollectionList()`, `RenderCollectionItems()` methods |
| `Infrastructure/Browser/BrowserDependencyInjection.cs` | Register `ICollectionService`, `ICollectionRepository`, exporters |
| `Infrastructure/Persistence/AppDbContext.cs` | Add `DbSet<Collection>`, `DbSet<CollectionItem>` |
| `Application/Interfaces/Browser/IPageRenderer.cs` | Add render methods for collection views |

---

## Resolved Questions

1. **`IsRead` semantics:** Means "opened from collection," not "ever read." Clarified above.
2. **Position/ordering:** Default is `SavedAt DESC`. No Position column. Reorder uses timestamp swap. Avoids N-update problem.
3. **Case-insensitive collection names:** Yes, using SQLite `COLLATE NOCASE`.
4. **`SourceUrl` field:** Removed. Not displayed anywhere.
5. **Helix `s` convention:** Acknowledged as conscious deviation. `s` for "select" has no meaning in a read-only browser.
6. **Shift+J/K handling:** Separate `ReorderUp`/`ReorderDown` command types. Input handler is stateless; orchestrator decides behavior based on ViewMode.
7. **`d` for delete:** Confirmation prompt for collection deletion (destructive). No confirmation for single item removal (low-stakes, instant).
8. **Launcher home screen:** Deferred entirely. Overlaps with architecture plan Problem 5.
9. **Back from article to collection:** Solved with `SaveCollectionReturnPoint`/`RestoreCollectionReturnPoint` pattern.
10. **Clipboard export:** Deferred. Default to file output. Cross-platform clipboard needs platform detection wrapper.
11. **Export from CollectionList / CLI:** Deferred as future work.

## Open Questions

1. **Should collections sync across devices?** Current plan is local-only (SQLite). If cross-device sync is needed later, the `ICollectionRepository` abstraction makes it easy to swap in a remote backend.

2. **Max collection count / item count?** No hard limits proposed. SQLite can handle thousands of items without issue. Add pagination if lists get very long (unlikely for personal use).

3. **Duplicate detection on save?** Should saving the same URL twice to the same collection be silently ignored, show a warning, or create a duplicate? Proposed: silently ignore (check URL uniqueness per collection before insert). This prevents accidental duplicates from rapid `s` presses.
