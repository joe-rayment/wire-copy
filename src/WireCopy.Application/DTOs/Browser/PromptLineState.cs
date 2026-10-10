// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Browser;

/// <summary>
/// The terminal's bottom-row command prompt (<c>:</c> command line, <c>/</c>
/// search) mirrored into the shell's HTML launcher / link-tree pane state
/// (workspace-up3zx). While an HTML pane covers the terminal, the prompt the
/// terminal draws underneath it is invisible, so the same prefix + typed text +
/// cursor ride along on the pane's state and the pane draws its own prompt row.
/// A null <c>promptLine</c> on the pane state means no prompt is active.
/// Field names are camelCase on the wire.
/// </summary>
public sealed record PromptLineState
{
    /// <summary>The prompt's leading glyph, e.g. <c>":"</c> or <c>"/"</c>.</summary>
    public required string Prefix { get; init; }

    /// <summary>The text typed so far (after the prefix).</summary>
    public string Buffer { get; init; } = "";

    /// <summary>Caret position within <see cref="Buffer"/> (0..Buffer.Length).</summary>
    public int Cursor { get; init; }
}
