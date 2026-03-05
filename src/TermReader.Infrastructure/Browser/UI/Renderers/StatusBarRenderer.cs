// Educational and personal use only.

using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the general-purpose status bar for hierarchical and readable views.
/// </summary>
internal class StatusBarRenderer
{
    private readonly RenderHelpers _helpers;

    public StatusBarRenderer(RenderHelpers helpers)
    {
        _helpers = helpers;
    }

    public void RenderStatusBar(NavigationContext context, ViewMode mode)
    {
        _helpers.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var separatorWidth = Math.Max(1, Console.WindowWidth - 1);
        _helpers.WriteLine(new string('\u2500', separatorWidth));

        Console.ForegroundColor = ConsoleColor.Yellow;

        var statusText = mode switch
        {
            ViewMode.Hierarchical => "[LinkView] j/k:move h:collapse l:expand Enter:select v:reader /:search :cmd q:quit",
            ViewMode.Readable => "[ReaderView] j/k:scroll v:links b:back /:search :cmd q:quit",
            _ => "[Browser] q:quit"
        };

        if (!string.IsNullOrEmpty(context.SearchQuery))
        {
            statusText += $" | /{context.SearchQuery} (n/N)";
        }

        if (context.CanGoBack)
        {
            statusText = $"[\u2190back] {statusText}";
        }

        _helpers.WriteLine(statusText);
        Console.ResetColor();
    }
}
