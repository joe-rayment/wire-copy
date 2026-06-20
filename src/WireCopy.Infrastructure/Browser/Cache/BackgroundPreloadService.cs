// Licensed under the MIT License. See LICENSE in the repository root.

using System.Collections.Concurrent;
using System.Net;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Configuration;
using WireCopy.Infrastructure.Podcast;
using WireCopy.Infrastructure.Podcast.Cache;

namespace WireCopy.Infrastructure.Browser.Cache;

/// <summary>
/// Background service that pre-loads pages the user is likely to navigate to next.
/// Prefetch renders through the browser preload context by default (HTTP is a
/// per-URL fallback), with rate limiting, same-origin filtering, and per-domain
/// circuit breaking. The fetch + cache-warming paths live in the
/// <c>.Fetch.cs</c> partial; this file holds state, scheduling, and the queue.
/// </summary>
internal sealed partial class BackgroundPreloadService : IPreloadService
{
    /// <summary>
    /// Minimum word count for caching paywalled domain responses. Higher than the general
    /// threshold (50) because truncated paywall previews often contain 50-200 words of
    /// teaser content that would pass the basic check.
    /// </summary>
    private const int MinPaywalledWordCount = 500;

    private readonly IPageCache _cache;
    private readonly IIdleDetector _idleDetector;
    private readonly HttpClient _httpClient;
    private readonly CacheConfiguration _config;
    private readonly BrowserConfiguration _browserConfig;
    private readonly IReadableContentExtractor? _contentExtractor;
    private readonly ILinkExtractor? _linkExtractor;
    private readonly IArticleContentCache? _articleContentCache;
    private readonly ICookieManager? _cookieManager;
    private readonly IHttpCookieRefresher? _httpCookieRefresher;
    private readonly IBrowserSession? _browserSession;
    private readonly ILogger<BackgroundPreloadService> _logger;
    private IPage? _backgroundPage;
    private readonly ConcurrentDictionary<string, Task<PageLoadResult>> _inFlight = new();
    private readonly ConcurrentDictionary<string, DateTime> _circuitBrokenDomains = new();
    private readonly ConcurrentDictionary<string, bool> _needsJsDomains = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastRequestByDomain = new();
    private readonly ConcurrentDictionary<string, string> _articleCachedUrls = new();

    /// <summary>
    /// Domains for which a "no auth cookies" skip has already been logged in this
    /// session. Each entry caps logging to one line per domain, preventing the
    /// preloader from spamming the log on every URL on a paywalled section page.
    /// </summary>
    private readonly HashSet<string> _loggedSkippedDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _loggedSkippedLock = new();
    private readonly SemaphoreSlim _queueSignal = new(0, 1);
    private readonly object _queueLock = new();
    private readonly Timer _debounceTimer;
    private List<PreloadItem> _queue = [];
    private volatile bool _paused;

    // workspace-mya7: true while prefetch yields to a HUMAN using the shared
    // browser. Distinct from _paused (app-driven): entered on browser input,
    // exits only after the browser stays quiet for TakeoverResumeIdleSeconds.
    private volatile bool _pausedByUser;

    // workspace-mya7: checkpoint loaded at startup, honored when its page reopens.
    private PreloadCheckpoint? _restoredCheckpoint;
    private volatile bool _eagerMode;
    private volatile bool _disposed;
    private volatile string? _currentlyFetchingUrl;

    // workspace-fh7g: timestamp when the current fetch started, so the detail
    // panel can compute ElapsedOnCurrent and flag stalls (>8s warning,
    // >30s "looks stuck") without the renderer needing its own clock.
    // Updated atomically alongside _currentlyFetchingUrl; long.MinValue is
    // the "not currently fetching" sentinel since DateTime can't be volatile.
    private long _currentlyFetchingStartedAtTicks = long.MinValue;

    // Last typed human-action signal raised by the preloader's HTML check
    // (workspace-0b9s). Surfaced through PreloadProgress so the launcher /
    // link-tree status bar can warn the user before they Enter into a doomed
    // article load. Cleared when the next preload succeeds for a URL on the
    // same origin.
    private HumanActionRequired? _lastBlockedAction;

    // workspace-7xw0: per-URL stage tracking for the detail panel. Updated as
    // PreloadUrlAsync progresses (Fetching → Detecting → ExtractingContent →
    // PersistingCache → Idle on completion). Surfaced via PreloadProgress so
    // the user can answer "is the loader actually doing something?"
    private volatile PreloadStage _currentStage = PreloadStage.Idle;

    // Bounded ring buffer of recent outcomes (workspace-7xw0). Capacity 20
    // covers ~10-15 seconds of preload activity at the typical 1.5s/URL pace.
    // Older entries are evicted FIFO. Guarded by _historyLock for thread
    // safety since AppendHistory may fire from worker threads while
    // GetProgress reads from the UI thread.
#pragma warning disable SA1203 // constant kept next to its associated lock + queue.
    private const int RecentItemsCapacity = 20;
#pragma warning restore SA1203
    private readonly object _historyLock = new();
    private readonly Queue<PreloadHistoryEntry> _recentItems = new(RecentItemsCapacity);

    // Debounce state: stores the latest selection change parameters
    private int _pendingSelectedIndex;
    private IReadOnlyList<LinkNode>? _pendingVisibleNodes;
    private string? _pendingCurrentPageUrl;

    // Debounce state for collection preloading
    private int _pendingCollectionSelectedIndex;
    private IReadOnlyList<string>? _pendingCollectionUrls;
    private bool _pendingIsCollection;

    // Progress tracking: all eligible content URLs from the current page
    private List<string> _allEligibleUrls = [];
    private List<string> _needsJsUrls = [];
    private int _paywalledLinkCount;
    private int _paywalledPreloadCount;
    private int _generalBrowserPreloadCount;
    private volatile bool _hasPaywalledCookies;

    // workspace-lmwm: one-shot guard for the session-start sync of headed
    // browser profile cookies into the persisted store + HTTP jar.
    private volatile bool _profileCookieSyncAttempted;

    public BackgroundPreloadService(
        IPageCache cache,
        IIdleDetector idleDetector,
        HttpClient httpClient,
        CacheConfiguration config,
        ILogger<BackgroundPreloadService> logger,
        IReadableContentExtractor? contentExtractor = null,
        ILinkExtractor? linkExtractor = null,
        IArticleContentCache? articleContentCache = null,
        BrowserConfiguration? browserConfig = null,
        ICookieManager? cookieManager = null,
        IHttpCookieRefresher? httpCookieRefresher = null,
        IBrowserSession? browserSession = null)
    {
        _cache = cache;
        _idleDetector = idleDetector;
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _contentExtractor = contentExtractor;
        _linkExtractor = linkExtractor;
        _articleContentCache = articleContentCache;
        _cookieManager = cookieManager;
        _httpCookieRefresher = httpCookieRefresher;
        _browserSession = browserSession;
        _browserConfig = browserConfig ?? new BrowserConfiguration { PaywalledDomains = [] };
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public event Action? ProgressChanged;

    // Eligibility is gated on the PRELOAD context, not the foreground _context,
    // so default-on prefetch does not require the user to navigate once in the
    // foreground first. The preload flag stays usable while the session is alive,
    // and the preload context itself launches lazily on the first background page.
    private bool CanBrowserPreload => _browserSession?.HasPreloadContext == true && _hasPaywalledCookies;

    private bool CanBrowserPreloadGeneral => _browserSession?.HasPreloadContext == true;

    // Master switch for browser-first prefetch of non-paywalled, non-section URLs.
    // When false the feature reverts to today's HTTP-by-default routing (kill switch).
    private bool BrowserPrefetchEnabled =>
        _config.PreloadUseBrowser && _browserSession?.HasPreloadContext == true;

    public void NotifyPageLoaded(Page page)
    {
        // Page loaded — queue will be rebuilt on next selection change.
        _logger.LogDebug("Page loaded: {Url}", page.Url);

        // workspace-mya7: the interrupted-work checkpoint is honored HERE — the
        // user is back on the page it belongs to, and the queue rebuild derives
        // the still-missing items from cache membership (always fresh, never a
        // stale URL list). The record has served its purpose.
        var restored = _restoredCheckpoint;
        if (restored != null && !string.IsNullOrEmpty(restored.PageUrl)
            && string.Equals(
                UrlNormalizer.Normalize(page.Url),
                UrlNormalizer.Normalize(restored.PageUrl),
                StringComparison.Ordinal))
        {
            _restoredCheckpoint = null;
            PreloadCheckpoint.Delete(PreloadCheckpoint.DefaultPath, _logger);
            _logger.LogInformation(
                "Prefetch checkpoint restored: continuing {Count} interrupted items for {Page}",
                restored.RemainingUrls.Count,
                restored.PageUrl);
        }

        // workspace-m7nc: a successful page load with real content is the
        // strongest signal that any HITL verdict raised by prefetch was either
        // resolved (user solved a captcha in the headed browser) or a false
        // positive (Cloudflare bot-monitor noise on a healthy page). Clear the
        // sticky badge for that origin so prefetch can resume and the status bar
        // doesn't keep showing a stale "action needed" hint.
        //
        // "Real content" = the orchestrator built a non-trivial page. Either:
        //   - readable content was extracted (article-shaped pages), OR
        //   - the link tree was populated with at least one link (link-list pages
        //     like nytimes.com/section/todayspaper where the captcha originally
        //     fired but the user's solve revealed the underlying section page).
        // A blank shell with neither is excluded so a half-loaded captcha page
        // doesn't accidentally clear the verdict.
        if (!string.IsNullOrWhiteSpace(page.Url)
            && (page.ReadableContent != null || page.HasLinks()))
        {
            NotifyChallengeResolved(page.Url);
        }
    }

    /// <summary>
    /// Clears any sticky <see cref="HumanActionRequired"/> verdict and circuit-
    /// breaker entry for the given URL's origin (workspace-m7nc). Called when an
    /// interactive page load on that origin proves the prefetch verdict was either
    /// resolved or a false positive.
    /// </summary>
    public void NotifyChallengeResolved(string url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var origin = UrlNormalizer.GetOrigin(url);
        if (origin == null)
        {
            return;
        }

        var hadAction = _lastBlockedAction != null;
        var hadCircuitBreak = _circuitBrokenDomains.ContainsKey(origin);

        if (!hadAction && !hadCircuitBreak)
        {
            return;
        }

        ClearBlockedActionForOriginIfMatches(url);
        if (_circuitBrokenDomains.TryRemove(origin, out _))
        {
            _logger.LogInformation(
                "NotifyChallengeResolved: cleared circuit-break for {Origin}",
                origin);
            NotifyProgressChanged();
        }
    }

    public void NotifySelectionChanged(int selectedIndex, IReadOnlyList<LinkNode> visibleNodes, string currentPageUrl)
    {
        if (_disposed)
        {
            return;
        }

        // Drop a stale "⏸ verify at X" badge as soon as the user navigates to a
        // page on a different origin (workspace-0b9s QA #2). Otherwise a verdict
        // raised on site A would keep showing while the user reads site B.
        ClearBlockedActionIfDifferentOrigin(currentPageUrl);

        lock (_queueLock)
        {
            _pendingSelectedIndex = selectedIndex;
            _pendingVisibleNodes = visibleNodes;
            _pendingCurrentPageUrl = currentPageUrl;
            _pendingIsCollection = false;
        }

        // Reset the debounce timer (200ms). If the user is scrolling fast,
        // this prevents rebuilding the queue on every keystroke.
        try
        {
            _debounceTimer.Change(200, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Timer was disposed between the _disposed check and the Change call
        }
    }

    public void NotifyCollectionChanged(int selectedIndex, IReadOnlyList<string> urls)
    {
        if (_disposed)
        {
            return;
        }

        // Drop a stale HITL badge if the collection's first item is on a
        // different origin than the last verdict (workspace-0b9s QA #2).
        // Collection URLs are typically same-domain, but reading-list / saved
        // collections can mix origins.
        if (urls is { Count: > 0 })
        {
            ClearBlockedActionIfDifferentOrigin(urls[0]);
        }

        lock (_queueLock)
        {
            _pendingCollectionSelectedIndex = selectedIndex;
            _pendingCollectionUrls = urls;
            _pendingIsCollection = true;
        }

        try
        {
            _debounceTimer.Change(200, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Timer was disposed between the _disposed check and the Change call
        }
    }

    public void ClearQueue()
    {
        lock (_queueLock)
        {
            _queue = [];
            _allEligibleUrls = [];
            _needsJsUrls = [];
        }

        CloseBackgroundPage();
    }

    public void EnableEagerMode()
    {
        _eagerMode = true;
        SignalQueueChanged();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Extension mode (workspace-blg5 / P2.2): the no-second-tab rule forbids background-tab
        // prefetch, and there is no server-side browser to prefetch with. Decision: drop background
        // prefetch entirely in extension mode (on-demand load only — the user's real browser already
        // makes navigation fast). Stay idle so nothing ever launches a server-side Chromium.
        if (string.Equals(
                Environment.GetEnvironmentVariable("WIRECOPY_BROWSER"), "extension", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Background pre-load disabled in extension mode (on-demand load only)");
            return;
        }

        _logger.LogInformation("Background pre-load service started");

        // workspace-mya7: an interrupted-work checkpoint BINDS TO ITS PAGE — it is
        // honored when the user reopens that page (NotifyPageLoaded), where the
        // normal queue rebuild derives the remaining work from cache membership.
        // Never pre-seed the queue from stored URLs: a stale checkpoint (dead host,
        // rotated session) would starve fresh work behind connection timeouts.
        _restoredCheckpoint = PreloadCheckpoint.Load(PreloadCheckpoint.DefaultPath, _logger);
        if (_restoredCheckpoint != null)
        {
            _logger.LogInformation(
                "Prefetch checkpoint found: {Count} items pending for {Page} (resumes when that page opens)",
                _restoredCheckpoint.RemainingUrls.Count,
                _restoredCheckpoint.PageUrl);
        }

        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            // workspace-mya7: while user-paused, wait for the browser to go quiet,
            // then resume from where we left off.
            if (_pausedByUser)
            {
                if (await CanResumeFromUserPauseAsync().ConfigureAwait(false))
                {
                    _pausedByUser = false;
                    _logger.LogInformation("Browser is quiet again — prefetch resuming from checkpoint");
                    NotifyProgressChanged();
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            try
            {
                // Wait once for user to become idle before starting a batch (skip in eager mode)
                if (!_eagerMode)
                {
                    await _idleDetector.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
                }

                // Process the entire queue while user stays idle (or eager mode is active)
                var processedAny = false;
                while (!cancellationToken.IsCancellationRequested && !_disposed && !_paused && !_pausedByUser && (_eagerMode || _idleDetector.IsIdle))
                {
                    // workspace-mya7: the user always wins — input anywhere in the
                    // shared browser pauses prefetch at an item boundary and
                    // checkpoints what remains.
                    if (await IsBrowserUserActiveAsync().ConfigureAwait(false))
                    {
                        EnterUserPause();
                        break;
                    }

                    var item = DequeueNext();
                    if (item == null)
                    {
                        break;
                    }

                    processedAny = true;
                    if (item.NeedsBrowser)
                    {
                        await BrowserPreloadUrlAsync(item.Url, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await PreloadUrlAsync(item.Url, cancellationToken).ConfigureAwait(false);
                    }

                    // Rate limit between pre-loads. Browser preloads already take several
                    // seconds per page load, so use a shorter delay (3s) than HTTP preloads
                    // which are near-instant and need longer delays to avoid bot detection.
                    var delayMs = item.NeedsBrowser ? 3000 : GetAdaptiveDelay(item.Url);
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
                }

                // Only reset eager mode if we actually processed items.
                // If queue was empty (e.g., debounce hasn't fired yet), keep eager for the next loop.
                if (processedAny)
                {
                    _eagerMode = false;
                }

                // workspace-1rfd: prefetch went idle — drop the in-page badge so the
                // tab doesn't claim activity it no longer has.
                await ClearPrefetchBadgeAsync().ConfigureAwait(false);

                // Clear the "currently fetching" indicator when batch ends
                _currentlyFetchingUrl = null;
                Interlocked.Exchange(ref _currentlyFetchingStartedAtTicks, long.MinValue);

                // If items remain uncached, trigger a queue rebuild so the loop continues.
                // Without this, the loop stalls on WaitForSignalAsync waiting for a user
                // interaction signal that never comes when the user is idle on the link list.
                if (processedAny && HasUncachedEligibleUrls())
                {
                    RequestQueueRebuild();
                }
                else if (processedAny)
                {
                    // Queue fully drained — the interrupted-work record is obsolete.
                    PreloadCheckpoint.Delete(PreloadCheckpoint.DefaultPath, _logger);
                }

                // Either queue is empty, user is active, or paused — wait for signal before next batch
                await WaitForSignalAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in pre-load loop");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Background pre-load service stopped");
    }

    public void Pause()
    {
        _paused = true;
        _logger.LogDebug("Pre-loading paused");
    }

    public void Resume()
    {
        _paused = false;
        _logger.LogDebug("Pre-loading resumed");
        SignalQueueChanged();
    }

    /// <inheritdoc />
    public async Task<Application.DTOs.Scheduling.RenderedLoad> LoadRenderedHtmlAsync(
        string url, CancellationToken cancellationToken = default)
    {
        if (_browserSession == null)
        {
            return new Application.DTOs.Scheduling.RenderedLoad
            {
                Outcome = Application.DTOs.Scheduling.LoadOutcome.LoadFailed,
                FinalUrl = url,
            };
        }

        // workspace-frpl.6: quiesce the preload loop and run on an ISOLATED page
        // in the preload context (NewPageAsync), so a scheduled load and any
        // in-flight preload never share a page. The scheduler (B6) also gates
        // generation, so this stays a single concurrent heavy navigation.
        var wasPaused = _paused;
        Pause();
        Microsoft.Playwright.IPage? page = null;
        try
        {
            page = await _browserSession.CreateBackgroundPageAsync().ConfigureAwait(false);
            if (page == null)
            {
                return new Application.DTOs.Scheduling.RenderedLoad
                {
                    Outcome = Application.DTOs.Scheduling.LoadOutcome.LoadFailed,
                    FinalUrl = url,
                };
            }

            try
            {
                await _browserSession.MinimizeWindowAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Minimize before scheduled load failed (non-fatal)");
            }

            await page.GotoAsync(url, new Microsoft.Playwright.PageGotoOptions
            {
                Timeout = 15000,
                WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
            }).ConfigureAwait(false);

            try
            {
                await _browserSession.MinimizeWindowAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Minimize after scheduled load failed (non-fatal)");
            }

            try
            {
                await page.WaitForFunctionAsync(
                    @"() => {
                        if (document.querySelector('[role=""main""] a, main a, article a')) return true;
                        return document.body && document.body.innerHTML.length > 5000;
                    }",
                    null,
                    new Microsoft.Playwright.PageWaitForFunctionOptions { Timeout = 4000 }).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("Scheduled load render wait timed out for {Url}", url);
            }
            catch (Microsoft.Playwright.PlaywrightException)
            {
                _logger.LogDebug("Scheduled load render wait failed for {Url}", url);
            }

            await PageLoader.DismissOverlaysAsync(page, _logger).ConfigureAwait(false);

            var html = await page.ContentAsync().ConfigureAwait(false);
            var finalUrl = page.Url;

            if (HumanActionDetector.Detect(html, finalUrl, statusCode: 0) != null || PageLoader.IsBotChallengePage(html))
            {
                return new Application.DTOs.Scheduling.RenderedLoad
                {
                    Outcome = Application.DTOs.Scheduling.LoadOutcome.Blocked,
                    Html = html,
                    FinalUrl = finalUrl,
                };
            }

            return new Application.DTOs.Scheduling.RenderedLoad
            {
                Outcome = Application.DTOs.Scheduling.LoadOutcome.Ok,
                Html = html,
                FinalUrl = finalUrl,
            };
        }
        catch (OperationCanceledException)
        {
            return new Application.DTOs.Scheduling.RenderedLoad
            {
                Outcome = Application.DTOs.Scheduling.LoadOutcome.LoadFailed,
                FinalUrl = url,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled rendered load failed for {Url}", url);
            return new Application.DTOs.Scheduling.RenderedLoad
            {
                Outcome = Application.DTOs.Scheduling.LoadOutcome.LoadFailed,
                FinalUrl = url,
            };
        }
        finally
        {
            if (page != null)
            {
                try
                {
                    await page.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Closing scheduled-load page failed (non-fatal)");
                }
            }

            if (!wasPaused)
            {
                Resume();
            }
        }
    }

    public PreloadProgress GetProgress()
    {
        List<string> eligible;
        List<string> needsJs;
        List<string> upcoming;

        lock (_queueLock)
        {
            eligible = _allEligibleUrls;
            needsJs = _needsJsUrls;

            // workspace-7xw0: snapshot the next 10 queued URLs for the detail
            // panel'\''s "up next" list. Taking the snapshot under _queueLock
            // guarantees a stable read even when the worker enqueues
            // concurrently.
            upcoming = _queue.Take(10).Select(item => item.Url).ToList();
        }

        var cachedCount = eligible.Count(url => _cache.Contains(url) || IsInArticleCache(url));
        var hasQueuedWork = _queue.Count > 0 || !_inFlight.IsEmpty;
        var pausedByUser = _pausedByUser;

        // workspace-7xw0: snapshot history under _historyLock so a concurrent
        // AppendHistory cannot tear the read.
        List<PreloadHistoryEntry> recent;
        lock (_historyLock)
        {
            recent = _recentItems.ToList();
        }

        // workspace-fh7g: compute elapsed time on the in-flight URL. The
        // worker is single-threaded so the URL+tick pair is consistent at the
        // write site (both set together in PreloadUrlAsync / Browser preload),
        // but a worker rotation can land between the URL read and the tick
        // read below. Worst case: one ProgressChanged frame shows an elapsed
        // value computed against the next URL using the prior URL's start
        // tick. The panel refreshes within ~100ms via the next ProgressChanged
        // tick, so the stale value clears immediately — acceptable noise.
        TimeSpan? elapsedOnCurrent = null;
        var fetchingUrlSnapshot = _currentlyFetchingUrl;
        var startedAtTicks = Interlocked.Read(ref _currentlyFetchingStartedAtTicks);
        if (fetchingUrlSnapshot != null && startedAtTicks != long.MinValue)
        {
            var elapsedTicks = DateTime.UtcNow.Ticks - startedAtTicks;
            if (elapsedTicks > 0)
            {
                elapsedOnCurrent = TimeSpan.FromTicks(elapsedTicks);
            }
        }

        return new PreloadProgress
        {
            TotalCacheableLinks = eligible.Count,
            CachedCount = cachedCount,
            NeedsBrowserCount = needsJs.Count,
            PaywalledLinkCount = _paywalledLinkCount,
            IsActivelyFetching = hasQueuedWork && !_paused && !pausedByUser,
            PausedByUser = pausedByUser,
            CurrentlyFetchingUrl = fetchingUrlSnapshot,
            BlockedAction = _lastBlockedAction,
            CurrentStage = _currentStage,
            UpcomingUrls = upcoming,
            RecentItems = recent,
            ElapsedOnCurrent = elapsedOnCurrent,
        };
    }

#pragma warning disable SA1202 // helper kept adjacent to its sole caller GetProgress (workspace-7xw0).
    /// <summary>
    /// Appends an entry to the bounded history ring (workspace-7xw0). Called
    /// from the preload worker as URLs complete. Capped at
    /// <see cref="RecentItemsCapacity"/>; oldest entry evicted FIFO.
    /// </summary>
    private void AppendHistory(string url, PreloadOutcome outcome, long elapsedMs, string? reason)
    {
        var entry = new PreloadHistoryEntry
        {
            Url = url,
            Outcome = outcome,
            ElapsedMs = elapsedMs,
            Reason = reason,
        };

        lock (_historyLock)
        {
            if (_recentItems.Count >= RecentItemsCapacity)
            {
                _recentItems.Dequeue();
            }

            _recentItems.Enqueue(entry);
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetArticleCachedUrls()
    {
        return _articleCachedUrls.Values.ToHashSet();
    }

    /// <inheritdoc />
    public async Task<PageLoadResult?> WaitForInFlightAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var normalizedUrl = UrlNormalizer.Normalize(url);

        if (!_inFlight.TryGetValue(normalizedUrl, out var inFlightTask))
        {
            return null;
        }

        _logger.LogDebug("Waiting for in-flight preload: {Url} (timeout: {Timeout}s)", url, timeout.TotalSeconds);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var result = await inFlightTask.WaitAsync(cts.Token).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Timed out waiting for in-flight preload: {Url}", url);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "In-flight preload failed for {Url}, falling back to normal fetch", url);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseBackgroundPage();
        _debounceTimer.Dispose();
        SignalQueueChanged();
        _queueSignal.Dispose();
    }

    /// <inheritdoc />
    public bool IsDomainNeedsJs(string url)
    {
        var origin = UrlNormalizer.GetOrigin(url);
        return origin != null && _needsJsDomains.ContainsKey(origin);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetMissingPaywalledCookieDomains(string? currentPageUrl)
    {
        if (string.IsNullOrEmpty(currentPageUrl))
        {
            return Array.Empty<string>();
        }

        if (_hasPaywalledCookies)
        {
            return Array.Empty<string>();
        }

        var domain = ExtractPaywalledDomainFromUrl(currentPageUrl);
        if (string.IsNullOrEmpty(domain))
        {
            return Array.Empty<string>();
        }

        return new[] { domain };
    }

    /// <summary>
    /// Backwards-compatible wrapper around <see cref="HumanActionDetector.IsBotDetectionResponse"/>.
    /// Kept on this type for tests that still call it directly. New code should use
    /// <see cref="HumanActionDetector.Detect"/> for a typed verdict.
    /// </summary>
    internal static bool IsBotDetectionResponse(string html)
        => HumanActionDetector.IsBotDetectionResponse(html);

    /// <summary>
    /// Computes a priority score for a preload item. Lower score = higher priority.
    /// The score is based on distance from the cursor (selected index).
    /// </summary>
    internal static int ComputePriorityScore(int listIndex, int selectedIndex)
    {
        return Math.Abs(listIndex - selectedIndex);
    }

    /// <summary>
    /// Detects if the server redirected to a different page by comparing the normalized
    /// paths of the request URL and final URL. Ignores fragment/query differences.
    /// </summary>
    internal static bool IsRedirectedUrl(string requestUrl, string finalUrl)
    {
        if (string.IsNullOrEmpty(finalUrl))
        {
            return false;
        }

        var normalizedRequest = UrlNormalizer.Normalize(requestUrl);
        var normalizedFinal = UrlNormalizer.Normalize(finalUrl);

        return !string.Equals(normalizedRequest, normalizedFinal, StringComparison.OrdinalIgnoreCase);
    }

    private static PageMetadata ExtractMetadata(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var title = ExtractMetaContent(doc, "og:title");
        if (string.IsNullOrWhiteSpace(title))
        {
            var titleNode = doc.DocumentNode.SelectSingleNode("//title");
            title = titleNode?.InnerText.Trim();
            if (title != null)
            {
                title = WebUtility.HtmlDecode(title);
            }
        }

        return new PageMetadata
        {
            Title = title ?? "Untitled",
            Description = ExtractMetaContent(doc, "description") ?? ExtractMetaContent(doc, "og:description")
        };
    }

    private static string? ExtractMetaContent(HtmlDocument doc, string name)
    {
        return HtmlMetadataExtractor.ExtractMetaContent(doc, name);
    }

    private void CloseBackgroundPage()
    {
        var page = _backgroundPage;
        _backgroundPage = null;
        if (page != null && _browserSession != null)
        {
            try
            {
                _browserSession.CloseBackgroundPageAsync(page).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing background page during cleanup");
            }
        }
    }

    // Synchronous variant for timer-callback callers (OnDebounceElapsed) which run
    // on the thread pool with no sync context — no deadlock risk.
    private void RefreshPaywalledCookieState() =>
        RefreshPaywalledCookieStateAsync().GetAwaiter().GetResult();

    private async Task RefreshPaywalledCookieStateAsync()
    {
        if (_cookieManager == null)
        {
            _hasPaywalledCookies = false;
            return;
        }

        try
        {
            var info = await _cookieManager.GetCookieInfoAsync().ConfigureAwait(false);
            var hadCookies = _hasPaywalledCookies;
            var hasStoredCookies = info is { Exists: true, IsExpired: false };

            // workspace-lmwm: before declaring "no auth cookies", consult the
            // headed browser PROFILE — a logged-in user's cookies live there and
            // previously only reached the persisted store / HTTP jar after a
            // manual interactive refresh, so the status bar showed a false
            // "🍪✗ Shift+I:login" badge to users who were already logged in.
            if (!hasStoredCookies)
            {
                hasStoredCookies = await TryImportProfileCookiesAsync().ConfigureAwait(false);
            }

            _hasPaywalledCookies = hasStoredCookies;

            // When cookies become available, refresh the HttpClient's CookieContainer
            // and clear paywalled domains from _needsJsDomains so they can be re-queued
            if (_hasPaywalledCookies && !hadCookies)
            {
                if (_httpCookieRefresher != null)
                {
                    await _httpCookieRefresher.RefreshAsync().ConfigureAwait(false);
                }

                _logger.LogInformation("Paywalled cookies detected, refreshed HTTP cookie container");

                foreach (var origin in _needsJsDomains.Keys.Where(IsPaywalledDomain).ToList())
                {
                    _needsJsDomains.TryRemove(origin, out _);
                }

                // Clear the per-session log throttle so a future cookie expiry will
                // surface a fresh "no auth cookies" log line per domain.
                lock (_loggedSkippedLock)
                {
                    _loggedSkippedDomains.Clear();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh paywalled cookie state; assuming none");
            _hasPaywalledCookies = false;
        }
    }

    /// <summary>
    /// One-shot sync of headed-browser-profile cookies into the persisted store
    /// and HTTP jar (workspace-lmwm). Runs at most once per session, and only
    /// once a browser context actually exists (lazy launch means it may not at
    /// first call — later refreshes retry until one is up). Returns true when
    /// the import produced valid auth cookies.
    /// </summary>
    private async Task<bool> TryImportProfileCookiesAsync()
    {
        if (_profileCookieSyncAttempted
            || _cookieManager == null
            || _browserSession is not { HasBrowserContext: true })
        {
            return false;
        }

        _profileCookieSyncAttempted = true;

        try
        {
            var aggregated = new Dictionary<(string Name, string Domain, string Path), StoredCookie>();
            foreach (var domain in _browserConfig.PaywalledDomains)
            {
                IReadOnlyList<StoredCookie> domainCookies;
                try
                {
                    domainCookies = await _browserSession.GetCookiesForUrlAsync($"https://{domain}/").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Profile cookie sync: failed to read cookies for {Domain}", domain);
                    continue;
                }

                foreach (var c in domainCookies)
                {
                    aggregated[(c.Name, c.Domain, c.Path)] = c;
                }
            }

            if (aggregated.Count == 0)
            {
                return false;
            }

            await _cookieManager.SaveCookiesAsync(aggregated.Values.ToList()).ConfigureAwait(false);
            if (_httpCookieRefresher != null)
            {
                await _httpCookieRefresher.RefreshAsync().ConfigureAwait(false);
            }

            var info = await _cookieManager.GetCookieInfoAsync().ConfigureAwait(false);
            var ok = info is { Exists: true, IsExpired: false };
            _logger.LogInformation(
                "Profile cookie sync: imported {Count} cookies from the headed browser profile (auth cookies valid: {Ok})",
                aggregated.Count,
                ok);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Profile cookie sync failed (non-fatal)");
            return false;
        }
    }

    private bool IsPaywalledDomain(string url) => _browserConfig.IsPaywalledDomain(url);

    /// <summary>
    /// Logs a single "paywalled article skipped: no auth cookies" line per domain
    /// per session. The throttle clears when cookies become available so the next
    /// expiry will surface a fresh log line.
    /// </summary>
    private void LogPaywalledSkipOnce(string url)
    {
        var domain = ExtractPaywalledDomainFromUrl(url);
        if (string.IsNullOrEmpty(domain))
        {
            return;
        }

        lock (_loggedSkippedLock)
        {
            if (!_loggedSkippedDomains.Add(domain))
            {
                return;
            }
        }

        _logger.LogInformation(
            "Paywalled article skipped: no auth cookies for {Domain}. Run :cookies import",
            domain);
    }

    /// <summary>
    /// Returns the configured paywalled domain that matches the given URL's host
    /// (e.g., "www.nytimes.com" -> "nytimes.com"). Returns empty when the URL is
    /// not on a configured paywalled domain.
    /// </summary>
    private string ExtractPaywalledDomainFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        try
        {
            var host = new Uri(url).Host;
            return _browserConfig.PaywalledDomains.FirstOrDefault(d =>
                host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentNullException)
        {
            // Malformed or null URL — no domain to extract. Narrowed from a bare catch
            // (workspace-3v8z): the only throwers in the try are new Uri(...) (the LINQ
            // predicate over the config array is pure), so any OTHER exception is
            // unexpected and should surface rather than be silently swallowed.
            return string.Empty;
        }
    }

    private void TryAddEligibleUrl(
        string url,
        int listIndex,
        List<string> needsJs,
        List<PreloadItem> items)
    {
        if (IsDomainNeedsJs(url))
        {
            if (CanBrowserPreloadGeneral && !IsUrlCached(url))
            {
                items.Add(new PreloadItem(url, listIndex, NeedsBrowser: true));
            }
            else
            {
                needsJs.Add(url);
            }

            return;
        }

        if (IsUrlCached(url))
        {
            return;
        }

        if (IsDomainCircuitBroken(url))
        {
            needsJs.Add(url);
            return;
        }

        // Browser-first by default: route the URL that would otherwise fall through
        // to a plain HTTP preload through the persistent preload context instead.
        // The dispatch loop branches on NeedsBrowser and the per-URL browser nav
        // falls back to HTTP on failure (BrowserPreloadUrlAsync).
        // Section/link-list pages stay on the fast HTTP path: the browser nav is
        // slower and unnecessary for free section pages, and they change frequently.
        if (BrowserPrefetchEnabled && !PageClassifier.IsSectionUrlPattern(url))
        {
            items.Add(new PreloadItem(url, listIndex, NeedsBrowser: true));
            return;
        }

        items.Add(new PreloadItem(url, listIndex));
    }

    private bool IsUrlCached(string url) => _cache.Contains(url) || IsInArticleCache(url);

    private bool HasUncachedEligibleUrls()
    {
        List<string> eligible;
        lock (_queueLock)
        {
            eligible = _allEligibleUrls;
        }

        return eligible.Any(url => !IsUrlCached(url));
    }

    private bool IsInArticleCache(string url) =>
        _articleContentCache != null && _articleCachedUrls.ContainsKey(UrlNormalizer.Normalize(url));

    private bool IsDomainCircuitBroken(string url)
    {
        var origin = UrlNormalizer.GetOrigin(url);
        if (origin == null || !_circuitBrokenDomains.TryGetValue(origin, out var brokenAt))
        {
            return false;
        }

        if (DateTime.UtcNow - brokenAt < TimeSpan.FromSeconds(_config.CircuitBreakerCooldownSeconds))
        {
            return true;
        }

        // Cooldown elapsed — allow retry
        _circuitBrokenDomains.TryRemove(origin, out _);
        return false;
    }

    private void UpdateProgressTracking(List<string> allEligible, List<string> needsJs, int paywalledCount = 0)
    {
        lock (_queueLock)
        {
            _allEligibleUrls = allEligible;
            _needsJsUrls = needsJs;
            _paywalledLinkCount = paywalledCount;
        }

        // Notify UI so status bar updates (important for paywalled domains
        // where no fetches happen and ProgressChanged wouldn't fire otherwise)
        try
        {
            ProgressChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ProgressChanged handler error in UpdateProgressTracking");
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        bool isCollection;
        int selectedIndex;
        IReadOnlyList<LinkNode>? visibleNodes;
        string? currentPageUrl;
        int collectionSelectedIndex;
        IReadOnlyList<string>? collectionUrls;

        lock (_queueLock)
        {
            isCollection = _pendingIsCollection;
            selectedIndex = _pendingSelectedIndex;
            visibleNodes = _pendingVisibleNodes;
            currentPageUrl = _pendingCurrentPageUrl;
            collectionSelectedIndex = _pendingCollectionSelectedIndex;
            collectionUrls = _pendingCollectionUrls;
        }

        if (isCollection)
        {
            if (collectionUrls == null)
            {
                return;
            }

            RefreshPaywalledCookieState();
            var newQueue = BuildCollectionQueue(collectionSelectedIndex, collectionUrls);

            lock (_queueLock)
            {
                _queue = newQueue;
            }

            SignalQueueChanged();
        }
        else
        {
            if (visibleNodes == null || currentPageUrl == null)
            {
                return;
            }

            RefreshPaywalledCookieState();
            var newQueue = BuildQueue(selectedIndex, visibleNodes, currentPageUrl);

            lock (_queueLock)
            {
                _queue = newQueue;
            }

            SignalQueueChanged();
        }
    }

    private void RequestQueueRebuild()
    {
        if (_disposed)
        {
            return;
        }

        bool hasPending;
        lock (_queueLock)
        {
            hasPending = _pendingVisibleNodes != null || _pendingCollectionUrls != null;
        }

        if (!hasPending)
        {
            return;
        }

        try
        {
            _debounceTimer.Change(50, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Timer disposed
        }
    }

    private void SignalQueueChanged()
    {
        try
        {
            _queueSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled — no action needed
        }
        catch (ObjectDisposedException)
        {
            // Disposed during shutdown — no action needed
        }
    }

    private async Task WaitForSignalAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _queueSignal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <summary>
    /// True when a human used the shared browser within the configured input
    /// window (workspace-mya7). Conservative on errors: never blocks prefetch.
    /// </summary>
    /// <summary>workspace-1rfd: (cached, total) snapshot for the prefetch-tab badge.</summary>
    private (int Done, int Total) CachedProgressSnapshot()
    {
        List<string> eligible;
        lock (_queueLock)
        {
            eligible = _allEligibleUrls;
        }

        var done = eligible.Count(url => _cache.Contains(url) || IsInArticleCache(url));
        return (done, eligible.Count);
    }

    private async Task ClearPrefetchBadgeAsync()
    {
        var page = _backgroundPage;
        if (page == null)
        {
            return;
        }

        try
        {
            await page.EvaluateAsync<string>(PrefetchBadgeScript.Clear).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Prefetch badge clear failed (non-fatal)");
        }
    }

    private async Task<bool> IsBrowserUserActiveAsync()
    {
        if (_browserSession == null)
        {
            return false;
        }

        try
        {
            var last = await _browserSession.ReadLastUserInputAsync().ConfigureAwait(false);
            var window = TimeSpan.FromSeconds(_browserConfig?.TakeoverInputWindowSeconds ?? 10);
            return BrowserOwnershipArbiter.IsUserActive(last, DateTimeOffset.UtcNow, window);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Takeover probe failed (non-fatal)");
            return false;
        }
    }

    private async Task<bool> CanResumeFromUserPauseAsync()
    {
        if (_browserSession == null)
        {
            return true;
        }

        try
        {
            var last = await _browserSession.ReadLastUserInputAsync().ConfigureAwait(false);
            var quiet = TimeSpan.FromSeconds(_browserConfig?.TakeoverResumeIdleSeconds ?? 25);
            return BrowserOwnershipArbiter.CanResume(last, DateTimeOffset.UtcNow, quiet);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Takeover resume probe failed (non-fatal)");
            return true;
        }
    }

    /// <summary>
    /// Pause for a human takeover: checkpoint the remaining queue (so 'where it
    /// left off' survives even a restart) and surface the state in the UI.
    /// </summary>
    private void EnterUserPause()
    {
        _pausedByUser = true;
        List<string> remaining;
        string? pageUrl;
        lock (_queueLock)
        {
            remaining = _queue.Select(i => i.Url).ToList();
            pageUrl = _pendingCurrentPageUrl;
        }

        PreloadCheckpoint.Save(PreloadCheckpoint.DefaultPath, pageUrl, remaining, _logger);
        _logger.LogInformation(
            "Prefetch paused — you're using the browser ({Count} items remaining)",
            remaining.Count);
        NotifyProgressChanged();
    }

    private PreloadItem? DequeueNext()
    {
        lock (_queueLock)
        {
            while (_queue.Count > 0)
            {
                var item = _queue[0];
                _queue.RemoveAt(0);

                // Re-check: skip if now cached or in-flight
                if (_cache.Contains(item.Url) || _inFlight.ContainsKey(UrlNormalizer.Normalize(item.Url)))
                {
                    continue;
                }

                return item;
            }

            return null;
        }
    }

#pragma warning disable SA1202 // Internal test seams kept adjacent to the private code they exercise (workspace-0b9s QA #2).
    /// <summary>
    /// Test seam (workspace-0b9s QA #2): lets unit tests prime the
    /// "last blocked action" field so the clearing-on-success and
    /// clearing-on-origin-change behaviour can be exercised without
    /// having to stand up a full HTTP test server.
    /// </summary>
    internal void SetBlockedActionForTesting(HumanActionRequired? action)
    {
        _lastBlockedAction = action;
        NotifyProgressChanged();
    }

    /// <summary>
    /// Test seam (workspace-0b9s QA #2): exposes the success-path clear so
    /// tests can verify the behaviour without driving a real preload.
    /// </summary>
    internal void ClearBlockedActionForOriginIfMatchesForTesting(string url)
        => ClearBlockedActionForOriginIfMatches(url);

    /// <summary>
    /// Test seam (workspace-m7nc): lets unit tests prime the circuit-breaker
    /// entry for an origin without having to drive a real preload failure.
    /// </summary>
    internal void SetCircuitBrokenForTesting(string url)
    {
        var origin = UrlNormalizer.GetOrigin(url);
        if (origin != null)
        {
            _circuitBrokenDomains[origin] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Test seam (workspace-m7nc): exposes the circuit-breaker state so tests
    /// can verify <see cref="NotifyChallengeResolved"/> lifts the break.
    /// </summary>
    internal bool IsCircuitBrokenForTesting(string url)
    {
        var origin = UrlNormalizer.GetOrigin(url);
        return origin != null && _circuitBrokenDomains.ContainsKey(origin);
    }

    /// <summary>
    /// Test seam (workspace-7xw0): lets unit tests prime the per-URL stage
    /// without standing up a real preload.
    /// </summary>
    internal void SetStageForTesting(PreloadStage stage) => _currentStage = stage;

    /// <summary>
    /// Test seam (workspace-fh7g): primes the in-flight URL + start tick so
    /// tests can verify ElapsedOnCurrent populates correctly without
    /// standing up a real network fetch. Pass <c>startedAt = null</c> to
    /// simulate "no fetch in flight" (clears both fields atomically).
    /// </summary>
    internal void SetCurrentlyFetchingForTesting(string? url, DateTime? startedAt)
    {
        _currentlyFetchingUrl = url;
        Interlocked.Exchange(
            ref _currentlyFetchingStartedAtTicks,
            startedAt.HasValue ? startedAt.Value.Ticks : long.MinValue);
    }

    /// <summary>
    /// Test seam (workspace-7xw0): exposes <see cref="AppendHistory"/> so
    /// tests can drive the history ring buffer directly without driving the
    /// full preload pipeline.
    /// </summary>
    internal void AppendHistoryForTesting(string url, PreloadOutcome outcome, long elapsedMs, string? reason = null)
        => AppendHistory(url, outcome, elapsedMs, reason);

    /// <summary>
    /// Test seam (workspace-7xw0): replaces the internal <c>_queue</c> with
    /// a synthetic list so tests can assert the <c>UpcomingUrls</c> snapshot
    /// behaviour without running through the dedupe / eligibility pipeline.
    /// </summary>
    internal void SetQueueForTesting(IEnumerable<string> urls)
    {
        lock (_queueLock)
        {
            _queue = urls.Select(url => new PreloadItem(url, 0)).ToList();
        }
    }

    /// <summary>
    /// Test seam (workspace-7xw0): exposes <see cref="PreloadUrlAsync"/> so
    /// unit tests can drive the full per-URL pipeline (stage transitions +
    /// history append + outcome classification) with mocked HttpClient and
    /// IPageCache, without standing up the background worker loop.
    /// </summary>
    internal Task PreloadUrlAsyncForTesting(string url, CancellationToken cancellationToken)
        => PreloadUrlAsync(url, cancellationToken);
#pragma warning restore SA1202

    private static string ExtractHostFromOrigin(string origin)
    {
        // origin format from UrlNormalizer.GetOrigin: "scheme://host[:port]"
        // Strip scheme prefix and any :port suffix to get bare host.
        var schemeSep = origin.IndexOf("://", StringComparison.Ordinal);
        var hostStart = schemeSep >= 0 ? schemeSep + 3 : 0;
        var portSep = origin.IndexOf(':', hostStart);
        return portSep >= 0 ? origin[hostStart..portSep] : origin[hostStart..];
    }

    private void NotifyProgressChanged()
    {
        try
        {
            ProgressChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ProgressChanged handler error");
        }
    }

    /// <summary>
    /// Clears <see cref="_lastBlockedAction"/> when the just-completed preload
    /// succeeds on the same origin that previously raised the verdict. Without
    /// this, once any URL trips a HITL detection during preload the badge
    /// sticks for the entire app session even after the user solves the gate
    /// and other URLs on that domain start succeeding (workspace-0b9s QA #2).
    /// </summary>
    private void ClearBlockedActionForOriginIfMatches(string url)
    {
        var current = _lastBlockedAction;
        if (current == null)
        {
            return;
        }

        var origin = UrlNormalizer.GetOrigin(url);
        if (origin == null)
        {
            return;
        }

        // Compare by host (Domain). Origin is "scheme://host[:port]"; current.Domain
        // is the bare host. Strip the scheme/port for the comparison so e.g. an
        // origin of "https://www.nytimes.com" matches a stored Domain of
        // "www.nytimes.com".
        var host = ExtractHostFromOrigin(origin);
        if (string.Equals(host, current.Domain, StringComparison.OrdinalIgnoreCase))
        {
            _lastBlockedAction = null;
            NotifyProgressChanged();
        }
    }

    /// <summary>
    /// Clears <see cref="_lastBlockedAction"/> when the user moves to a page
    /// whose origin differs from the one that raised the last HITL verdict.
    /// Prevents a stale badge for one site from leaking into another's status
    /// bar (workspace-0b9s QA #2).
    /// </summary>
    private void ClearBlockedActionIfDifferentOrigin(string? currentPageUrl)
    {
        var current = _lastBlockedAction;
        if (current == null || string.IsNullOrWhiteSpace(currentPageUrl))
        {
            return;
        }

        var origin = UrlNormalizer.GetOrigin(currentPageUrl);
        if (origin == null)
        {
            return;
        }

        var host = ExtractHostFromOrigin(origin);
        if (!string.Equals(host, current.Domain, StringComparison.OrdinalIgnoreCase))
        {
            _lastBlockedAction = null;
            NotifyProgressChanged();
        }
    }

    internal sealed record PreloadItem(string Url, int ListIndex, bool NeedsBrowser = false);
}
