// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// ':layout reset' (workspace-8qyo): forgets the per-domain article-extraction
/// config (tuned + AI entries) so the next visit falls back to generic
/// readability — the escape hatch when a saved layout goes stale.
/// </summary>
internal static class ArticleLayoutResetHandler
{
    public static async Task HandleResetAsync(CommandContext ctx, RenderOptions options, CancellationToken ct)
    {
        var domain = ArticleLayoutDomains.FromUrl(ctx.NavigationService.CurrentPage?.Url);
        if (domain is null)
        {
            ctx.NavigationService.SetStatusMessage("Open a page on the site first");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        using var scope = ctx.ScopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetService<IArticleLayoutStore>();
        if (store == null)
        {
            ctx.NavigationService.SetStatusMessage("Layout store unavailable");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        // An empty entry list never matches — extraction falls back to generic.
        await store.SaveAsync(new ArticleSelectorConfig { Domain = domain, PageTypes = [] }).ConfigureAwait(false);
        ctx.NavigationService.SetStatusMessage($"Article layout reset for {domain}");
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }
}
