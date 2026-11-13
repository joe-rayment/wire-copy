// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.Themes;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>Minimal plain-text terminal drawing of a <see cref="TileScreen"/> (workspace-6ifpp.1).</summary>
internal static class TileScreenPainter
{
    internal static void Paint(RenderHelpers helpers, ThemePalette palette, TileScreen screen, RenderOptions options)
    {
        helpers.Clear();
        helpers.WriteLine(screen.Eyebrow);
        helpers.WriteLine(screen.Title);
        helpers.WriteLine(screen.Tagline);
        if (screen.BarPlaceholder != null)
        {
            helpers.WriteLine(screen.BarPlaceholder);
        }

        var items = TileGrid.Items(screen);
        for (var i = 0; i < items.Count; i++)
        {
            var rail = i == screen.Selected ? "▌ " : "  ";
            helpers.WriteLine($"{rail}[{i + 1}] {items[i].Title.ToUpperInvariant()} — {items[i].Hint}");
        }

        helpers.WriteLine(string.Join("  ", screen.Hints.Select(h => $"[{h.Key}]:{h.Label}")));
    }
}
