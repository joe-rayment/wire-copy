// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Components;

/// <summary>
/// Shared visual indicator characters and formatters.
/// </summary>
internal static class Indicators
{
    /// <summary>Filled circle — unread/active state.</summary>
    public const char FilledCircle = '\u25cf';

    /// <summary>Empty circle — read/inactive state.</summary>
    public const char EmptyCircle = '\u25cb';

    /// <summary>Collapse indicator: expanded (down-pointing triangle).</summary>
    public const char Expanded = '\u25bc';

    /// <summary>Collapse indicator: collapsed (right-pointing triangle).</summary>
    public const char Collapsed = '\u25b6';

    /// <summary>Selection arrow.</summary>
    public const char SelectionArrow = '\u2192';

    /// <summary>Checkmark.</summary>
    public const char Checkmark = '\u2714';

    /// <summary>Star marker for default collections.</summary>
    public const char Star = '\u2605';

    private const string Reset = "\x1b[0m";

    /// <summary>Empty track character — white parallelogram (U+25B1).</summary>
    private const char EmptyTrack = '\u25b1';

    // Eighth-block characters for smooth progress bars (from full to 1/8).
    private static readonly char[] EighthBlocks = [
        '\u2588', // █ Full block (8/8)
        '\u2589', // ▉ 7/8
        '\u258A', // ▊ 6/8
        '\u258B', // ▋ 5/8
        '\u258C', // ▌ 4/8
        '\u258D', // ▍ 3/8
        '\u258E', // ▎ 2/8
        '\u258F', // ▏ 1/8
    ];

    /// <summary>
    /// Renders a smooth progress bar using eighth-block Unicode characters.
    /// Uses WarningFg for active progress and MutedFg for the empty track.
    /// </summary>
    public static string ProgressBar(ThemePalette p, int filled, int total)
    {
        return RenderEighthBlockBar(
            p.GetWarningFg().AnsiFg,
            p.GetMutedFg().AnsiFg,
            (double)filled / Math.Max(1, total),
            total);
    }

    /// <summary>
    /// Renders a themed progress bar that changes color based on completion state:
    /// WarningFg/amber when in progress, SuccessFg when complete, MutedFg for empty track.
    /// </summary>
    public static string ProgressBar(ThemePalette p, int filled, int total, bool isComplete)
    {
        var filledColor = isComplete ? p.GetSuccessFg().AnsiFg : p.GetWarningFg().AnsiFg;
        return RenderEighthBlockBar(
            filledColor,
            p.GetMutedFg().AnsiFg,
            (double)filled / Math.Max(1, total),
            total);
    }

    /// <summary>
    /// Renders a smooth eighth-block progress bar for a given fraction and bar length.
    /// The empty portion renders as ▱ (U+25B1) characters in the empty color.
    /// </summary>
    internal static string RenderEighthBlockBar(string filledColor, string emptyColor, double fraction, int barLength)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        var totalEighths = fraction * barLength * 8;
        var fullBlocks = (int)(totalEighths / 8);
        var remainder = (int)(totalEighths % 8);

        var sb = new System.Text.StringBuilder();
        sb.Append(filledColor);
        sb.Append(new string(EighthBlocks[0], fullBlocks));

        if (remainder > 0 && fullBlocks < barLength)
        {
            sb.Append(EighthBlocks[8 - remainder]);
            sb.Append(Reset);
            sb.Append(emptyColor);
            sb.Append(new string(EmptyTrack, Math.Max(0, barLength - fullBlocks - 1)));
        }
        else
        {
            sb.Append(Reset);
            sb.Append(emptyColor);
            sb.Append(new string(EmptyTrack, Math.Max(0, barLength - fullBlocks)));
        }

        sb.Append(Reset);
        return sb.ToString();
    }
}
