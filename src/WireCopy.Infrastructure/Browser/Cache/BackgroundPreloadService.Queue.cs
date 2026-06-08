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
/// Queue-building portion of <see cref="BackgroundPreloadService"/>: turns the
/// visible link list / collection into a prioritised, eligibility-filtered
/// preload queue, plus adaptive rate-limit delay. Split out for size; behaviour
/// is unchanged (workspace-dn1g).
/// </summary>
internal sealed partial class BackgroundPreloadService
{
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
            // article pages need cookies (skip if unauthenticated). When a browser
            // session is available with cookies, route paywalled articles directly
            // to the browser path — HTTP-with-cookies on these sites returns thin
            // previews even when the request is technically authenticated.
            if (IsPaywalledDomain(url))
            {
                var isSection = PageClassifier.IsSectionUrlPattern(url);
                if (isSection || _hasPaywalledCookies)
                {
                    allEligibleWithIndex.Add((url, i));
                    if (!isSection && CanBrowserPreload && !IsUrlCached(url))
                    {
                        items.Add(new PreloadItem(url, i, NeedsBrowser: true));
                    }
                    else
                    {
                        TryAddEligibleUrl(url, i, needsJs, items);
                    }
                }
                else
                {
                    paywalledCount++;
                    LogPaywalledSkipOnce(url);
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

            // Paywalled domains: skip if no cookies, include if authenticated.
            // Articles route directly to browser preload when a browser session is
            // available — HTTP-with-cookies returns thin previews on these sites.
            if (IsPaywalledDomain(url))
            {
                if (!_hasPaywalledCookies)
                {
                    LogPaywalledSkipOnce(url);
                    continue;
                }

                allEligibleWithIndex.Add((url, i));
                if (!PageClassifier.IsSectionUrlPattern(url) && CanBrowserPreload && !IsUrlCached(url))
                {
                    items.Add(new PreloadItem(url, i, NeedsBrowser: true));
                }
                else
                {
                    TryAddEligibleUrl(url, i, needsJs, items);
                }

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
}
