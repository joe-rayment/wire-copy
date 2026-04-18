// Educational and personal use only.

using System.Collections.Concurrent;
using System.Net;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Configuration;
using TermReader.Infrastructure.Podcast;
using TermReader.Infrastructure.Podcast.Cache;

namespace TermReader.Infrastructure.Browser.Cache;

/// <summary>
/// Background service that pre-loads pages the user is likely to navigate to next.
/// Uses HTTP-only fetching (never browser), rate limiting, same-origin filtering,
/// and per-domain circuit breaking.
/// </summary>
internal sealed class BackgroundPreloadService : IPreloadService
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
    private readonly string[] _paywalledDomains;
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
    private readonly SemaphoreSlim _queueSignal = new(0, 1);
    private readonly object _queueLock = new();
    private readonly Timer _debounceTimer;
    private List<PreloadItem> _queue = [];
    private volatile bool _paused;
    private volatile bool _eagerMode;
    private volatile bool _disposed;
    private volatile string? _currentlyFetchingUrl;

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
    private volatile bool _hasPaywalledCookies;

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
        _paywalledDomains = browserConfig?.PaywalledDomains ?? [];
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public event Action? ProgressChanged;

    private bool CanBrowserPreload => _browserSession?.HasBrowserContext == true && _hasPaywalledCookies;

    public void NotifyPageLoaded(Page page)
    {
        // Page loaded — queue will be rebuilt on next selection change
        _logger.LogDebug("Page loaded: {Url}", page.Url);
    }

    public void NotifySelectionChanged(int selectedIndex, IReadOnlyList<LinkNode> visibleNodes, string currentPageUrl)
    {
        if (_disposed)
        {
            return;
        }

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
        _logger.LogInformation("Background pre-load service started");

        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            try
            {
                // Wait once for user to become idle before starting a batch (skip in eager mode)
                if (!_eagerMode)
                {
                    await _idleDetector.WaitForIdleAsync(cancellationToken);
                }

                // Process the entire queue while user stays idle (or eager mode is active)
                var processedAny = false;
                while (!cancellationToken.IsCancellationRequested && !_disposed && !_paused && (_eagerMode || _idleDetector.IsIdle))
                {
                    var item = DequeueNext();
                    if (item == null)
                    {
                        break;
                    }

                    processedAny = true;
                    if (item.NeedsBrowser)
                    {
                        await BrowserPreloadUrlAsync(item.Url, cancellationToken);
                    }
                    else
                    {
                        await PreloadUrlAsync(item.Url, cancellationToken);
                    }

                    // Rate limit between pre-loads. Browser preloads already take several
                    // seconds per page load, so use a shorter delay (3s) than HTTP preloads
                    // which are near-instant and need longer delays to avoid bot detection.
                    var delayMs = item.NeedsBrowser ? 3000 : GetAdaptiveDelay(item.Url);
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
                }

                // Only reset eager mode if we actually processed items.
                // If queue was empty (e.g., debounce hasn't fired yet), keep eager for the next loop.
                if (processedAny)
                {
                    _eagerMode = false;
                }

                // Clear the "currently fetching" indicator when batch ends
                _currentlyFetchingUrl = null;

                // If items remain uncached, trigger a queue rebuild so the loop continues.
                // Without this, the loop stalls on WaitForSignalAsync waiting for a user
                // interaction signal that never comes when the user is idle on the link list.
                if (processedAny && HasUncachedEligibleUrls())
                {
                    RequestQueueRebuild();
                }

                // Either queue is empty, user is active, or paused — wait for signal before next batch
                await WaitForSignalAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in pre-load loop");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
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

    public PreloadProgress GetProgress()
    {
        List<string> eligible;
        List<string> needsJs;

        lock (_queueLock)
        {
            eligible = _allEligibleUrls;
            needsJs = _needsJsUrls;
        }

        var cachedCount = eligible.Count(url => _cache.Contains(url) || IsInArticleCache(url));
        var hasQueuedWork = _queue.Count > 0 || !_inFlight.IsEmpty;

        return new PreloadProgress
        {
            TotalCacheableLinks = eligible.Count,
            CachedCount = cachedCount,
            NeedsBrowserCount = needsJs.Count,
            PaywalledLinkCount = _paywalledLinkCount,
            IsActivelyFetching = hasQueuedWork && !_paused,
            CurrentlyFetchingUrl = _currentlyFetchingUrl,
        };
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

            var result = await inFlightTask.WaitAsync(cts.Token);
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

    internal static bool IsBotDetectionResponse(string html)
    {
        var indicators = new[]
        {
            "attention required! | cloudflare",
            "you have been blocked",
            "checking your browser",
            "cf-browser-verification",
            "cf-challenge",
            "challenge-platform",
            "just a moment...",
            "enable cookies",
            "please enable javascript",
            "access denied"
        };

        return Array.Exists(indicators, i => html.Contains(i, StringComparison.OrdinalIgnoreCase));
    }

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

    internal List<PreloadItem> BuildQueue(
        int selectedIndex,
        IReadOnlyList<LinkNode> visibleNodes,
        string currentPageUrl)
    {
        var items = new List<PreloadItem>();
        var allEligibleWithIndex = new List<(string Url, int ListIndex)>();
        var needsJs = new List<string>();
        var paywalledCount = 0;

        for (var i = 0; i < visibleNodes.Count; i++)
        {
            var node = visibleNodes[i];

            // Only pre-load content links
            if (node.IsGroupHeader || node.Link.Type != LinkType.Content)
            {
                continue;
            }

            var url = node.Link.Url;
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            // Same-origin only
            if (!UrlNormalizer.IsSameOrigin(url, currentPageUrl))
            {
                continue;
            }

            // Paywalled domains: section pages are free (always preload),
            // article pages need cookies (skip if unauthenticated)
            if (IsPaywalledDomain(url))
            {
                if (PageClassifier.IsSectionUrlPattern(url) || _hasPaywalledCookies)
                {
                    allEligibleWithIndex.Add((url, i));
                    TryAddEligibleUrl(url, i, needsJs, items);
                }
                else
                {
                    paywalledCount++;
                }

                continue;
            }

            allEligibleWithIndex.Add((url, i));
            TryAddEligibleUrl(url, i, needsJs, items);
        }

        // Sort all eligible URLs by priority (cursor proximity)
        allEligibleWithIndex.Sort((a, b) =>
        {
            var scoreA = ComputePriorityScore(a.ListIndex, selectedIndex);
            var scoreB = ComputePriorityScore(b.ListIndex, selectedIndex);
            var cmp = scoreA.CompareTo(scoreB);
            return cmp != 0 ? cmp : a.ListIndex.CompareTo(b.ListIndex);
        });

        // Apply budget limit to eligible URLs
        var budget = _config.MaxPreloadLinks;
        if (allEligibleWithIndex.Count > budget)
        {
            allEligibleWithIndex.RemoveRange(budget, allEligibleWithIndex.Count - budget);
        }

        var budgetedUrls = new HashSet<string>(
            allEligibleWithIndex.Select(e => e.Url), StringComparer.OrdinalIgnoreCase);

        // Sort queue items by priority score: cursor proximity (primary), then list index (tiebreaker)
        items.Sort((a, b) =>
        {
            var scoreA = ComputePriorityScore(a.ListIndex, selectedIndex);
            var scoreB = ComputePriorityScore(b.ListIndex, selectedIndex);

            // Lower score = higher priority
            var cmp = scoreA.CompareTo(scoreB);
            return cmp != 0 ? cmp : a.ListIndex.CompareTo(b.ListIndex);
        });

        // Trim items and needsJs to the budgeted set
        items.RemoveAll(item => !budgetedUrls.Contains(item.Url));
        needsJs.RemoveAll(url => !budgetedUrls.Contains(url));

        var allEligible = allEligibleWithIndex.Select(e => e.Url).ToList();
        UpdateProgressTracking(allEligible, needsJs, paywalledCount);
        return items;
    }

    internal List<PreloadItem> BuildCollectionQueue(
        int selectedIndex,
        IReadOnlyList<string> urls)
    {
        var items = new List<PreloadItem>();
        var allEligibleWithIndex = new List<(string Url, int ListIndex)>();
        var needsJs = new List<string>();

        for (var i = 0; i < urls.Count; i++)
        {
            var url = urls[i];
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            // Paywalled domains: skip if no cookies, include if authenticated
            if (IsPaywalledDomain(url) && !_hasPaywalledCookies)
            {
                continue;
            }

            allEligibleWithIndex.Add((url, i));
            TryAddEligibleUrl(url, i, needsJs, items);
        }

        // Sort all eligible URLs by distance from selected index
        allEligibleWithIndex.Sort((a, b) =>
            Math.Abs(a.ListIndex - selectedIndex).CompareTo(
                Math.Abs(b.ListIndex - selectedIndex)));

        // Apply budget limit to eligible URLs
        var budget = _config.MaxPreloadLinks;
        if (allEligibleWithIndex.Count > budget)
        {
            allEligibleWithIndex.RemoveRange(budget, allEligibleWithIndex.Count - budget);
        }

        var budgetedUrls = new HashSet<string>(
            allEligibleWithIndex.Select(e => e.Url), StringComparer.OrdinalIgnoreCase);

        // Sort queue items by distance from selected index (closest first)
        items.Sort((a, b) =>
            Math.Abs(a.ListIndex - selectedIndex).CompareTo(
                Math.Abs(b.ListIndex - selectedIndex)));

        // Trim items and needsJs to the budgeted set
        items.RemoveAll(item => !budgetedUrls.Contains(item.Url));
        needsJs.RemoveAll(url => !budgetedUrls.Contains(url));

        var allEligible = allEligibleWithIndex.Select(e => e.Url).ToList();
        UpdateProgressTracking(allEligible, needsJs);
        return items;
    }

    /// <summary>
    /// Returns the appropriate delay after fetching a URL, based on whether the next
    /// dequeued item targets the same domain or a different one.
    /// </summary>
    internal int GetAdaptiveDelay(string lastFetchedUrl)
    {
        // Paywalled domains get extra-long delay with jitter to avoid bot detection
        if (IsPaywalledDomain(lastFetchedUrl))
        {
            var jitter = Random.Shared.Next(-1500, 1500);
            return Math.Max(2000, _config.PaywalledDomainDelayMs + jitter);
        }

        if (!_config.AdaptiveRateLimitEnabled)
        {
            return _config.PreloadDelayMs;
        }

        var lastOrigin = UrlNormalizer.GetOrigin(lastFetchedUrl);
        if (lastOrigin == null)
        {
            return _config.PreloadDelayMs;
        }

        // Record this domain's last request time
        _lastRequestByDomain[lastOrigin] = DateTime.UtcNow;

        // Peek at the next item to decide delay
        PreloadItem? nextItem;
        lock (_queueLock)
        {
            nextItem = _queue.Count > 0 ? _queue[0] : null;
        }

        if (nextItem == null)
        {
            return _config.PreloadDelayMs;
        }

        var nextOrigin = UrlNormalizer.GetOrigin(nextItem.Url);
        if (nextOrigin == null)
        {
            return _config.PreloadDelayMs;
        }

        // Same domain → full delay; different domain → shorter delay
        if (string.Equals(lastOrigin, nextOrigin, StringComparison.OrdinalIgnoreCase))
        {
            return _config.PreloadDelayMs;
        }

        // For cross-domain, also check if we've recently hit this domain
        if (_lastRequestByDomain.TryGetValue(nextOrigin, out var lastRequest))
        {
            var elapsed = (int)(DateTime.UtcNow - lastRequest).TotalMilliseconds;
            var remaining = _config.PreloadDelayMs - elapsed;
            if (remaining > _config.CrossDomainDelayMs)
            {
                // We hit this domain recently — wait the remaining same-domain cooldown
                return remaining;
            }
        }

        return _config.CrossDomainDelayMs;
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

    private void RefreshPaywalledCookieState()
    {
        if (_cookieManager == null)
        {
            _hasPaywalledCookies = false;
            return;
        }

        try
        {
            var info = _cookieManager.GetCookieInfoAsync().GetAwaiter().GetResult();
            var hadCookies = _hasPaywalledCookies;
            _hasPaywalledCookies = info is { Exists: true, IsExpired: false };

            // When cookies become available, refresh the HttpClient's CookieContainer
            // and clear paywalled domains from _needsJsDomains so they can be re-queued
            if (_hasPaywalledCookies && !hadCookies)
            {
                _httpCookieRefresher?.RefreshAsync().GetAwaiter().GetResult();
                _logger.LogInformation("Paywalled cookies detected, refreshed HTTP cookie container");

                foreach (var origin in _needsJsDomains.Keys.Where(IsPaywalledDomain).ToList())
                {
                    _needsJsDomains.TryRemove(origin, out _);
                }
            }
        }
        catch
        {
            _hasPaywalledCookies = false;
        }
    }

    private bool IsPaywalledDomain(string url)
    {
        if (_paywalledDomains.Length == 0)
        {
            return false;
        }

        try
        {
            var host = new Uri(url).Host;
            return _paywalledDomains.Any(d =>
                host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
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
            if (CanBrowserPreload && IsPaywalledDomain(url) && !IsUrlCached(url))
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

    private bool IsDomainNeedsJs(string url)
    {
        var origin = UrlNormalizer.GetOrigin(url);
        return origin != null && _needsJsDomains.ContainsKey(origin);
    }

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
            await _queueSignal.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
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

    private async Task PreloadUrlAsync(string url, CancellationToken cancellationToken)
    {
        // Paywalled domains: re-check cookie validity before each attempt (cookies may
        // expire mid-session) and enforce per-session limit
        if (IsPaywalledDomain(url))
        {
            RefreshPaywalledCookieState();
            if (!_hasPaywalledCookies || _paywalledPreloadCount >= _config.MaxPaywalledPreloads)
            {
                _logger.LogDebug(
                    "Skipping preload for paywalled domain (cookies={HasCookies}, count={Count}/{Max}): {Url}",
                    _hasPaywalledCookies,
                    _paywalledPreloadCount,
                    _config.MaxPaywalledPreloads,
                    url);

                // Mark domain as needing JS so these URLs are counted in NeedsBrowserCount
                // on the next queue rebuild, rather than vanishing from progress tracking
                var origin = UrlNormalizer.GetOrigin(url);
                if (origin != null)
                {
                    _needsJsDomains[origin] = true;
                }

                NotifyProgressChanged();
                return;
            }
        }

        var normalizedUrl = UrlNormalizer.Normalize(url);

        // In-flight deduplication
        var tcs = new TaskCompletionSource<PageLoadResult>();
        var existing = _inFlight.GetOrAdd(normalizedUrl, tcs.Task);
        if (existing != tcs.Task)
        {
            // Another fetch is already in progress for this URL
            _logger.LogDebug("Skipping duplicate pre-load for {Url}", url);
            return;
        }

        try
        {
            _currentlyFetchingUrl = url;
            _logger.LogDebug("Pre-loading: {Url}", url);
            var result = await HttpFetchAsync(url, cancellationToken);

            if (result.Success)
            {
                if (IsBotDetectionResponse(result.Html))
                {
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _circuitBrokenDomains[origin] = DateTime.UtcNow;
                        _logger.LogWarning(
                            "Bot detection triggered for {Origin}, stopping pre-loads for this domain",
                            origin);
                    }
                }
                else if (ReadableContentExtractor.IsEmptyArticleShell(result.Html))
                {
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _needsJsDomains[origin] = true;
                        _logger.LogDebug(
                            "Domain {Origin} needs JS rendering, skipping future HTTP pre-loads",
                            origin);
                    }
                }
                else if (!CachingPageLoader.HasSufficientContent(result.Html))
                {
                    // Page passed the article shell check but has too little visible text.
                    // This catches JS shells without article markup, empty pages, and pages
                    // where content is loaded dynamically. Mark the domain as needing JS
                    // so future preloads for this domain are skipped.
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _needsJsDomains[origin] = true;
                    }

                    _logger.LogDebug(
                        "Skipping cache for preloaded URL with insufficient content: {Url}",
                        url);
                }
                else if (ReadableContentExtractor.IsArticlePage(result.Html) &&
                         !ReadableContentExtractor.HasExtractableContent(result.Html))
                {
                    // Page looks like an article (has article indicators) but has no
                    // extractable article content. This catches JS-heavy sites like NYT
                    // that return a shell with nav/header text but no article body.
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _needsJsDomains[origin] = true;
                    }

                    _logger.LogDebug(
                        "Skipping cache for preloaded URL with no extractable article content: {Url}",
                        url);
                }
                else if (IsRedirectedUrl(url, result.Url))
                {
                    // Server redirected to a different page (e.g., paywalled article → section page).
                    // Do NOT cache under the original URL — the content doesn't match the request.
                    _logger.LogDebug(
                        "Skipping cache for redirected URL: requested={RequestUrl}, redirected={FinalUrl}",
                        url,
                        result.Url);
                }
                else if (ReadableContentExtractor.HasPaywallElements(result.Html))
                {
                    // Paywall gate detected — don't cache truncated preview content.
                    // Mark domain as needing browser (browser with cookies).
                    var origin = UrlNormalizer.GetOrigin(url);
                    if (origin != null)
                    {
                        _needsJsDomains[origin] = true;
                    }

                    _logger.LogDebug("Skipping cache for paywalled content: {Url}", url);
                }
                else if (IsPaywalledDomain(url) && !CachingPageLoader.HasSufficientContent(result.Html, MinPaywalledWordCount))
                {
                    // Paywalled domain passed basic checks but has too few words.
                    // Cookies may not have loaded correctly (domain mismatch, encryption
                    // error), resulting in a truncated preview. Don't cache — the browser
                    // with cookies will get the full article.
                    _logger.LogDebug(
                        "Skipping cache for paywalled domain with insufficient content (<{MinWords} words): {Url}",
                        MinPaywalledWordCount,
                        url);
                }
                else
                {
                    _cache.Put(url, result);

                    if (IsPaywalledDomain(url))
                    {
                        Interlocked.Increment(ref _paywalledPreloadCount);
                    }

                    _logger.LogDebug("Pre-loaded and cached: {Url}", url);

                    // Warm the PageBuildCache: extract links and readable content
                    // so navigation to this URL skips extraction entirely.
                    await TryBuildAndCachePageAsync(url, result, cancellationToken);

                    // Bridge to article content cache: extract article and persist
                    // so collection items served from article cache on navigation.
                    await TryExtractAndCacheArticleAsync(url, result.Html, cancellationToken);
                }
            }
            else
            {
                _logger.LogDebug("Pre-load failed for {Url}: {Error}", url, result.ErrorMessage);
            }

            tcs.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            _logger.LogDebug(ex, "Pre-load error for {Url}", url);
        }
        finally
        {
            _inFlight.TryRemove(normalizedUrl, out _);
            NotifyProgressChanged();

            // If this domain was marked as needsJs and browser preloading is available,
            // trigger a queue rebuild so remaining URLs for this domain get routed
            // through the browser path instead of being skipped.
            if (IsDomainNeedsJs(url) && CanBrowserPreload)
            {
                _logger.LogInformation(
                    "Domain {Url} marked as needsJs with browser available — triggering queue rebuild",
                    UrlNormalizer.GetOrigin(url));
                RequestQueueRebuild();
            }
        }
    }

    private async Task BrowserPreloadUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (_browserSession == null)
        {
            return;
        }

        if (IsPaywalledDomain(url) && _paywalledPreloadCount >= _config.MaxPaywalledPreloads)
        {
            _logger.LogDebug(
                "Browser preload skipped — paywalled limit reached ({Count}/{Max}): {Url}",
                _paywalledPreloadCount,
                _config.MaxPaywalledPreloads,
                url);
            return;
        }

        try
        {
            _currentlyFetchingUrl = url;
            _logger.LogDebug("Browser pre-loading: {Url}", url);

            // Lazily create background page on first use.
            // Context may not exist yet if browser warmup is still running (fire-and-forget).
            // Retry once after a short wait to handle the race condition.
            if (_backgroundPage == null)
            {
                _backgroundPage = await _browserSession.CreateBackgroundPageAsync();
                if (_backgroundPage == null)
                {
                    await Task.Delay(2000, cancellationToken);
                    _backgroundPage = await _browserSession.CreateBackgroundPageAsync();
                }
            }

            if (_backgroundPage == null)
            {
                _logger.LogWarning("Browser context not available for background preload of {Url}", url);
                return;
            }

            await _backgroundPage.GotoAsync(url, new PageGotoOptions
            {
                Timeout = 15000,
                WaitUntil = WaitUntilState.DOMContentLoaded,
            });

            // Wait for JS content to render (article paragraphs or sufficient DOM size)
            try
            {
                await _backgroundPage.WaitForFunctionAsync(
                    @"() => {
                        if (document.querySelector('[role=""main""] p, article p, .entry-content p, .post-content p')) return true;
                        if (document.querySelector('[data-testid=""storyContent""] p, .StoryBodyCompanionColumn p')) return true;
                        return document.body && document.body.innerHTML.length > 5000;
                    }",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 4000 });
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("Browser preload content render wait timed out for {Url}", url);
            }
            catch (PlaywrightException)
            {
                _logger.LogDebug("Browser preload content render wait failed for {Url}", url);
            }

            await PageLoader.DismissOverlaysAsync(_backgroundPage, _logger);

            var html = await _backgroundPage.ContentAsync();
            var finalUrl = _backgroundPage.Url;

            if (PageLoader.IsBotChallengePage(html))
            {
                var origin = UrlNormalizer.GetOrigin(url);
                if (origin != null)
                {
                    _circuitBrokenDomains[origin] = DateTime.UtcNow;
                }

                _logger.LogWarning("Bot challenge during browser preload: {Url}", url);
                return;
            }

            // Skip paywall element check for browser preloads — authenticated pages
            // still contain paywall CSS classes (gateway, meter-) in the DOM even though
            // the gate is inactive. The content sufficiency check below catches truly
            // paywalled content (too few words).
            if (!CachingPageLoader.HasSufficientContent(html, MinPaywalledWordCount))
            {
                _logger.LogDebug("Browser preload content below threshold for {Url}", url);
                return;
            }

            // Cache the rendered HTML
            var metadata = ExtractMetadata(html);
            var result = PageLoadResult.Successful(finalUrl, html, metadata);
            _cache.Put(url, result);

            // Extract article content for the article cache
            if (_contentExtractor != null && _articleContentCache != null)
            {
                try
                {
                    var readable = await _contentExtractor.ExtractAsync(html, url, cancellationToken);
                    if (readable != null && !readable.IsPaywalled)
                    {
                        var article = new ExtractedArticle
                        {
                            Title = readable.Title,
                            CleanedText = readable.CleanedText,
                            Author = readable.Author,
                            Url = url,
                            WordCount = readable.WordCount,
                            PublishedDate = readable.PublishedDate,
                        };

                        await _articleContentCache.PutAsync(url, article, cancellationToken);
                        _articleCachedUrls[UrlNormalizer.Normalize(url)] = url;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Article extraction failed for browser-preloaded {Url}", url);
                }
            }

            // Only count successful caches against the paywalled limit
            if (IsPaywalledDomain(url))
            {
                Interlocked.Increment(ref _paywalledPreloadCount);
            }

            _logger.LogInformation("Browser pre-loaded and cached: {Url}", url);
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "Browser preload failed for {Url}", url);

            // Page may have crashed — null it so next call creates a fresh one
            _backgroundPage = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Browser preload error for {Url}", url);
        }
        finally
        {
            _currentlyFetchingUrl = null;
            NotifyProgressChanged();
        }
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
    /// Extracts links and readable content from pre-loaded HTML and stores a
    /// PageBuildCache alongside the HTML in IPageCache. This means navigating to
    /// a preloaded URL skips link extraction, tree building, and content extraction
    /// entirely — the page is rebuilt from cached inputs in ~1ms.
    /// </summary>
    private async Task TryBuildAndCachePageAsync(
        string url,
        PageLoadResult result,
        CancellationToken cancellationToken)
    {
        if (_linkExtractor == null)
        {
            return;
        }

        try
        {
            var links = await _linkExtractor.ExtractLinksAsync(
                result.Html, result.Url ?? url, cancellationToken);

            if (links.Count == 0)
            {
                return;
            }

            ReadableContent? readable = null;
            if (_contentExtractor != null)
            {
                readable = await _contentExtractor.ExtractAsync(
                    result.Html, result.Url ?? url, cancellationToken);
            }

            var isArticlePage = ReadableContentExtractor.IsArticlePage(result.Html);
            var doc = new HtmlDocument();
            doc.LoadHtml(result.Html);
            var articleContainerCount = doc.DocumentNode.SelectNodes("//article")?.Count ?? 0;
            var classification = PageClassifier.Classify(links, isArticlePage, articleContainerCount, result.Url ?? url);

            var buildCache = new PageBuildCache
            {
                Links = links,
                ReadableContent = readable,
                Metadata = result.Metadata ?? new PageMetadata { Title = "Untitled" },
                FinalUrl = result.Url ?? url,
                Classification = classification,
            };

            _cache.PutBuildCache(url, buildCache);
            _logger.LogDebug(
                "PageBuildCache warmed from preload: {Url} ({LinkCount} links)",
                url,
                links.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PageBuildCache warm failed for preloaded {Url}", url);
        }
    }

    /// <summary>
    /// Extracts article content from pre-loaded HTML and stores it in the persistent
    /// article content cache. This bridges the background preload (IPageCache) to the
    /// article cache (IArticleContentCache) so that collection items can be served
    /// directly from the article cache on navigation, skipping network I/O entirely.
    /// </summary>
    private async Task TryExtractAndCacheArticleAsync(string url, string html, CancellationToken cancellationToken)
    {
        if (_contentExtractor == null || _articleContentCache == null)
        {
            return;
        }

        // Never article-cache content from paywalled domains via HTTP.
        // These need browser with cookies for full content; HTTP fetch
        // only gets truncated preview that may not trigger paywall detection.
        if (IsPaywalledDomain(url))
        {
            _logger.LogDebug("Skipping article cache for paywalled domain: {Url}", url);
            return;
        }

        try
        {
            var readable = await _contentExtractor.ExtractAsync(html, url, cancellationToken);
            if (readable == null || readable.IsPaywalled)
            {
                return;
            }

            var article = new ExtractedArticle
            {
                Title = readable.Title,
                CleanedText = readable.CleanedText,
                Author = readable.Author,
                Url = url,
                WordCount = readable.WordCount,
                PublishedDate = readable.PublishedDate,
            };

            await _articleContentCache.PutAsync(url, article, cancellationToken);
            _articleCachedUrls[UrlNormalizer.Normalize(url)] = url;
            _logger.LogDebug(
                "Article content cached from preload: {Url} ({Words} words)",
                url,
                article.WordCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Article extraction failure should not break preloading
            _logger.LogDebug(ex, "Article extraction/cache failed for preloaded {Url}", url);
        }
    }

    private async Task<PageLoadResult> HttpFetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            request.Headers.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.Add("Accept-Language", "en-US,en;q=0.9");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var response = await _httpClient.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return PageLoadResult.Failure(
                    $"HTTP {(int)response.StatusCode}",
                    (int)response.StatusCode);
            }

            var html = await response.Content.ReadAsStringAsync(cts.Token);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            var metadata = ExtractMetadata(html);

            return PageLoadResult.Successful(finalUrl, html, metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PageLoadResult.Failure(ex.Message);
        }
    }

    internal sealed record PreloadItem(string Url, int ListIndex, bool NeedsBrowser = false);
}
