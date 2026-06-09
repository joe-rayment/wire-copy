// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI;

/// <summary>
/// Dock-aware viewport for ABSOLUTE-POSITIONED overlays (workspace-s621):
/// confirmation dialogs, wizards, settings panels, and help popups that draw
/// via <see cref="Console.SetCursorPosition"/> with widths derived from
/// <see cref="Console.WindowWidth"/>. While the sidecar browser is docked over
/// part of the terminal, those overlays must render inside the UNCOVERED
/// columns — the same region the main views use — or they end up underneath
/// the browser window (unanswerable y/n prompts under a left dock).
///
/// <para>
/// Overlays read <see cref="Left"/>/<see cref="Width"/> instead of column 0 /
/// <c>Console.WindowWidth</c>. The provider is wired once by the orchestrator
/// to the dock geometry; without a provider (unit tests, standalone use) the
/// viewport is the full window, preserving pre-dock behaviour exactly.
/// </para>
/// </summary>
internal static class OverlayViewport
{
    private static Func<(int Left, int Width)>? _provider;

    /// <summary>First column overlays may draw in (0 unless docked-left).</summary>
    public static int Left => Current().Left;

    /// <summary>Columns available to overlays (full window unless docked).</summary>
    public static int Width => Current().Width;

    /// <summary>
    /// Installs the dock-aware geometry source. Pass null to restore the
    /// full-window fallback (used by tests).
    /// </summary>
    public static void SetProvider(Func<(int Left, int Width)>? provider) => _provider = provider;

    private static (int Left, int Width) Current()
    {
        var provider = _provider;
        if (provider != null)
        {
            try
            {
                return provider();
            }
            catch (Exception)
            {
                // Geometry must never break an overlay — fall through to full window.
            }
        }

        try
        {
            return (0, Console.WindowWidth);
        }
        catch (Exception)
        {
            return (0, 80);
        }
    }
}
