# Known Problems

## 1. Console warnings on first load

When launching `dotnet run --project src/TermReader.API -- browse https://macleans.ca`, a large number of warnings are printed to the console on first run. This clutters the output before the browser UI renders.

**Expected:** Clean startup with no warnings visible to the user.

## 2. Reader view still starts at bottom of article

When selecting an article link, the reader view opens with the scroll position at the bottom of the article instead of the top.

**Expected:** Reader view should always start at the top of the article (scroll offset = 0).

**Previous fix attempt:** `TerminalPageRenderer` was updated to track `_linesWritten` after header render and pass remaining height to content renderer instead of hardcoded `ContentHeight - 6`. The fix either didn't fully resolve the issue or has regressed.

## 3. Reader view navigation (j/k) is completely broken

Once in reader view, pressing `j` and `k` does nothing despite the on-screen instructions indicating they should scroll up/down. Navigation is non-functional in reader view.

**Expected:** `j` scrolls down, `k` scrolls up through the article content. This likely relates to issue #2 — if the scroll position and/or content height calculations are wrong, there may be no scrollable range detected.

## 4. Reader view is fundamentally broken — acts like terminal print, not a document viewer

The reader view should behave like a full-window Helix document (read-only, no insert/delete). Instead it acts more like content dumped to the terminal with minimal control. Specific issues:

- **Scrolling past article start:** After loading more than one article, it's possible to scroll up past the beginning of the current article into previously loaded content. The reader view is not properly isolated to its own viewport.
- **Line width ignores terminal width:** Text wrapping does not respect the actual width of the terminal window. Lines are either too short or too long regardless of terminal size.
- **Not a managed viewport:** Should fully own the terminal window like Helix does — clear the screen, render content within the window bounds, handle scroll within those bounds, and respond to terminal resize. Currently it seems to append/print rather than manage a fixed viewport.

## 5. Launcher and startup experience needs rework

- **Single command launch:** Should be able to run a single command (e.g. `termreader`) and get into the app immediately, then navigate to URLs from within.
- **Config inside the app:** All configuration and setup should be accessible from within the launcher itself, not require external dotnet user-secrets or appsettings files.
- **Slow startup:** The app is slow to launch and slow to load pages. Being a terminal app, it should feel significantly faster than a web browser — that's a core value proposition. Right now it's slower, which defeats the purpose.
- **Speed is a feature:** Page loads, navigation, and rendering should all be near-instant for simple pages. HTTP-first fetch should return in under a second for most sites. The terminal UI should render immediately with no perceptible delay.

## 6. URL loading is too slow — needs optimization and measurement

The slowest part of the experience is loading URLs. Several optimization opportunities:

- **Keep browser running:** Many sites block the initial HTTP fetch (403, Cloudflare, etc.) and we fall back to Selenium anyway. Instead of starting a browser on-demand each time, start a headless browser at app launch and keep it running. This eliminates the ~2-5s ChromeDriver startup overhead on every fallback.
- **Parallel fetch strategy:** Try HTTP and have the browser ready simultaneously, so fallback is instant rather than sequential.
- **Preload/prefetch:** When viewing a page's link list, speculatively prefetch likely targets (e.g. the first few visible links) in the background.
- **Measure realistic load times:** As part of testing, add instrumentation to measure end-to-end page load time in realistic scenarios (first load, cached load, HTTP-only sites, browser-fallback sites). Use these measurements to find bottlenecks and track improvements. Target benchmarks should be set based on equivalent browser load times — the terminal app should be faster, not slower.

## 7. Remove podcast and TTS API elements

The ElevenLabs TTS, Inworld TTS, podcast generation (RSS feed, chapters JSON), and M4B audiobook creation features should be removed. This simplifies the codebase, reduces dependencies, and sharpens the project's focus as a terminal browser rather than an audio scraping tool. This includes removing:

- ElevenLabs and Inworld audio generators
- AudioProcessor, ParallelAudioGenerator, ResilientAudioGenerator
- BudgetService, RateLimiter, AdaptiveRateLimiter
- ChapterMarker, ChaptersJsonGenerator, Mp3Tagger
- RssFeedGenerator
- Related configuration classes (ElevenLabsConfiguration, InworldConfiguration, AudioConfiguration)
- Related interfaces and DTOs
- The `scrape` and `podcast` command verbs (keep `browse`)
- Associated tests and dependencies (ElevenLabs-DotNet, FFMpegCore, ATL.NET)

## 8. Read-later list with collections

From the navigation view (link list), I want to quickly save articles to a read-later list.

- **Minimal keystrokes:** Saving should be a single keypress (e.g. `s` or `Space`) on a highlighted link. No confirmation dialogs or extra steps for the common case.
- **Smart default collection:** Articles save to the most recently used collection by default. No prompt unless the user explicitly wants to change the target.
- **Collection management:** Support creating and managing named collections (e.g. "Tech", "Politics", "Weekend reads"). Accessible via a command (e.g. `:collections`) but never required for basic save-to-read-later.
- **Collection switching:** A quick keybinding (e.g. `S` or `:save <collection>`) to pick a different collection when needed, but the default (last-used) should cover 90% of cases.
- **Viewing saved articles:** A way to browse saved collections and open articles from them (e.g. `:readlater` or a dedicated view).
- **Persistence:** Saved articles and collections should persist across sessions (local storage — SQLite or JSON file).

### Collection management details

- **Accessible from launcher:** Collections should be browsable directly from the launcher/home screen, not just via commands while browsing.
- **Accessible while navigating:** Should be able to jump to collections at any point while using the app (e.g. `:collections` or a keybinding), not only from the launcher.
- **Create:** Create new named collections from the launcher or via command.
- **Rename:** Rename existing collections.
- **Reorder items:** Reorder saved items within a collection (move up/down). New items always go to the top of the collection by default.
- **Remove item:** Remove individual items from a collection.
- **Clear collection:** Remove all items from a collection at once.
- **Delete collection:** Delete an entire collection.
- **Navigation feel:** Browsing collections should feel the same as browsing a webpage — same j/k navigation, Enter to open, Backspace/h to go back. Collections are just another navigable view, not a separate modal or settings screen.

## 9. Export collections to ElevenReader (feasibility research)

**Status: Research needed — gauge feasibility before planning implementation.**

I'd like to send an entire collection to ElevenReader as a collection. Requirements if feasible:

- **One-way sync:** Just push the collection over. No need to track changes, deletions, or sync state.
- **Datestamped naming:** Include a datestamp in the ElevenReader collection name (e.g. "Weekend reads - 2026-02-10").
- **Duplicates are fine:** Sending the same collection twice just creates a second collection in ElevenReader. No dedup needed.
- **Research tasks:**
  - Does ElevenReader have a public API or any programmatic way to create collections and add items?
  - What format does ElevenReader expect for articles/URLs?
  - Are there authentication requirements? OAuth, API keys, cookies?
  - Any existing integrations, browser extensions, or open-source tools that interact with ElevenReader programmatically?
  - If no official API exists, is there an unofficial/undocumented API that could be reverse-engineered from the app?
