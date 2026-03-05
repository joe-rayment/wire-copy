// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Entities.Bookmarks;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders the launcher home screen with bookmark tiles and status bar.
/// </summary>
internal class LauncherRenderer
{
    private readonly RenderHelpers _helpers;

    public LauncherRenderer(RenderHelpers helpers)
    {
        _helpers = helpers;
    }

    public void RenderLauncher(List<Bookmark> bookmarks, int selectedIndex, int scrollOffset, RenderOptions options)
    {
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        // Header
        _helpers.WriteLine();
        var headerLine = "\x1b[36m\u2554" + new string('\u2550', width - 2) + "\u2557\x1b[0m";
        _helpers.WriteLine(headerLine);

        var title = RenderHelpers.TruncateText("TermReader", width - 4);
        var titleLine = $"\x1b[36m\u2551 \x1b[37m{title.PadRight(width - 4)}\x1b[36m \u2551\x1b[0m";
        _helpers.WriteLine(titleLine);

        var bottomLine = "\x1b[36m\u255a" + new string('\u2550', width - 2) + "\u255d\x1b[0m";
        _helpers.WriteLine(bottomLine);
        _helpers.WriteLine();

        // Calculate grid dimensions
        var headerLines = _helpers.LinesWritten;
        var statusBarLines = 3;
        var availableHeight = Math.Max(6, options.TerminalHeight - headerLines - statusBarLines);

        var columns = width < 35 ? 1 : 2;
        var gap = 1;
        var margin = 1;
        var tileWidth = columns == 1 ? width - (margin * 2) : (width - (margin * 2) - gap) / 2;
        var tileHeight = Math.Max(3, availableHeight / 6);

        var totalItems = bookmarks.Count + 1; // +1 for Collections tile

        var visibleRows = Math.Max(1, availableHeight / tileHeight);
        var startRow = scrollOffset;
        var totalRows = (totalItems + columns - 1) / columns;

        if (bookmarks.Count == 0 && totalItems == 1)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            _helpers.WriteLine("  No bookmarks. Press 'a' to add one.");
            Console.ResetColor();
            _helpers.WriteLine();
        }

        for (var row = startRow; row < Math.Min(startRow + visibleRows, totalRows); row++)
        {
            var leftIdx = row * columns;
            var rightIdx = columns == 2 ? leftIdx + 1 : -1;

            var leftBookmark = leftIdx < bookmarks.Count ? bookmarks[leftIdx] : null;
            var rightBookmark = rightIdx >= 0 && rightIdx < bookmarks.Count ? bookmarks[rightIdx] : null;

            var leftIsCollections = leftIdx == bookmarks.Count;
            var rightIsCollections = rightIdx == bookmarks.Count;

            var leftSelected = leftIdx == selectedIndex;
            var rightSelected = rightIdx >= 0 && rightIdx == selectedIndex;

            for (var line = 0; line < tileHeight; line++)
            {
                var leftStr = BuildTileLine(leftBookmark, leftSelected, leftIsCollections, tileWidth, tileHeight, line);
                var sb = new System.Text.StringBuilder();
                sb.Append(new string(' ', margin));
                sb.Append(leftStr);

                if (columns == 2)
                {
                    sb.Append(new string(' ', gap));

                    if (rightIdx >= 0 && rightIdx < totalItems)
                    {
                        var rightStr = BuildTileLine(rightBookmark, rightSelected, rightIsCollections, tileWidth, tileHeight, line);
                        sb.Append(rightStr);
                    }
                }

                sb.Append("\x1b[0m");
                _helpers.WriteLine(sb.ToString());
            }
        }

        if (totalRows > startRow + visibleRows)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var remaining = totalRows - startRow - visibleRows;
            _helpers.WriteLine($"  ... {remaining} more row{(remaining == 1 ? "" : "s")} below");
            Console.ResetColor();
        }
    }

    public void RenderLauncherStatusBar()
    {
        _helpers.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var separatorWidth = Math.Max(1, Console.WindowWidth - 1);
        _helpers.WriteLine(new string('\u2500', separatorWidth));

        Console.ForegroundColor = ConsoleColor.Yellow;
        _helpers.WriteLine("[Launcher] h/j/k/l:navigate Enter:open a:add d:delete c:collections :cmd q:quit");
        Console.ResetColor();
    }

    internal static string BuildTileLine(Bookmark? bookmark, bool selected, bool isCollections, int tileWidth, int tileHeight, int lineIdx)
    {
        var innerWidth = Math.Max(1, tileWidth - 2);

        string name;
        string domain;
        string borderColor;

        if (isCollections)
        {
            name = "Collections";
            domain = "saved links";
            borderColor = "\x1b[36m";
        }
        else if (bookmark != null)
        {
            name = bookmark.Name;
            domain = ExtractDomain(bookmark.Url);
            borderColor = "\x1b[36m";
        }
        else
        {
            return new string(' ', tileWidth);
        }

        var nameLineIdx = Math.Max(1, (tileHeight - 2) / 2);
        var domainLineIdx = nameLineIdx + 1;

        if (selected)
        {
            var sel = "\x1b[30;47m";
            var reset = "\x1b[0m";

            if (lineIdx == 0)
            {
                return $"{sel}\u2554{new string('\u2550', innerWidth)}\u2557{reset}";
            }

            if (lineIdx == tileHeight - 1)
            {
                return $"{sel}\u255a{new string('\u2550', innerWidth)}\u255d{reset}";
            }

            if (lineIdx == nameLineIdx)
            {
                var truncName = RenderHelpers.TruncateText(name, innerWidth - 2);
                return $"{sel}\u2551 {truncName.PadRight(innerWidth - 1)}\u2551{reset}";
            }

            if (lineIdx == domainLineIdx)
            {
                var truncDomain = RenderHelpers.TruncateText(domain, innerWidth - 2);
                return $"{sel}\u2551 {truncDomain.PadRight(innerWidth - 1)}\u2551{reset}";
            }

            return $"{sel}\u2551{new string(' ', innerWidth)}\u2551{reset}";
        }
        else
        {
            var reset = "\x1b[0m";

            if (lineIdx == 0)
            {
                return $"{borderColor}\u250c{new string('\u2500', innerWidth)}\u2510{reset}";
            }

            if (lineIdx == tileHeight - 1)
            {
                return $"{borderColor}\u2514{new string('\u2500', innerWidth)}\u2518{reset}";
            }

            if (lineIdx == nameLineIdx)
            {
                var truncName = RenderHelpers.TruncateText(name, innerWidth - 2);
                return $"{borderColor}\u2502\x1b[37m {truncName.PadRight(innerWidth - 1)}{borderColor}\u2502{reset}";
            }

            if (lineIdx == domainLineIdx)
            {
                var truncDomain = RenderHelpers.TruncateText(domain, innerWidth - 2);
                return $"{borderColor}\u2502\x1b[90m {truncDomain.PadRight(innerWidth - 1)}{borderColor}\u2502{reset}";
            }

            return $"{borderColor}\u2502{new string(' ', innerWidth)}\u2502{reset}";
        }
    }

    internal static string ExtractDomain(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            return url;
        }
    }
}
