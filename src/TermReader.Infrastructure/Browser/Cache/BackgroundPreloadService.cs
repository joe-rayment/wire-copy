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
    private readonly ConcurrentDictionary<string, byte> _circuitBrokenDomains = new();
    private readonly object _queueLock = new();
    private List<PreloadItem> _queue = [];
    private volatile bool _paused;
    private volatile bool _disposed;
    private CancellationTokenSource? _queueChangedCts;

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
    }

    public void NotifyPageLoaded(Page page)
    {
        // Page loaded — queue will be rebuilt on next selection change
        _logger.LogDebug("Page loaded: {Url}", page.Url);
    }

    public void NotifySelectionChanged(int selectedIndex, IReadOnlyList<LinkNode> visibleNodes, string currentPageUrl)
    {
        var newQueue = BuildQueue(selectedIndex, visibleNodes, currentPageUrl);

        lock (_queueLock)
        {
            _queue = newQueue;
        }

        // Signal the background loop that the queue changed
        try
        {
            _queueChangedCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore if already disposed during shutdown
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
                    // No items to pre-load; wait for queue change or short delay
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _queueChangedCts = waitCts;

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), waitCts.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Queue changed — loop back
                    }
                    finally
                    {
                        _queueChangedCts = null;
                    }

                    continue;
                }

                if (_paused)
                {
                    continue;
                }

                await PreloadUrlAsync(item.Url, cancellationToken);

                // Rate limit between pre-loads
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _queueChangedCts = delayCts;

                try
                {
                    await Task.Delay(_config.PreloadDelayMs, delayCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Queue changed or paused — continue
                }
                finally
                {
                    _queueChangedCts = null;
                }
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

        try
        {
            _queueChangedCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _queueChangedCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore
        }
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

            // Skip already cached
            if (_cache.Contains(url))
            {
                continue;
            }

            // Skip circuit-broken domains
            var origin = UrlNormalizer.GetOrigin(url);
            if (origin != null && _circuitBrokenDomains.ContainsKey(origin))
            {
                continue;
            }

            // Determine priority
            PreloadPriority priority;
            if (i == selectedIndex)
            {
                priority = PreloadPriority.Focused;
            }
            else if (Math.Abs(i - selectedIndex) <= _config.NearbyLinkRadius)
            {
                priority = PreloadPriority.Nearby;
            }
            else
            {
                priority = PreloadPriority.Speculative;
            }

            items.Add(new PreloadItem(url, priority, Math.Abs(i - selectedIndex)));
        }

        // Sort by priority, then distance from selection
        items.Sort((a, b) =>
        {
            var cmp = a.Priority.CompareTo(b.Priority);
            return cmp != 0 ? cmp : a.Distance.CompareTo(b.Distance);
        });

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
        var node = doc.DocumentNode.SelectSingleNode($"//meta[@name='{name}']") ??
                   doc.DocumentNode.SelectSingleNode($"//meta[@property='{name}']");

        var value = node?.GetAttributeValue("content", null);
        return value != null ? WebUtility.HtmlDecode(value) : null;
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
                        _circuitBrokenDomains.TryAdd(origin, 0);
                        _logger.LogWarning(
                            "Bot detection triggered for {Origin}, stopping pre-loads for this domain",
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

            var response = await _httpClient.SendAsync(request, cts.Token);

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

    internal sealed record PreloadItem(string Url, PreloadPriority Priority, int Distance);
}
