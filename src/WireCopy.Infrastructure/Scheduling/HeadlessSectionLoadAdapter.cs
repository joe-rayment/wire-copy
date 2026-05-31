// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Application.Interfaces.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>
/// workspace-frpl.6 — composes the headless rendered load (via
/// <see cref="IPreloadService.LoadRenderedHtmlAsync"/>, which uses an isolated
/// page in the preload context and pauses the preload loop for the duration)
/// with the SAME <see cref="ILinkExtractor"/> and durable
/// <see cref="IHierarchyConfigStore"/> the interactive path uses. It depends on
/// NONE of NavigationService / the foreground IPageAccessQueue / PageLoadPipeline
/// — a scheduled run never disturbs the user's foreground session.
/// </summary>
internal sealed class HeadlessSectionLoadAdapter : IHeadlessSectionLoader
{
    private readonly IPreloadService _preloadService;
    private readonly ILinkExtractor _linkExtractor;
    private readonly IHierarchyConfigStore _configStore;

    public HeadlessSectionLoadAdapter(
        IPreloadService preloadService,
        ILinkExtractor linkExtractor,
        IHierarchyConfigStore configStore)
    {
        _preloadService = preloadService;
        _linkExtractor = linkExtractor;
        _configStore = configStore;
    }

    public async Task<HeadlessSectionLoad> LoadLinksAndConfigAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        var load = await _preloadService.LoadRenderedHtmlAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
        if (load.Outcome != LoadOutcome.Ok || string.IsNullOrEmpty(load.Html))
        {
            return new HeadlessSectionLoad { Outcome = load.Outcome };
        }

        var finalUrl = string.IsNullOrEmpty(load.FinalUrl) ? sourceUrl : load.FinalUrl;
        var links = await _linkExtractor.ExtractLinksAsync(load.Html, finalUrl, cancellationToken).ConfigureAwait(false);
        var config = await _configStore.GetConfigAsync(sourceUrl).ConfigureAwait(false);

        return new HeadlessSectionLoad
        {
            Outcome = LoadOutcome.Ok,
            Links = links,
            Config = config,
        };
    }
}
