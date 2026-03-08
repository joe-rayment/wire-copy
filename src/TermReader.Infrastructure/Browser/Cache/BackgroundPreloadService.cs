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
    private readonly SemaphoreSlim _queueSignal = new(0, 1);
    private readonly object _queueLock = new();
    private readonly Timer _debounceTimer;
    private List<PreloadItem> _queue = [];
    private volatile bool _paused;
    private volatile bool _disposed;

    // Debounce state: stores the latest selection change parameters
    private int _pendingSelectedIndex;
    private IReadOnlyList<LinkNode>? _pendingVisibleNodes;
    private string? _pendingCurrentPageUrl;

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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Background pre-load service started");

        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            try
            {
                await _idleDetector.WaitForIdleAsync(cancellationToken);

                var item = DequeueNext();
                if (item == null)
                {
                    // No items to pre-load; wait for queue change or short timeout
                    await WaitForSignalAsync(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }

                if (_paused)
                {
                    continue;
                }

                await PreloadUrlAsync(item.Url, cancellationToken);

                // Rate limit between pre-loads
                await WaitForSignalAsync(TimeSpan.FromMilliseconds(_config.PreloadDelayMs), cancellationToken);
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

        var cachedCount = 0;
        foreach (var url in eligible)
        {
            if (_cache.Contains(url))
            {
                cachedCount++;
            }
        }

        return new PreloadProgress
        {
            TotalCacheableLinks = eligible.Count,
            CachedCount = cachedCount,
            NeedsBrowserCount = needsJs.Count
        };
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

            // Track for progress: this is a same-origin content link
            allEligible.Add(url);

            // Track needs-JS urls separately for progress reporting
            var origin = UrlNormalizer.GetOrigin(url);
            if (origin != null && _needsJsDomains.ContainsKey(origin))
            {
                needsJs.Add(url);
                continue;
            }

            // Skip already cached
            if (_cache.Contains(url))
            {
                continue;
            }

            // Skip circuit-broken domains (with TTL recovery)
            if (origin != null && _circuitBrokenDomains.TryGetValue(origin, out var brokenAt))
            {
                if (DateTime.UtcNow - brokenAt < TimeSpan.FromSeconds(_config.CircuitBreakerCooldownSeconds))
                {
                    continue;
                }

                // Cooldown elapsed — allow retry
                _circuitBrokenDomains.TryRemove(origin, out _);
            }

            items.Add(new PreloadItem(url, i));
        }

        // Sort by original list index (top-to-bottom)
        items.Sort((a, b) => a.ListIndex.CompareTo(b.ListIndex));

        // Update progress tracking state
        lock (_queueLock)
        {
            _allEligibleUrls = allEligible;
            _needsJsUrls = needsJs;
        }

        return items;
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

    private void OnDebounceElapsed(object? state)
    {
        int selectedIndex;
        IReadOnlyList<LinkNode>? visibleNodes;
        string? currentPageUrl;

        lock (_queueLock)
        {
            selectedIndex = _pendingSelectedIndex;
            visibleNodes = _pendingVisibleNodes;
            currentPageUrl = _pendingCurrentPageUrl;
        }

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
                else
                {
                    _cache.Put(url, result);
                    _logger.LogDebug("Pre-loaded and cached: {Url}", url);
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
