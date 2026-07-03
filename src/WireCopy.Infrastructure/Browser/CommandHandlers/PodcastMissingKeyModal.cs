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
                   $"{palette.GetAccentFg().AnsiFg}[Esc]{Reset} {palette.PrimaryText.AnsiFg}cancel{Reset}";
        var hintPlain = "[s] set up now  [Esc] cancel";

        try
        {
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

                if (command.Type is CommandType.GoBack or CommandType.Quit)
                {
                    return Outcome.Cancel;
                }

                if (command.RawKeyChar is 's' or 'S')
                {
                    return Outcome.SetUpNow;
                }

                // workspace-nahg item 4: flash the border in the warning
                // colour so the ignored keystroke gets visible feedback.
                await PodcastInlineBox.FlashInvalidKeyAsync(
                    options, palette, summary, hint, hintPlain, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            PodcastInlineBox.ClearBoxRows(options);
        }
    }
}
