# Architecture & Performance Plan (Problems 5-7)

> **Revision 2** -- Updated based on critic review. Changes marked with [REV].

## Cross-Plan Dependencies & Implementation Order

[REV] The recommended implementation order across all plans is:

1. **Problem 7** (audio/TTS removal) -- do FIRST to simplify the codebase
2. **Problem 1 + Problem 4** (UI rendering fixes) -- builds on cleaned codebase
3. **Problems 8-9** (collections feature) -- new functionality on clean foundation
4. **Problems 5 + 6** (startup/speed optimizations) -- polish on top of everything

This plan's changes to `Program.cs` and file deletions do not conflict with the UI renderer's changes to `BrowserOrchestrator.cs` and `TerminalPageRenderer.cs`. However, coordinate the `BrowserOrchestrator.RunAsync` entry point: this plan simplifies the launcher, while the UI plan adds alt screen buffer and resize handling inside `RunAsync`.

---

## Problem 5: Launcher and Startup Experience

### Root Cause Analysis

**Current state:** The app has two entry paths:
1. `browse` verb via `BrowseOptions` -- creates a minimal host with only `AddTerminalBrowser()`, prompts for URL if not provided.
2. Default verb via `CommandOptions` -- creates a full host with `AddInfrastructure(configuration)` which registers ElevenLabs, Inworld, Audio, Budget, Database, HealthChecks, Polly resilience policies, and more. This path validates ElevenLabs API key on startup (`ValidateOnStart()`), requires `secrets.json` or user-secrets, and runs health checks for FFmpeg.

**Why startup is slow:**
- `CreateHostBuilder` calls `AddInfrastructure()` which eagerly validates configuration (`ValidateOnStart()`) including ElevenLabs API key, audio config, etc. -- even for browse mode when those services are never used.
- The `browse` verb path (`CreateBrowseHostBuilder`) is lighter but still initializes the full .NET Generic Host with Serilog.
- No configuration is pre-warmed or cached between runs.

**Why the user experience is clunky:**
- Two command verbs (`browse` and default/scrape) with overlapping `--browse` flag on the default verb. Confusing.
- Configuration requires external `secrets.json` or `dotnet user-secrets` -- not discoverable from within the app.
- No single-command launch. User must know about the `browse` verb or pass `--browse` flag.

### Proposed Fix

#### 5.1: Make `browse` the default verb (no verb required)

**Change:** When the user runs `termreader` (or `dotnet run`) with no arguments, launch directly into browse mode with a default URL (e.g., Hacker News). The current default verb (scraping/audio) is removed entirely by Problem 7.

**Implementation:**
[REV] The `CommandLineParser` library does not natively support a default verb without a `[Verb]` attribute. The recommended approach is to **detect empty/no-verb args BEFORE calling `parser.ParseArguments`** and bypass the parser for the default browse case:

```csharp
public static async Task<int> Main(string[] args)
{
    // If no arguments or first arg is not a known verb, treat as browse
    if (args.Length == 0 || !args[0].StartsWith('-') && !IsKnownVerb(args[0]))
    {
        // Treat first positional arg as URL, or use default
        var url = args.Length > 0 ? args[0] : null;
        return await RunBrowseVerbAsync(new BrowseOptions { Url = url });
    }

    // Only parse if there are explicit flags
    var parser = new Parser(with => with.HelpWriter = Console.Error);
    var result = parser.ParseArguments<BrowseOptions>(args);
    return await result.MapResult(
        async (BrowseOptions opts) => await RunBrowseVerbAsync(opts),
        errs => Task.FromResult(1));
}
```

- Remove the URL prompt. If no URL is given, default to `https://news.ycombinator.com` silently.
- After Problem 7 removes the scrape verb, `CommandOptions` is deleted entirely.

**Files to change:**
- `/workspace/src/TermReader.API/Program.cs` -- change argument parsing logic
- `/workspace/src/TermReader.API/CommandOptions.cs` -- remove `CommandOptions` entirely (done as part of Problem 7)

#### 5.2: Suppress console noise on startup

[REV] **Ownership note:** The UI renderer plan (Problem 1) also proposes suppressing console warnings. That plan owns the Serilog reconfiguration for browse mode. This plan defers to Problem 1 for that fix and focuses only on the startup speed aspects (removing unnecessary service initialization).

The key startup speed win is removing `AddInfrastructure()` from the browse path entirely (already the case -- `CreateBrowseHostBuilder` only calls `AddTerminalBrowser()`). After Problem 7 removes the scrape code, `AddInfrastructure()` is deleted, eliminating the slow path altogether.

#### 5.3: In-app configuration (future, lower priority)

**Change:** Add a `:config` or `:settings` command in the browser's command-line input handler. This would allow setting browser preferences, etc. from within the app.

**Implementation (sketch):**
- Add a `CommandType.OpenSettings` to the navigation command enum.
- Render a settings view (key-value pairs with inline editing).
- [REV] Persist settings using **EF Core + SQLite** (a simple `Settings` key-value table), NOT a separate JSON file. This aligns with the collections plan which already uses EF Core for persistence. Do not introduce a second persistence mechanism.
- This is lower priority and can be deferred. The immediate win is making browse mode work without any config at all (no API keys needed for plain browsing).

**Files to change:**
- `/workspace/src/TermReader.Application/DTOs/Browser/NavigationCommand.cs` -- add `OpenSettings` command type
- `/workspace/src/TermReader.Infrastructure/Browser/BrowserOrchestrator.cs` -- handle settings command
- New entity: `Setting` with key/value columns in existing SQLite database

---

## Problem 6: URL Loading Too Slow

### Root Cause Analysis

**Current flow (`PageLoader.LoadAsync`):**
1. Try HTTP fetch first (via `HttpClient`)
2. If HTTP fails (403, JS-required), fall back to browser via `BrowserFetchAsync`
3. `BrowserFetchAsync` calls `_browserSession.GetOrCreateDriver(headless)` -- this creates a new ChromeDriver on first call (~2-5s cold start)
4. After navigation, `WaitForPageLoadAsync` has two hardcoded `Task.Delay` calls: 1000ms + 2000ms = **3 seconds of mandatory waiting** after every browser page load

**Why it's slow:**
1. **ChromeDriver cold start:** `BrowserSession.GetOrCreateDriver()` creates a new ChromeDriver lazily on first browser fallback. This adds ~2-5 seconds the first time. Subsequent loads reuse the driver (good), but the first one is painful.
2. **Hardcoded 3-second delay:** `WaitForPageLoadAsync` at lines 188-191 has `Task.Delay(1000)` + `Task.Delay(2000)` after `document.readyState == "complete"`. These are unnecessary padding. The readyState check already ensures the page is loaded.
3. **Sequential strategy:** HTTP is tried first, and only after it fails does the browser start. If the HTTP attempt takes 5-10 seconds to timeout before failing, the user waits for HTTP timeout + browser startup + page load + 3s delay.
4. **No prefetching:** When viewing a link list, no links are prefetched in the background.

### Proposed Fix

#### 6.1: Start browser session eagerly at app launch

**Change:** Start the ChromeDriver session eagerly in the background when the app launches, rather than lazily on first browser fallback.

**Implementation:**
- Add an `IBrowserSession.WarmUpAsync()` method that creates the driver in a background task.
- [REV] Use proper error handling for the fire-and-forget warmup. Log failures rather than silently swallowing exceptions:
  ```csharp
  var session = host.Services.GetRequiredService<IBrowserSession>();
  _ = Task.Run(async () =>
  {
      try
      {
          await session.WarmUpAsync();
      }
      catch (Exception ex)
      {
          Log.Warning(ex, "Browser warmup failed, will retry on first navigation");
      }
  });
  await browser.RunAsync(url);
  ```
- [REV] **Concurrency note:** If the first navigation happens BEFORE warmup completes, `GetOrCreateDriver()` is safe because it uses a `lock` internally. The warmup and the first navigation will serialize on the lock -- whichever gets the lock first creates the driver, and the other reuses it. This is correct behavior but should be documented in the code for future developers.

**Files to change:**
- `/workspace/src/TermReader.Infrastructure/Browser/IBrowserSession.cs` -- add `WarmUpAsync()`
- `/workspace/src/TermReader.Infrastructure/Browser/BrowserSession.cs` -- implement `WarmUpAsync()`
- `/workspace/src/TermReader.API/Program.cs` -- call warmup in `RunBrowseVerbAsync`

#### 6.2: Remove hardcoded delays in WaitForPageLoadAsync

**Change:** Remove the 3-second hardcoded delay in `PageLoader.WaitForPageLoadAsync()` (lines 188-191).

**Implementation:**
- Remove `await Task.Delay(1000, cancellationToken)` (line 188) and `await Task.Delay(2000, cancellationToken)` (line 191).
- The `WebDriverWait` for `document.readyState == "complete"` is sufficient for most pages.
- [REV] **Safety valve:** Add a configurable `Browser:PostLoadDelayMs` setting (default `0`) to `BrowserConfiguration`. SPAs and JS-heavy sites sometimes load content asynchronously after `readyState == "complete"`. Users who encounter such sites can set this to a non-zero value (e.g., `500`) without code changes. This replaces the blanket 3s delay with an opt-in mechanism.

```csharp
// In BrowserConfiguration:
public int PostLoadDelayMs { get; init; } = 0;

// In WaitForPageLoadAsync:
if (_browserConfig.PostLoadDelayMs > 0)
{
    await Task.Delay(_browserConfig.PostLoadDelayMs, cancellationToken);
}
```

**Files to change:**
- `/workspace/src/TermReader.Infrastructure/Browser/PageLoader.cs` -- remove hardcoded `Task.Delay`, add configurable delay
- `/workspace/src/TermReader.Infrastructure/Configuration/BrowserConfiguration.cs` -- add `PostLoadDelayMs` property

#### 6.3: Timeout-based HTTP-then-browser strategy (replaces parallel fetch)

[REV] **Revised approach.** The original parallel fetch proposal using `Task.WhenAny` had a critical flaw: the WebDriver is a SINGLE browser tab. If HTTP and browser fetch race simultaneously and HTTP wins, the browser driver is left in an unexpected state (it navigated to a page the user didn't ask to see). The next browser fallback would find the driver on the wrong page.

**New approach -- timeout-based sequential with short HTTP timeout:**

Instead of true parallelism, use a short timeout on the HTTP attempt. If HTTP doesn't succeed quickly, start the browser fetch immediately. The browser session is already warmed up (6.1), so the fallback is fast.

```csharp
public async Task<PageLoadResult> LoadAsync(PageLoadRequest request, CancellationToken cancellationToken)
{
    if (_httpClient != null)
    {
        // Try HTTP with a short timeout (3 seconds)
        using var httpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        httpCts.CancelAfter(3000);

        var httpResult = await TryHttpFetchAsync(request, httpCts.Token);
        if (httpResult.Success)
        {
            return httpResult;
        }

        _logger.LogDebug("HTTP fetch did not succeed in 3s, falling back to browser");
    }

    // Browser fallback (fast because driver is pre-warmed)
    return await BrowserFetchAsync(request, cancellationToken);
}
```

**Why this is better than parallel:**
- No WebDriver state confusion -- the browser only navigates when we commit to using it
- Simple control flow, easy to reason about
- The 3-second HTTP timeout is generous enough for fast sites but doesn't waste time on 403s or Cloudflare blocks (which respond quickly with error codes)
- Combined with eager browser warmup (6.1), the total fallback path is: ~0-3s HTTP attempt + ~1-2s browser navigation = 3-5s worst case, vs. the current ~5-10s

**Files to change:**
- `/workspace/src/TermReader.Infrastructure/Browser/PageLoader.cs` -- add short timeout to HTTP path

#### 6.4: Prefetch visible links (future enhancement)

**Change:** When viewing a link list, prefetch the first N visible links in the background using HTTP.

**Implementation (sketch):**
- After rendering a page's link list, identify the top 3-5 visible links.
- Fire off background HTTP fetches for those URLs.
- Cache the results (HTML + metadata) in an in-memory dictionary.
- When the user selects a link, check the prefetch cache before making a new request.
- This is a nice-to-have and can be deferred to a later iteration. The other fixes (6.1-6.3) will provide the biggest speed gains.

**Files to change:**
- `/workspace/src/TermReader.Infrastructure/Browser/PageLoader.cs` -- add prefetch cache
- `/workspace/src/TermReader.Infrastructure/Browser/BrowserOrchestrator.cs` -- trigger prefetch after render

#### 6.5: Add load time instrumentation

**Change:** Measure and log end-to-end page load time for every navigation.

**Implementation:**
[REV] Add `Stopwatch` instrumentation at two levels:
1. **In `PageLoader.LoadAsync`** -- measure per-strategy timing (HTTP attempt duration, browser attempt duration, total duration). Log which strategy won.
2. **In `BrowserOrchestrator.LoadPageAsync`** -- measure total end-to-end time including link extraction and tree building.
- Output at `Debug` level so it doesn't clutter the UI, but can be seen in log files.

**Files to change:**
- `/workspace/src/TermReader.Infrastructure/Browser/BrowserOrchestrator.cs` -- add timing around `LoadPageAsync`
- `/workspace/src/TermReader.Infrastructure/Browser/PageLoader.cs` -- log per-strategy timing

---

## Problem 7: Remove Podcast and TTS API Elements

### Root Cause Analysis

The codebase evolved from an NYT audio scraping tool into a terminal browser. The audio/TTS/podcast infrastructure is dead weight that:
- Adds ~10 NuGet packages that aren't needed for browsing
- Requires API key configuration (ElevenLabs, Inworld) that fails validation on startup
- Adds complexity to DI registration, health checks, and command options
- Increases build time and binary size

### Removal Plan: Complete Inventory

Below is an exhaustive list of everything to remove, organized by layer.

#### 7.1: Application Layer -- Interfaces to Remove

| File | Reason |
|------|--------|
| `src/TermReader.Application/Interfaces/IAudioGenerator.cs` | ElevenLabs TTS interface |
| `src/TermReader.Application/Interfaces/IAudioProcessor.cs` | FFmpeg audio processing interface |
| `src/TermReader.Application/Interfaces/IBudgetService.cs` | TTS budget tracking interface |
| `src/TermReader.Application/Interfaces/IChapterMarker.cs` | M4B chapter marker interface |
| `src/TermReader.Application/Interfaces/IChaptersJsonGenerator.cs` | Podcasting 2.0 chapters JSON interface |
| `src/TermReader.Application/Interfaces/IMp3Tagger.cs` | MP3 ID3 tagging interface (includes `Mp3Metadata` record) |
| `src/TermReader.Application/Interfaces/IParallelAudioGenerator.cs` | Parallel audio generation interface |
| `src/TermReader.Application/Interfaces/IRateLimiter.cs` | Rate limiter for audio API calls |
| `src/TermReader.Application/Interfaces/IRssFeedGenerator.cs` | RSS feed generation interface (includes `PodcastEpisode`, `PodcastInfo`, `CombinedPodcastEpisode` records) |
| `src/TermReader.Application/Interfaces/IAudioCache.cs` | Audio file caching interface (includes `CacheStatistics` class) |
| `src/TermReader.Application/Interfaces/BudgetSummary.cs` | Budget summary DTO |
| `src/TermReader.Application/Interfaces/IScraperService.cs` | NYT scraper interface (scrape verb) |
| `src/TermReader.Application/Interfaces/IScrapingSessionRepository.cs` | Scraping session persistence |
| `src/TermReader.Application/Interfaces/ICacheService.cs` | [REV] Generic cache interface -- only used by `IArticleCache` which is being removed |
| `src/TermReader.Application/Interfaces/IArticleCache.cs` | [REV] Article caching -- only used by `ScraperService` (scraping workflow), not by browse mode |
| `src/TermReader.Application/Interfaces/IArticleParser.cs` | [REV] NYT article parser -- only used by `ScraperService`, not by browse mode. The browser uses `ReadableContentExtractor` instead (independent implementation) |
| `src/TermReader.Application/Interfaces/IArticleRepository.cs` | [REV] Article repository -- used by `ArticleCache` and `ScraperService`, both being removed. Collections feature uses its own `CollectionItem` entity. Coordinate with collections agent to confirm. |

#### 7.2: Domain Layer -- Entities and Value Objects to Remove

| File | Reason |
|------|--------|
| `src/TermReader.Domain/Entities/AudioChapter.cs` | Audiobook chapter entity |
| `src/TermReader.Domain/Entities/AudioGenerationResult.cs` | Audio generation result entity |
| `src/TermReader.Domain/Entities/ScrapingSession.cs` | Scraping session entity (includes `ScrapingStatus` enum) |
| `src/TermReader.Domain/ValueObjects/AudioMetadata.cs` | Audio file metadata value object |
| `src/TermReader.Domain/ValueObjects/ArticleContent.cs` | [REV] Only used for audio cost estimation (`EstimatedAudioCost`). Not referenced by browser code. |
| `src/TermReader.Domain/Entities/Article.cs` | [REV] NYT-specific article entity with `AudioFilePath`, `ScrapedDate`, `EstimatedWordCount`. Collections feature uses its own `CollectionItem` entity (per collections plan). Remove entirely. |

#### 7.3: Infrastructure Layer -- Implementations to Remove

**Audio directory (delete entire directory `src/TermReader.Infrastructure/Audio/`):**
| File | Reason |
|------|--------|
| `Audio/AdaptiveRateLimitHandler.cs` | HTTP handler for adaptive rate limiting |
| `Audio/AdaptiveRateLimiter.cs` | Adaptive rate limiter implementation |
| `Audio/AudioGenerator.cs` | ElevenLabs TTS implementation |
| `Audio/AudioProcessor.cs` | FFmpeg audio processing implementation |
| `Audio/BudgetService.cs` | Budget tracking implementation |
| `Audio/ChapterMarker.cs` | M4B chapter marker implementation |
| `Audio/InworldAudioGenerator.cs` | Inworld TTS fallback implementation |
| `Audio/Mp3Tagger.cs` | MP3 ID3 tagging implementation |
| `Audio/ParallelAudioGenerator.cs` | Parallel audio generation implementation |
| `Audio/RateLimiter.cs` | Rate limiter implementation |
| `Audio/ResilientAudioGenerator.cs` | Resilient TTS with fallback |

**Podcast directory (delete entire directory `src/TermReader.Infrastructure/Podcast/`):**
| File | Reason |
|------|--------|
| `Podcast/ChaptersJsonGenerator.cs` | Podcasting 2.0 chapters JSON |
| `Podcast/RssFeedGenerator.cs` | RSS feed generation |

**Caching (delete entire directory `src/TermReader.Infrastructure/Caching/`):**
| File | Reason |
|------|--------|
| `Caching/AudioCache.cs` | Disk-based audio cache |
| `Caching/ArticleCache.cs` | [REV] Two-tier article cache -- only used by `ScraperService` |

**Configuration:**
| File | Reason |
|------|--------|
| `Configuration/AudioConfiguration.cs` | Audio processing config |
| `Configuration/ElevenLabsConfiguration.cs` | ElevenLabs API config |
| `Configuration/InworldConfiguration.cs` | Inworld API config |
| `Configuration/Validation/AudioConfigurationValidator.cs` | Audio config validator |
| `Configuration/Validation/ElevenLabsConfigurationValidator.cs` | ElevenLabs config validator |

**Health checks:**
| File | Reason |
|------|--------|
| `Health/FFmpegHealthCheck.cs` | FFmpeg availability check (only needed for audio processing) |

**Persistence:**
| File | Reason |
|------|--------|
| `Persistence/Configurations/AudioChapterConfiguration.cs` | EF Core config for AudioChapter table |
| `Persistence/Configurations/ScrapingSessionConfiguration.cs` | EF Core config for ScrapingSession table |
| `Persistence/Configurations/ArticleConfiguration.cs` | [REV] EF Core config for Article table -- removed with Article entity |
| `Persistence/Repositories/ScrapingSessionRepository.cs` | Scraping session repository implementation |
| `Persistence/Repositories/ArticleRepository.cs` | [REV] Article repository implementation -- removed with Article entity |

**Metrics (delete entire directory `src/TermReader.Infrastructure/Metrics/`):**
| File | Reason |
|------|--------|
| `Metrics/MetricsSummary.cs` | Only used for audio/scraping performance tracking |
| `Metrics/OperationMetrics.cs` | Same |
| `Metrics/PerformanceMetrics.cs` | Same |

**Parsing (delete entire directory `src/TermReader.Infrastructure/Parsing/`):**
| File | Reason |
|------|--------|
| `Parsing/ArticleParser.cs` | [REV] NYT-specific article parser -- only used by `ScraperService`. Browser uses `ReadableContentExtractor` (independent implementation in Browser/ directory). The comment in `IReadableContentExtractor.cs` saying "Wraps the existing ArticleParser" is misleading -- the implementation has no dependency on `ArticleParser`. |

**Browser -- scraping-specific files:**
| File | Reason |
|------|--------|
| `Browser/ScraperService.cs` | NYT scraper implementation |
| `Browser/AuthService.cs` | NYT authentication (login flow) |
| `Browser/IAuthService.cs` | Auth service interface |

**Resilience (delete entire directory `src/TermReader.Infrastructure/Resilience/`):**
| File | Reason |
|------|--------|
| `Resilience/PollyContextExtensions.cs` | Only used for ElevenLabs HTTP client |

**HTTP (delete entire directory `src/TermReader.Infrastructure/Http/`):**
| File | Reason |
|------|--------|
| `Http/HttpResilienceLogger.cs` | Only used for ElevenLabs HTTP client |

#### 7.4: Infrastructure Layer -- Files to Modify

| File | Changes |
|------|---------|
| `src/TermReader.Infrastructure/DependencyInjection.cs` | **Remove entirely.** This file registers ALL the audio/scraping services. The browse path already uses `BrowserDependencyInjection.cs` instead. [REV] Database registration (EF Core + SQLite) must be moved to `BrowserDependencyInjection.cs` or a new `PersistenceDependencyInjection.cs` because the collections feature depends on it. Cookie-related services should also be moved. |
| `src/TermReader.Infrastructure/Persistence/AppDbContext.cs` | Remove `DbSet<ScrapingSession>`, `DbSet<AudioChapter>`, `DbSet<Article>`. Remove their configurations from `OnModelCreating`. Collections feature will add its own `DbSet<CollectionItem>` etc. |
| `src/TermReader.Infrastructure/Browser/BrowserDependencyInjection.cs` | [REV] Add database registration (moved from `DependencyInjection.cs`): EF Core, SQLite, cookie services. This ensures browse mode has database access for collections. |

#### 7.5: API Layer -- Files to Modify

| File | Changes |
|------|---------|
| `src/TermReader.API/Program.cs` | Remove: `RunApplicationAsync` (lines 391-583), `RunAudioOnlyWorkflowAsync` (lines 585-1040), `RunProductionWorkflowAsync` (lines 1042-1608), `RunWithOptionsAsync` (lines 55-113), `HandleCookieInfoAsync`, `HandleClearCookiesAsync`, `HandleImportCookiesAsync`, `CreateHostBuilder`. Remove `using` statements for Audio, Podcast, Metrics, Persistence namespaces. Simplify `Main` to only support browse. [REV] **Keep cookie management commands** -- they enable authenticated browsing (paywall bypass for subscribers). Move them to work within the browse host. |
| `src/TermReader.API/CommandOptions.cs` | Remove entire `CommandOptions` class. Keep only `BrowseOptions`. |

#### 7.6: Test Files to Remove

| File | Reason |
|------|--------|
| `tests/TermReader.Tests/AudioGeneratorTests.cs` | Tests for ElevenLabs audio generation |
| `tests/TermReader.Tests/AudioCacheTests.cs` | Tests for audio caching |
| `tests/TermReader.Tests/BudgetServiceTests.cs` | Tests for budget tracking |
| `tests/TermReader.Tests/ChaptersJsonGeneratorTests.cs` | Tests for chapters JSON generation |
| `tests/TermReader.Tests/RssFeedGeneratorTests.cs` | Tests for RSS feed generation |
| `tests/TermReader.Tests/ParallelAudioGeneratorTests.cs` | Tests for parallel audio generation |
| `tests/TermReader.Tests/RateLimiterTests.cs` | Tests for rate limiter |
| `tests/TermReader.Tests/HttpResilienceLoggerTests.cs` | Tests for HTTP resilience logger |
| `tests/TermReader.Tests/ScrapingSessionRepositoryTests.cs` | Tests for scraping session repo |
| `tests/TermReader.Tests/ArticleParserTests.cs` | [REV] Tests for NYT article parser (being removed) |
| `tests/TermReader.Tests/ArticleRepositoryTests.cs` | [REV] Tests for article repository (being removed) |

**Tests to modify:**
| File | Changes |
|------|---------|
| `tests/TermReader.Tests/DependencyInjectionTests.cs` | Update to reflect new minimal DI without audio/scraping services |
| `tests/TermReader.Tests/CommandOptionsTests.cs` | Update to test only `BrowseOptions` |
| `tests/TermReader.Tests/InterfaceIntegrationTests.cs` | Remove audio/scraping interface tests |
| `tests/TermReader.Tests/CookieImporterTests.cs` | Keep -- cookies still needed for browse mode |
| `tests/TermReader.Tests/CookieManagerTests.cs` | Keep |
| `tests/TermReader.Tests/CookieEncryptionServiceTests.cs` | Keep |
| `tests/TermReader.Tests/UnitOfWorkTests.cs` | Keep -- EF Core still used for collections |

#### 7.7: NuGet Packages to Remove

From `src/TermReader.Infrastructure/TermReader.Infrastructure.csproj`:

| Package | Reason |
|---------|--------|
| `ElevenLabs-DotNet` (3.6.0) | ElevenLabs TTS client |
| `FFMpegCore` (5.1.0) | FFmpeg audio processing |
| `TagLibSharp` (2.3.0) | MP3 ID3 tagging |
| `z440.atl.core` (6.1.0) | ATL.NET for M4B chapter markers |
| `Microsoft.Extensions.Http.Polly` (9.0.10) | Polly HTTP policies (only used for ElevenLabs client) |
| `Polly` (8.5.0) | Resilience policies (only used for audio) |

**Keep:**
- `Selenium.WebDriver` -- needed for browser fallback
- `HtmlAgilityPack` -- needed for HTML parsing in PageLoader/LinkExtractor/ReadableContentExtractor
- `Microsoft.EntityFrameworkCore` + `Microsoft.EntityFrameworkCore.Sqlite` -- needed for collections feature
- `Microsoft.AspNetCore.DataProtection` -- needed for cookie encryption
- `Terminal.Gui` -- check usage; keep if used by UI, remove if not
- All other `Microsoft.Extensions.*` except `Http.Polly`

#### 7.8: Configuration to Remove from appsettings.json

Remove these sections from `src/TermReader.API/appsettings.json`:
- `ElevenLabs` section (lines 57-62)
- `Inworld` section (lines 63-69)
- `Audio` section (lines 70-78)
- `Scraping` section entirely (lines 33-47) -- NYT-specific selectors

**Keep:**
- `Serilog` section
- `Auth` section -- [REV] Keep for cookie-based auth in browse mode
- `Browser` section

#### 7.9: Docker/Deployment Files to Modify

| File | Changes |
|------|---------|
| `Dockerfile` | Remove `ffmpeg` from `apt-get install` list (line 38). Keep Chromium/ChromeDriver and Xvfb (needed for browser). |
| `docker-compose.yml` | Remove `ElevenLabs__ApiKey` environment variable. Keep `Auth__Email` and `Auth__Password` if cookie auth is retained. |

#### 7.10: Migrations

[REV] **Important:** Removing entities that have existing EF Core migrations (`AudioChapter`, `ScrapingSession`, `Article`) requires migration cleanup. Since the collections feature needs a new migration anyway, the recommended approach is:

1. Delete ALL existing migration files under `src/TermReader.Infrastructure/Migrations/`
2. The collections feature will create a fresh `InitialCreate` migration with only the collections tables
3. This avoids schema conflicts and keeps the migration history clean

Files to delete:
- `src/TermReader.Infrastructure/Migrations/20251120001514_InitialCreate.Designer.cs`
- `src/TermReader.Infrastructure/Migrations/20251120001514_InitialCreate.cs`
- `src/TermReader.Infrastructure/Migrations/AppDbContextModelSnapshot.cs`

#### 7.11: Other Files

| File | Changes |
|------|---------|
| `.gitignore` | Review for audio-specific patterns (e.g., `*.m4b`, `*.mp3`, `output/`) |
| `CLAUDE.md` | Update to reflect new architecture (remove references to audio, podcast, ElevenLabs, FFmpeg, ATL.NET, etc.) |
| `src/TermReader.Application/Interfaces/Browser/IReadableContentExtractor.cs` | [REV] Update misleading comment "Wraps the existing ArticleParser" -- it does not depend on ArticleParser |

### Removal Order (recommended)

[REV] Updated order with critic feedback:

0. **Create a feature branch** and plan to do the removal in a **single commit** to avoid intermediate broken states from dangling references.
1. **Delete test files** (7.6) -- removes leaves first
2. **Delete Application interfaces** (7.1) and **Domain entities** (7.2)
3. **Delete Infrastructure implementations** (7.3) -- Audio/, Podcast/, Caching/, Parsing/, Configuration/Audio+ElevenLabs+Inworld, Health/FFmpeg, Persistence/AudioChapter+ScrapingSession+Article configs+repos, Browser/Scraper+Auth, Resilience/, Http/, Metrics/
4. **Delete migrations** (7.10)
5. **Remove `DependencyInjection.cs`** and **move essential services** (database, cookies) to `BrowserDependencyInjection.cs` (7.4)
6. **Simplify API layer** (7.5) -- gut Program.cs, delete CommandOptions
7. **Remove NuGet packages** (7.7) -- edit .csproj files
8. **Clean up configuration** (7.8) -- appsettings.json
9. **Clean up Docker** (7.9)
10. **Build and fix** -- resolve any remaining compilation errors from dangling references
11. **Run tests** -- ensure remaining tests pass

### Summary: Complete File Deletion List

```
# Application interfaces (17 files)
src/TermReader.Application/Interfaces/IAudioGenerator.cs
src/TermReader.Application/Interfaces/IAudioProcessor.cs
src/TermReader.Application/Interfaces/IBudgetService.cs
src/TermReader.Application/Interfaces/IChapterMarker.cs
src/TermReader.Application/Interfaces/IChaptersJsonGenerator.cs
src/TermReader.Application/Interfaces/IMp3Tagger.cs
src/TermReader.Application/Interfaces/IParallelAudioGenerator.cs
src/TermReader.Application/Interfaces/IRateLimiter.cs
src/TermReader.Application/Interfaces/IRssFeedGenerator.cs
src/TermReader.Application/Interfaces/IAudioCache.cs
src/TermReader.Application/Interfaces/ICacheService.cs
src/TermReader.Application/Interfaces/IArticleCache.cs
src/TermReader.Application/Interfaces/IArticleParser.cs
src/TermReader.Application/Interfaces/IArticleRepository.cs
src/TermReader.Application/Interfaces/BudgetSummary.cs
src/TermReader.Application/Interfaces/IScraperService.cs
src/TermReader.Application/Interfaces/IScrapingSessionRepository.cs

# Domain entities/value objects (6 files)
src/TermReader.Domain/Entities/AudioChapter.cs
src/TermReader.Domain/Entities/AudioGenerationResult.cs
src/TermReader.Domain/Entities/ScrapingSession.cs
src/TermReader.Domain/Entities/Article.cs
src/TermReader.Domain/ValueObjects/AudioMetadata.cs
src/TermReader.Domain/ValueObjects/ArticleContent.cs

# Infrastructure - Audio (11 files, entire directory)
src/TermReader.Infrastructure/Audio/AdaptiveRateLimitHandler.cs
src/TermReader.Infrastructure/Audio/AdaptiveRateLimiter.cs
src/TermReader.Infrastructure/Audio/AudioGenerator.cs
src/TermReader.Infrastructure/Audio/AudioProcessor.cs
src/TermReader.Infrastructure/Audio/BudgetService.cs
src/TermReader.Infrastructure/Audio/ChapterMarker.cs
src/TermReader.Infrastructure/Audio/InworldAudioGenerator.cs
src/TermReader.Infrastructure/Audio/Mp3Tagger.cs
src/TermReader.Infrastructure/Audio/ParallelAudioGenerator.cs
src/TermReader.Infrastructure/Audio/RateLimiter.cs
src/TermReader.Infrastructure/Audio/ResilientAudioGenerator.cs

# Infrastructure - Podcast (2 files, entire directory)
src/TermReader.Infrastructure/Podcast/ChaptersJsonGenerator.cs
src/TermReader.Infrastructure/Podcast/RssFeedGenerator.cs

# Infrastructure - Caching (2 files, entire directory)
src/TermReader.Infrastructure/Caching/AudioCache.cs
src/TermReader.Infrastructure/Caching/ArticleCache.cs

# Infrastructure - Configuration (5 files)
src/TermReader.Infrastructure/Configuration/AudioConfiguration.cs
src/TermReader.Infrastructure/Configuration/ElevenLabsConfiguration.cs
src/TermReader.Infrastructure/Configuration/InworldConfiguration.cs
src/TermReader.Infrastructure/Configuration/Validation/AudioConfigurationValidator.cs
src/TermReader.Infrastructure/Configuration/Validation/ElevenLabsConfigurationValidator.cs

# Infrastructure - Health (1 file)
src/TermReader.Infrastructure/Health/FFmpegHealthCheck.cs

# Infrastructure - Persistence (5 files)
src/TermReader.Infrastructure/Persistence/Configurations/AudioChapterConfiguration.cs
src/TermReader.Infrastructure/Persistence/Configurations/ScrapingSessionConfiguration.cs
src/TermReader.Infrastructure/Persistence/Configurations/ArticleConfiguration.cs
src/TermReader.Infrastructure/Persistence/Repositories/ScrapingSessionRepository.cs
src/TermReader.Infrastructure/Persistence/Repositories/ArticleRepository.cs

# Infrastructure - Parsing (1 file, entire directory)
src/TermReader.Infrastructure/Parsing/ArticleParser.cs

# Infrastructure - Browser, scraping-specific (3 files)
src/TermReader.Infrastructure/Browser/ScraperService.cs
src/TermReader.Infrastructure/Browser/AuthService.cs
src/TermReader.Infrastructure/Browser/IAuthService.cs

# Infrastructure - Other (5 files, 3 entire directories)
src/TermReader.Infrastructure/Resilience/PollyContextExtensions.cs
src/TermReader.Infrastructure/Http/HttpResilienceLogger.cs
src/TermReader.Infrastructure/Metrics/MetricsSummary.cs
src/TermReader.Infrastructure/Metrics/OperationMetrics.cs
src/TermReader.Infrastructure/Metrics/PerformanceMetrics.cs

# Infrastructure - DI (1 file)
src/TermReader.Infrastructure/DependencyInjection.cs

# Migrations (3 files, entire directory)
src/TermReader.Infrastructure/Migrations/20251120001514_InitialCreate.Designer.cs
src/TermReader.Infrastructure/Migrations/20251120001514_InitialCreate.cs
src/TermReader.Infrastructure/Migrations/AppDbContextModelSnapshot.cs

# Tests (11 files)
tests/TermReader.Tests/AudioGeneratorTests.cs
tests/TermReader.Tests/AudioCacheTests.cs
tests/TermReader.Tests/BudgetServiceTests.cs
tests/TermReader.Tests/ChaptersJsonGeneratorTests.cs
tests/TermReader.Tests/RssFeedGeneratorTests.cs
tests/TermReader.Tests/ParallelAudioGeneratorTests.cs
tests/TermReader.Tests/RateLimiterTests.cs
tests/TermReader.Tests/HttpResilienceLoggerTests.cs
tests/TermReader.Tests/ScrapingSessionRepositoryTests.cs
tests/TermReader.Tests/ArticleParserTests.cs
tests/TermReader.Tests/ArticleRepositoryTests.cs
```

**Total: 73 files to delete**

### Summary: Files to Modify

```
# Core modifications
src/TermReader.API/Program.cs                              -- remove all scrape/audio workflows, simplify to browse-only, keep cookie commands
src/TermReader.API/CommandOptions.cs                       -- remove CommandOptions class, keep BrowseOptions only
src/TermReader.Infrastructure/Browser/BrowserDependencyInjection.cs  -- add database + cookie service registration (moved from DependencyInjection.cs)
src/TermReader.Infrastructure/Persistence/AppDbContext.cs  -- remove all DbSets (Article, AudioChapter, ScrapingSession), clean OnModelCreating
src/TermReader.Infrastructure/TermReader.Infrastructure.csproj  -- remove 6 NuGet packages
src/TermReader.API/appsettings.json                        -- remove ElevenLabs, Inworld, Audio, Scraping sections
src/TermReader.Application/Interfaces/Browser/IReadableContentExtractor.cs  -- fix misleading comment

# Docker
Dockerfile                                                  -- remove ffmpeg from apt-get
docker-compose.yml                                          -- remove ElevenLabs env var

# Tests
tests/TermReader.Tests/DependencyInjectionTests.cs          -- update for new DI
tests/TermReader.Tests/CommandOptionsTests.cs                -- update for BrowseOptions only
tests/TermReader.Tests/InterfaceIntegrationTests.cs          -- remove audio interface tests

# Documentation
CLAUDE.md                                                    -- update to reflect browser-only architecture
```

### NuGet Packages to Remove (from Infrastructure .csproj)

```xml
<!-- REMOVE these 6 packages -->
<PackageReference Include="ElevenLabs-DotNet" Version="3.6.0" />
<PackageReference Include="FFMpegCore" Version="5.1.0" />
<PackageReference Include="TagLibSharp" Version="2.3.0" />
<PackageReference Include="z440.atl.core" Version="6.1.0" />
<PackageReference Include="Microsoft.Extensions.Http.Polly" Version="9.0.10" />
<PackageReference Include="Polly" Version="8.5.0" />
```

### Coordination Notes

[REV] Items requiring coordination with other agents:

1. **Collections agent:** Confirm that `Article.cs`, `ArticleRepository.cs`, and `IArticleRepository.cs` can be removed. The collections plan uses `CollectionItem` instead of `Article`. If collections needs any of these, we keep them.

2. **UI renderer agent:** The Serilog console suppression for browse mode is owned by the UI renderer (Problem 1). This plan does not duplicate that fix.

3. **Cookie services:** `CookieImporter`, `CookieManager`, `ICookieManager`, `ICookieEncryptionService`, and `DpapiCookieEncryptionService` are **kept**. They enable authenticated browsing. Their registration moves from `DependencyInjection.cs` to `BrowserDependencyInjection.cs`.
