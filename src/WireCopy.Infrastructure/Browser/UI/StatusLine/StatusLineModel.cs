// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.UI.Renderers;

namespace WireCopy.Infrastructure.Browser.UI.StatusLine;

/// <summary>
/// The composed, fully-resolved status line (workspace-wef6 B1). Everything
/// width-related has already been decided by <see cref="StatusComposer"/>:
/// which items survived, which variant each uses, and how much padding sits
/// between the left and right groups. The renderer just paints it.
/// </summary>
internal sealed record StatusLineModel
{
    /// <summary>Identity/context group rendered at the left edge (back arrow, view context).</summary>
    public required IReadOnlyList<StatusSegment[]> Left { get; init; }

    /// <summary>
    /// Adaptive hints rendered after the left group, in space nothing else
    /// claimed. Null when no tier fit.
    /// </summary>
    public StatusSegment[]? Hints { get; init; }

    /// <summary>Status group rendered right-aligned (alerts → transients → activity → ambient), in display order.</summary>
    public required IReadOnlyList<StatusSegment[]> Right { get; init; }

    /// <summary>Trailing help affordance ("?:help"); always last on the line.</summary>
    public StatusSegment[]? Help { get; init; }

    /// <summary>Spaces inserted between the left/hints block and the right block.</summary>
    public required int Padding { get; init; }

    /// <summary>Total width budget the model was composed for.</summary>
    public required int Width { get; init; }

    /// <summary>Plain-text projection of the full line — what the user sees, ANSI-free. Test seam.</summary>
    public string PlainText
    {
        get
        {
            var parts = new List<string>();
            foreach (var group in Left)
            {
                parts.Add(Plain(group));
            }

            if (Hints is { Length: > 0 })
            {
                parts.Add(Plain(Hints));
            }

            var leftText = string.Join(" ", parts.Where(s => s.Length > 0));
            var rightText = string.Join(" · ", Right.Select(Plain).Where(s => s.Length > 0));
            var helpText = Help is { Length: > 0 } ? Plain(Help) : string.Empty;

            return $"{leftText}{new string(' ', Padding)}{rightText}{(helpText.Length > 0 && rightText.Length > 0 ? " " : string.Empty)}{helpText}";
        }
    }

    /// <summary>Display width of one variant including nothing but its own runs.</summary>
    internal static int MeasureVariant(StatusSegment[] variant)
        => variant.Sum(s => RenderHelpers.GetDisplayWidth(s.Text));

    private static string Plain(StatusSegment[] segments)
        => string.Concat(segments.Select(s => s.Text));
}
