// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-frpl.6 — composes the unattended rendered load (headful browser,
/// no TUI — via
/// <see cref="IPreloadService.LoadRenderedHtmlAsync"/>, which uses an isolated
/// page in the preload context and pauses the preload loop for the duration)
/// with the SAME <see cref="ILinkExtractor"/> and durable
/// <see cref="IHierarchyConfigStore"/> the interactive path uses. It depends on
/// NONE of NavigationService / the foreground IPageAccessQueue / PageLoadPipeline
/// — a scheduled run never disturbs the user's foreground session.
/// </summary>
internal sealed class UnattendedSectionLoadAdapter : IUnattendedSectionLoader
{
    private readonly IPreloadService _preloadService;
    private readonly ILinkExtractor _linkExtractor;
    private readonly IHierarchyConfigStore _configStore;
    private readonly IAutoCookieRefresher? _cookieRefresher;

    public UnattendedSectionLoadAdapter(
        IPreloadService preloadService,
        ILinkExtractor linkExtractor,
        IHierarchyConfigStore configStore,
        IAutoCookieRefresher? cookieRefresher = null)
    {
        _preloadService = preloadService;
        _linkExtractor = linkExtractor;
        _configStore = configStore;
        _cookieRefresher = cookieRefresher;
    }

    public async Task<UnattendedSectionLoad> LoadLinksAndConfigAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        var load = await _preloadService.LoadRenderedHtmlAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
        if (load.Outcome != LoadOutcome.Ok || string.IsNullOrEmpty(load.Html))
        {
            return new UnattendedSectionLoad { Outcome = load.Outcome };
        }

        var finalUrl = string.IsNullOrEmpty(load.FinalUrl) ? sourceUrl : load.FinalUrl;

        // workspace-frpl.11 (B8): a scheduled load that rendered a logged-in-looking
        // page is an opportunity to refresh cookies.json from the foreground session
        // so SUBSEQUENT scheduled runs stay authenticated. The refresher is
        // conservative (paywalled-domain + logged-in markup + 24h cooldown gates) and
        // swallows its own failures, so this never blocks or fails the load. It does
        // NOT attempt unattended re-authentication.
        if (_cookieRefresher != null)
        {
            await _cookieRefresher.MaybeRefreshAsync(finalUrl, load.Html, cancellationToken).ConfigureAwait(false);
        }

        var links = await _linkExtractor.ExtractLinksAsync(load.Html, finalUrl, cancellationToken).ConfigureAwait(false);
        var config = await _configStore.GetConfigAsync(sourceUrl).ConfigureAwait(false);

        return new UnattendedSectionLoad
        {
            Outcome = LoadOutcome.Ok,
            Links = links,
            Config = config,
        };
    }
}
