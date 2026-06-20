// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Infrastructure.Browser.Extension;

/// <summary>
/// <see cref="IPageLoader"/> implementation for the host-browser-as-renderer architecture
/// (workspace-blg5 / workspace-wrs5): instead of launching a server-side Playwright browser, it asks
/// the WireCopy Chrome extension (over <see cref="IExtensionBridge"/>) to navigate the user's own tab
/// and return the rendered DOM. All downstream extraction/hierarchy/AI/reader logic runs unchanged on
/// the returned HTML — the only thing that changes is where the rendered DOM comes from.
///
/// <para>Registered in place of <see cref="PageLoader"/> only when <c>WIRECOPY_BROWSER=extension</c>;
/// still wrapped by the caching decorator. There is no server-side bot detection here because the
/// page is rendered by the user's real, logged-in browser — Cloudflare/WAF never sees an automation
/// fingerprint.</para>
/// </summary>
public sealed class ExtensionPageLoader : IPageLoader
{
    private readonly IExtensionBridge _bridge;
    private readonly ILogger<ExtensionPageLoader> _logger;

    public ExtensionPageLoader(IExtensionBridge bridge, ILogger<ExtensionPageLoader> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public async Task<PageLoadResult> LoadAsync(PageLoadRequest request, CancellationToken cancellationToken = default)
    {
        if (!_bridge.IsConnected)
        {
            // The extension hasn't attached yet (user hasn't loaded it, or the SW is reconnecting).
            // Give it a brief grace window before surfacing an actionable failure.
            var ready = await _bridge.WaitForReadyAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                return PageLoadResult.Failure(
                    "Waiting for the WireCopy browser extension to connect. Load the extension and reload the page.");
            }
        }

        try
        {
            // If the tab is ALREADY on this URL (the common case right after a navigation completes,
            // and on extension-mode startup), capture the rendered DOM in place rather than navigating
            // again — re-navigating would reload the tab and restart the overlay (workspace-pfea).
            var sameAsCurrent = !request.ForceRefresh && UrlsEquivalent(request.Url, _bridge.CurrentUrl);

            ExtensionDomSnapshot snapshot;
            if (sameAsCurrent)
            {
                _logger.LogInformation("Capturing current page via extension (already loaded): {Url}", request.Url);
                snapshot = await _bridge.CaptureDomAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Loading page via extension (host-browser renderer): {Url}", request.Url);
                snapshot = await _bridge.NavigateAndCaptureAsync(request.Url, cancellationToken).ConfigureAwait(false);
            }

            var finalUrl = string.IsNullOrEmpty(snapshot.Url) ? request.Url : snapshot.Url;
            var html = snapshot.Html ?? string.Empty;

            if (string.IsNullOrWhiteSpace(html))
            {
                return PageLoadResult.Failure("Extension returned an empty DOM snapshot.");
            }

            // The user's real browser already rendered (and, if needed, let the user solve) any gate,
            // so a hard bot block should not occur. We still run the detector for SOFT signals
            // (paywall preview, cookie banner) and surface them non-fatally as the Playwright path does.
            var detectedAction = HumanActionDetector.Detect(html, finalUrl, statusCode: 0);

            var metadata = PageLoader.ExtractMetadata(html, finalUrl);
            _logger.LogInformation("Loaded page via extension: {Url} ({Bytes} bytes, {W}x{H})", finalUrl, html.Length, snapshot.ViewportWidth, snapshot.ViewportHeight);

            return PageLoadResult.Successful(finalUrl, html, metadata, FetchMethod.Browser) with { RequiredAction = detectedAction };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Extension did not return a DOM snapshot in time for {Url}", request.Url);
            return PageLoadResult.Failure("The browser extension did not respond. Is the tab still open?");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extension page load failed for {Url}", request.Url);
            return PageLoadResult.Failure(ex.Message);
        }
    }

    public async Task<string> GetPageSourceAsync(string url, CancellationToken cancellationToken = default)
    {
        var result = await LoadAsync(new PageLoadRequest { Url = url }, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to load page: {result.ErrorMessage}");
        }

        return result.Html;
    }

    /// <summary>
    /// Loose URL equivalence for "is the tab already here?" — ignores trailing slashes, fragments and
    /// case in the scheme/host. Deliberately conservative: when in doubt it returns false and the
    /// loader navigates (correct, just a redundant reload).
    /// </summary>
    internal static bool UrlsEquivalent(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        if (!Uri.TryCreate(a, UriKind.Absolute, out var ua) || !Uri.TryCreate(b, UriKind.Absolute, out var ub))
        {
            return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        var pathA = ua.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var pathB = ub.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ua.Query, ub.Query, StringComparison.Ordinal);
    }
}
