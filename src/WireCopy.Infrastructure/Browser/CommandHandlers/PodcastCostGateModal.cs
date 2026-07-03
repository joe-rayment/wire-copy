// Licensed under the MIT License. See LICENSE in the repository root.

using System.Globalization;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.DTOs.Podcast;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// One-line cost-gate confirm modal (workspace-lr80, Phase 2 of workspace-mhwa).
/// Appears between the cache analysis and the progress screen when the
/// estimated spend OR the article count exceeds <see cref="PodcastCostGateConfig"/>'s
/// thresholds. Below the thresholds the gate is silent — the user gets
/// one-keystroke generation that Phase 1 (workspace-kuu7) shipped.
///
/// <para>
/// Shape (one line, no full-screen takeover):
/// </para>
/// <code>
///   Generate 12 articles · est. $0.84 · ~6 min                       [Enter] go  [Esc] cancel
/// </code>
///
/// <para>
/// With cached articles dominating, the line reflects the actual TTS spend:
/// </para>
/// <code>
///   Generate 12 articles (8 cached · est. $0.18) · ~2 min            [Enter] go  [Esc] cancel
/// </code>
/// </summary>
internal static class PodcastCostGateModal
{
    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Roughly how many minutes the listener will get out of one uncached
    /// article on the default OpenAI TTS voice. Matches
    /// <c>OpenAiTtsService.MinutesPerArticle</c>'s rule-of-thumb so the
    /// estimated duration here lines up with the duration the orchestrator
    /// emits when generation actually runs.
    /// </summary>
    private const double MinutesPerArticleUncached = 3.5;

    /// <summary>
    /// Cached articles still take real wall-clock to upload + assemble — they
    /// don't synth, so they're cheaper, but they aren't free. ~0.5 min is a
    /// conservative read across the assembly + GCS upload phases.
    /// </summary>
    private const double MinutesPerArticleCached = 0.5;

    /// <summary>
    /// Decides whether the modal should appear; if yes, renders it and waits
    /// for Enter (proceed) / Esc (cancel). Returns true when the user
    /// accepts the cost OR when the gate is skipped entirely (small jobs).
    /// </summary>
    public static async Task<bool> ShowAsync(
        CommandContext ctx,
        RenderOptions options,
        CacheAnalysis analysis,
        PodcastCostGateConfig config,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(config);

        if (!config.ShouldShowGate(analysis))
        {
            return true;
        }

        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var summary = BuildSummaryLine(analysis);
        var hint = $"{palette.GetAccentFg().AnsiFg}[Enter]{Reset} {palette.PrimaryText.AnsiFg}go{Reset}  " +
                   $"{palette.GetAccentFg().AnsiFg}[Esc]{Reset} {palette.PrimaryText.AnsiFg}cancel{Reset}";
        var hintPlain = "[Enter] go  [Esc] cancel";

        try
        {
            // Paint the page once so the reading list shows behind the box.
            options = ctx.GetCurrentRenderOptions();
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            PodcastInlineBox.RenderBox(options, palette, summary, hint, hintPlain);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

                if (command.Type == CommandType.TerminalResized)
                {
                    options = ctx.GetCurrentRenderOptions();
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    PodcastInlineBox.RenderBox(options, palette, summary, hint, hintPlain);
                    continue;
                }

                if (command.Type == CommandType.ActivateLink)
                {
                    return true;
                }

                if (command.Type is CommandType.GoBack or CommandType.Quit)
                {
                    return false;
                }

                // Any other key — flash the border in the warning colour for
                // one frame (workspace-nahg item 4) so the user sees their
                // input was received but ignored, and the modal is still
                // awaiting Enter/Esc.
                await PodcastInlineBox.FlashInvalidKeyAsync(
                    options, palette, summary, hint, hintPlain, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // Blank the rows where the modal's box was painted so the next
            // RenderCurrentPageAsync starts from a clean state. WriteAt leaves
            // artifacts otherwise — the box would persist as ghost rows under
            // the reading list when the caller repaints.
            PodcastInlineBox.ClearBoxRows(options);
        }
    }

    internal static string BuildSummaryLine(CacheAnalysis analysis)
    {
        var totalArticles = analysis.TotalArticles;
        var cached = analysis.CachedArticles;
        var uncached = analysis.UncachedArticles;
        var costStr = analysis.EstimatedCost.ToString("0.00", CultureInfo.InvariantCulture);
        var minutes = (int)Math.Round((uncached * MinutesPerArticleUncached) + (cached * MinutesPerArticleCached));
        var durationStr = minutes <= 1 ? "~1 min" : $"~{minutes.ToString(CultureInfo.InvariantCulture)} min";

        var article = totalArticles == 1 ? "article" : "articles";

        // When cached articles dominate, surface the cache savings inline so
        // the user sees they're not actually paying for the whole list.
        if (cached > 0 && cached >= uncached)
        {
            return $"Generate {totalArticles} {article} ({cached} cached · est. ${costStr}) · {durationStr}";
        }

        return $"Generate {totalArticles} {article} · est. ${costStr} · {durationStr}";
    }
}
