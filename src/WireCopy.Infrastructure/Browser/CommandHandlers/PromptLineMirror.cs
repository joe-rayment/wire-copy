// Licensed under the MIT License. See LICENSE in the repository root.

using System.Diagnostics.CodeAnalysis;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser.UI;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-up3zx: wraps a bottom-row ':' / '/' prompt so its prefix, typed
/// buffer and cursor are mirrored into the shell's HTML launcher / link-tree
/// pane while it covers the terminal (where the terminal-drawn prompt is
/// invisible). The wrapper only observes — the prompt itself runs unchanged.
/// </summary>
internal static class PromptLineMirror
{
    /// <summary>
    /// True when the prompt can be mirrored: the context exports prompt lines AND
    /// the input handler is the real <see cref="TerminalInputHandler"/> (the only
    /// one with an on-change prompt). Anything else — test substitutes, terminal
    /// mode without a shell — keeps its exact existing prompt call.
    /// </summary>
    /// <param name="ctx">The command context.</param>
    /// <param name="terminalInput">The terminal input handler when mirroring applies.</param>
    /// <returns>Whether the mirrored prompt path applies.</returns>
    public static bool CanMirror(CommandContext ctx, [NotNullWhen(true)] out TerminalInputHandler? terminalInput)
    {
        terminalInput = ctx.ExportPromptLine is not null ? ctx.InputHandler as TerminalInputHandler : null;
        return terminalInput is not null;
    }

    /// <summary>
    /// Runs <paramref name="runPrompt"/> with an on-change callback that pushes
    /// <c>{prefix, buffer, cursor}</c> through <paramref name="export"/>: once
    /// with an empty buffer when the prompt opens, again after every edit, and a
    /// final <c>null</c> when it closes (submit, Escape, or an exception).
    /// With a null <paramref name="export"/> the prompt runs with no callback.
    /// </summary>
    /// <param name="export">Pushes the prompt line to the pane; null disables mirroring.</param>
    /// <param name="prefix">The prompt's leading glyph (":" or "/").</param>
    /// <param name="runPrompt">Runs the real prompt, given the on-change callback.</param>
    /// <returns>Whatever the prompt returned (the typed text, or null when cancelled).</returns>
    public static async Task<string?> RunAsync(
        Func<PromptLineState?, Task>? export,
        string prefix,
        Func<Action<string, int>?, Task<string?>> runPrompt)
    {
        if (export is null)
        {
            return await runPrompt(null).ConfigureAwait(false);
        }

        // Fire and forget: the shell channel serialises writes in call order
        // (ShellChannel.RequestAsync takes its write lock synchronously), so the
        // pushes land in order without blocking the keystroke path.
        static void Fire(Func<PromptLineState?, Task> send, PromptLineState? line)
        {
            try
            {
                var task = send(line);
                _ = task.ContinueWith(
                    t => _ = t.Exception, // observe so a lost channel never surfaces as an unobserved fault
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
            catch (Exception)
            {
                // fails soft: a mirror problem must never break the prompt
            }
        }

        try
        {
            Fire(export, new PromptLineState { Prefix = prefix, Buffer = string.Empty, Cursor = 0 });
            return await runPrompt((buffer, cursor) =>
                Fire(export, new PromptLineState { Prefix = prefix, Buffer = buffer, Cursor = cursor })).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await export(null).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // fails soft
            }
        }
    }
}
