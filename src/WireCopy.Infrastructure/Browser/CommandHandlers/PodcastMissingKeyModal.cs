// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Pre-flight inline modal for the no-TTS-key case (workspace-yib5, Phase 5
/// of workspace-mhwa). Pops BEFORE cache analysis when the user presses
/// <c>p</c> without an OpenAI key — today the user would otherwise sit
/// through cache-analysis and the cost-gate before the confirmation screen
/// finally surfaces the "no key" state.
///
/// <para>
/// Shape (one box row, sits above the status bar like
/// <see cref="PodcastCostGateModal"/>):
/// </para>
/// <code>
///   OpenAI TTS API key required                          [s] set up now  [Esc] back
/// </code>
/// </summary>
internal static class PodcastMissingKeyModal
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";

    /// <summary>
    /// Outcome of the modal — drives the resume-or-cancel branch back in
    /// <see cref="PodcastCommandHandler.RunGeneratePodcastAttempt"/>.
    /// </summary>
    internal enum Outcome
    {
        /// <summary>User pressed <c>s</c> — open the API-key prompt and, on save success, resume generation.</summary>
        SetUpNow,

        /// <summary>User pressed Esc / GoBack / Quit — abort the generation flow.</summary>
        Cancel,
    }

    /// <summary>
    /// Renders the inline modal and waits for <c>s</c> (set up now) or Esc
    /// (cancel). Any other key re-paints the modal so the user sees their
    /// input was ignored and the prompt is still active.
    /// </summary>
    public static async Task<Outcome> ShowAsync(
        CommandContext ctx,
        RenderOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var palette = BuiltInThemes.Get(ctx.ThemeProvider.CurrentTheme);
        var summary = "OpenAI TTS API key required";
        var hint = $"{palette.GetAccentFg().AnsiFg}[s]{Reset} {palette.PrimaryText.AnsiFg}set up now{Reset}  " +
                   $"{palette.GetAccentFg().AnsiFg}[Esc]{Reset} {palette.PrimaryText.AnsiFg}back{Reset}";
        var hintPlain = "[s] set up now  [Esc] back";

        try
        {
            options = ctx.GetCurrentRenderOptions();
            await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
            RenderBox(options, palette, summary, hint, hintPlain);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var command = await ctx.InputHandler.WaitForInputAsync(ct).ConfigureAwait(false);

                if (command.Type == CommandType.TerminalResized)
                {
                    options = ctx.GetCurrentRenderOptions();
                    await ctx.RenderCurrentPageAsync(options, ct).ConfigureAwait(false);
                    RenderBox(options, palette, summary, hint, hintPlain);
                    continue;
                }

                if (command.Type is CommandType.GoBack or CommandType.Quit)
                {
                    return Outcome.Cancel;
                }

                if (command.RawKeyChar is 's' or 'S')
                {
                    return Outcome.SetUpNow;
                }

                RenderBox(options, palette, summary, hint, hintPlain);
            }
        }
        finally
        {
            ClearBoxRows(options);
        }
    }

    private static void RenderBox(
        RenderOptions options,
        ThemePalette palette,
        string summary,
        string hint,
        string hintPlain)
    {
        var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
        var width = Math.Max(60, Math.Min(options.TerminalWidth - 4, 120));
        var boxWidth = width;

        var leftPad = Math.Max(0, (options.TerminalWidth - boxWidth) / 2);
        var pad = new string(' ', leftPad);
        var borderFg = palette.HeaderBorderFg.AnsiFg;

        var contentWidth = boxWidth - 4;
        var summaryWidth = RenderHelpers.GetDisplayWidth(summary);
        var hintWidth = RenderHelpers.GetDisplayWidth(hintPlain);
        var gapWidth = Math.Max(2, contentWidth - summaryWidth - hintWidth);

        var styledSummary = $"{Bold}{palette.PrimaryText.AnsiFg}{summary}{Reset}";

        var topRow = Math.Max(0, options.TerminalHeight - 5);
        helpers.WriteAt(0, topRow, $"{pad}{borderFg}╭{new string('─', boxWidth - 2)}╮{Reset}");
        helpers.WriteAt(0, topRow + 1, $"{pad}{borderFg}│{Reset} {styledSummary}{new string(' ', gapWidth)}{hint} {borderFg}│{Reset}");
        helpers.WriteAt(0, topRow + 2, $"{pad}{borderFg}╰{new string('─', boxWidth - 2)}╯{Reset}");
    }

    private static void ClearBoxRows(RenderOptions options)
    {
        var helpers = new RenderHelpers { TerminalHeight = options.TerminalHeight };
        var topRow = Math.Max(0, options.TerminalHeight - 5);
        var blank = new string(' ', Math.Max(1, options.TerminalWidth));
        for (var row = topRow; row < Math.Min(options.TerminalHeight - 2, topRow + 3); row++)
        {
            helpers.WriteAt(0, row, blank);
        }
    }
}
