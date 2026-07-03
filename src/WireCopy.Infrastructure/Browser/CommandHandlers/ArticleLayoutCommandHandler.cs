// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Manual regenerate of the per-domain article layout (workspace-2e1k).
/// Triggered by Shift+E in reader view: invokes <see cref="IAiArticleExtractor"/>
/// against the current page's HTML, validates the result via
/// <see cref="ISelectorBasedArticleExtractor"/> against the same quality gate
/// the persistence path uses, then merges the new entry into the saved
/// <see cref="ArticleSelectorConfig"/>. Replaces an existing entry by name;
/// appends otherwise.
/// </summary>
internal static class ArticleLayoutCommandHandler
{
    public static async Task HandleRegenerateAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        var navContext = ctx.NavigationService.CurrentContext;
        if (navContext.ViewMode != ViewMode.Readable || navContext.CurrentPage == null)
        {
            ctx.NavigationService.SetStatusMessage("Regenerate is only available in reader view");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var page = navContext.CurrentPage;
        var html = page.RawHtml ?? string.Empty;
        if (string.IsNullOrEmpty(html))
        {
            ctx.NavigationService.SetStatusMessage("No HTML available to regenerate");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        using var scope = ctx.ScopeFactory.CreateScope();
        var aiExtractor = scope.ServiceProvider.GetService<IAiArticleExtractor>();
        var selectorExtractor = scope.ServiceProvider.GetService<ISelectorBasedArticleExtractor>();
        var store = scope.ServiceProvider.GetService<IArticleLayoutStore>();

        if (aiExtractor == null || selectorExtractor == null || store == null)
        {
            ctx.NavigationService.SetStatusMessage("AI extractor unavailable");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        if (!aiExtractor.IsConfigured)
        {
            ctx.NavigationService.SetStatusMessage("OpenAI key not configured — see Setup");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        // workspace-wef6.5: the 30-60s AnalyzeAsync runs in the activity slot
        // (animated spinner) instead of a long-TTL status message.
        ctx.NavigationService.SetActivity("ai", "✨ regenerating article layout…", priority: 1);
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);

        ArticleSelectorConfig? candidate;
        try
        {
            candidate = await aiExtractor.AnalyzeAsync(page.Url, html, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // intentional: regenerate is best-effort, surface failure as toast
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ctx.Logger.LogWarning(ex, "Manual article-layout regeneration threw for {Url}", page.Url);
            ctx.NavigationService.SetStatusMessage("Regenerate failed — see logs", StatusSeverity.Error);
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }
        finally
        {
            ctx.NavigationService.ClearActivity("ai");
        }

        if (candidate == null || candidate.PageTypes.Count == 0)
        {
            ctx.NavigationService.SetStatusMessage("AI returned no layout — try again later");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var trial = selectorExtractor.Extract(candidate, page.Url, html);
        if (trial == null)
        {
            ctx.NavigationService.SetStatusMessage("AI selectors didn't match the page — kept old layout");
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            return;
        }

        var existing = await store.LoadAsync(candidate.Domain).ConfigureAwait(false);
        var merged = MergeByName(existing, candidate);
        await store.SaveAsync(merged).ConfigureAwait(false);

        ctx.NavigationService.SetStatusMessage(
            $"✓ Layout regenerated for {candidate.Domain} ({merged.PageTypes.Count} entries)");
        await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
    }

    internal static ArticleSelectorConfig MergeByName(
        ArticleSelectorConfig? existing,
        ArticleSelectorConfig fresh)
    {
        if (existing == null || existing.PageTypes.Count == 0)
        {
            return fresh;
        }

        var freshEntry = fresh.PageTypes[0];
        var entries = new List<PageTypeEntry>(existing.PageTypes);
        var idx = entries.FindIndex(e => string.Equals(e.Name, freshEntry.Name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            entries[idx] = freshEntry;
        }
        else
        {
            entries.Add(freshEntry);
        }

        return existing with
        {
            UpdatedAt = DateTime.UtcNow,
            PageTypes = entries,
        };
    }
}
