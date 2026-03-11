// Educational and personal use only.

using System.Collections.Concurrent;
using System.Net;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Configuration;

namespace TermReader.Infrastructure.Browser.Cache;

/// <summary>
/// Background service that pre-loads pages the user is likely to navigate to next.
/// Uses HTTP-only fetching (never Selenium), rate limiting, same-origin filtering,
/// and per-domain circuit breaking.
/// </summary>
internal sealed class BackgroundPreloadService : IPreloadService
{
    private readonly IPageCache _cache;
    private readonly IIdleDetector _idleDetector;
    private readonly HttpClient _httpClient;
    private readonly CacheConfiguration _config;
    private readonly ILogger<BackgroundPreloadService> _logger;

    private readonly ConcurrentDictionary<string, Task<PageLoadResult>> _inFlight = new();
    private readonly ConcurrentDictionary<string, DateTime> _circuitBrokenDomains = new();
    private readonly ConcurrentDictionary<string, bool> _needsJsDomains = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastRequestByDomain = new();
    private readonly SemaphoreSlim _queueSignal = new(0, 1);
    private readonly object _queueLock = new();
    private readonly Timer _debounceTimer;
    private List<PreloadItem> _queue = [];
    private volatile bool _paused;
    private volatile bool _eagerMode;
    private volatile bool _disposed;

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

    public BackgroundPreloadService(
        IPageCache cache,
        IIdleDetector idleDetector,
        HttpClient httpClient,
        CacheConfiguration config,
        ILogger<BackgroundPreloadService> logger)
    {
        _cache = cache;
        _idleDetector = idleDetector;
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public event Action? ProgressChanged;

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

                // Process the entire queue while user stays idle
                while (!cancellationToken.IsCancellationRequested && !_disposed && !_paused && _idleDetector.IsIdle)
                {
                    var item = DequeueNext();
                    if (item == null)
                    {
                        break;
                    }

                    await PreloadUrlAsync(item.Url, cancellationToken);

                    // Rate limit between pre-loads (adaptive delay), but NO idle re-check
                    var delayMs = GetAdaptiveDelay(item.Url);
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
                }

                // Batch complete (queue drained) — reset eager mode
                _eagerMode = false;

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

        var cachedCount = eligible.Count(url => _cache.Contains(url));

        return new PreloadProgress
        {
            TotalCacheableLinks = eligible.Count,
            CachedCount = cachedCount,
            NeedsBrowserCount = needsJs.Count
        };
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
        _debounceTimer.Dispose();
        SignalQueueChanged();
        _queueSignal.Dispose();
    }

    internal static bool IsBotDetectionResponse(string html)
    {
        var lower = html.ToLowerInvariant();
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

        return Array.Exists(indicators, i => lower.Contains(i));
    }

    internal List<PreloadItem> BuildQueue(
        int selectedIndex,
        IReadOnlyList<LinkNode> visibleNodes,
        string currentPageUrl)
    {
        var items = new List<PreloadItem>();
        var allEligible = new List<string>();
        var needsJs = new List<string>();

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

            TryAddEligibleUrl(url, i, allEligible, needsJs, items);
        }

        // Sort by original list index (top-to-bottom)
        items.Sort((a, b) => a.ListIndex.CompareTo(b.ListIndex));

        UpdateProgressTracking(allEligible, needsJs);
        return items;
    }

    internal List<PreloadItem> BuildCollectionQueue(
        int selectedIndex,
        IReadOnlyList<string> urls)
    {
        var items = new List<PreloadItem>();
        var allEligible = new List<string>();
        var needsJs = new List<string>();

        for (var i = 0; i < urls.Count; i++)
        {
            var url = urls[i];
            if (string.IsNullOrEmpty(url))
            {
                continue;
            }

            TryAddEligibleUrl(url, i, allEligible, needsJs, items);
        }

        // Sort by distance from selected index (closest first)
        items.Sort((a, b) =>
            Math.Abs(a.ListIndex - selectedIndex).CompareTo(
                Math.Abs(b.ListIndex - selectedIndex)));

        UpdateProgressTracking(allEligible, needsJs);
        return items;
    }

    /// <summary>
    /// Returns the appropriate delay after fetching a URL, based on whether the next
    /// dequeued item targets the same domain or a different one.
    /// </summary>
    internal int GetAdaptiveDelay(string lastFetchedUrl)
    {
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

    private void TryAddEligibleUrl(
        string url,
        int listIndex,
        List<string> allEligible,
        List<string> needsJs,
        List<PreloadItem> items)
    {
        allEligible.Add(url);

        if (IsDomainNeedsJs(url))
        {
            needsJs.Add(url);
            return;
        }

        if (IsUrlCached(url))
        {
            return;
        }

        if (IsDomainCircuitBroken(url))
        {
            return;
        }

        items.Add(new PreloadItem(url, listIndex));
    }

    private bool IsUrlCached(string url) => _cache.Contains(url);

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

    private void UpdateProgressTracking(List<string> allEligible, List<string> needsJs)
    {
        lock (_queueLock)
        {
            _allEligibleUrls = allEligible;
            _needsJsUrls = needsJs;
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

            var newQueue = BuildQueue(selectedIndex, visibleNodes, currentPageUrl);

            lock (_queueLock)
            {
                _queue = newQueue;
            }

            SignalQueueChanged();
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
                else
                {
                    _cache.Put(url, result);
                    _logger.LogDebug("Pre-loaded and cached: {Url}", url);

                    try
                    {
                        ProgressChanged?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "ProgressChanged handler error");
                    }
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

    internal sealed record PreloadItem(string Url, int ListIndex);
}
