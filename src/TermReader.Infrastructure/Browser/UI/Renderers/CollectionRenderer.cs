// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders collection list and collection items views.
/// </summary>
internal class CollectionRenderer
{
    private readonly RenderHelpers _helpers;

    public CollectionRenderer(RenderHelpers helpers)
    {
        _helpers = helpers;
    }

    public void RenderCollectionList(List<Collection> collections, int selectedIndex, Guid? defaultCollectionId, RenderOptions options)
    {
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        _helpers.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        _helpers.WriteLine($"\u2554{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u2557");

        var title = RenderHelpers.TruncateText("Collections", width - 4);
        _helpers.WriteLine($"\u2551 {title.PadRight(width - 4)} \u2551");

        _helpers.WriteLine($"\u255a{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u255d");
        Console.ResetColor();
        _helpers.WriteLine();

        var remainingHeight = Math.Max(3, options.TerminalHeight - _helpers.LinesWritten - 3);

        if (collections.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            _helpers.WriteLine("  No collections yet. Use :new <name> to create one.");
            Console.ResetColor();
        }
        else
        {
            var startIndex = 0;
            if (selectedIndex >= remainingHeight)
            {
                startIndex = selectedIndex - remainingHeight + 1;
            }

            var endIndex = Math.Min(collections.Count, startIndex + remainingHeight);
            for (var i = startIndex; i < endIndex; i++)
            {
                var collection = collections[i];
                var isSelected = i == selectedIndex;
                var isDefault = defaultCollectionId.HasValue && collection.Id == defaultCollectionId.Value;

                var prefix = isSelected ? "\u2192" : " ";
                var star = isDefault ? " \u2605" : "";
                var itemCount = collection.Items.Count;
                var countText = $"({itemCount} item{(itemCount == 1 ? "" : "s")})";

                if (isSelected)
                {
                    Console.ForegroundColor = ConsoleColor.Black;
                    Console.BackgroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.White;
                }

                var displayName = RenderHelpers.TruncateText(collection.Name, options.MaxContentWidth - 20);
                _helpers.WriteLine($"  {prefix} {displayName} {countText}{star}");
                Console.ResetColor();
            }

            if (collections.Count > endIndex)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                _helpers.WriteLine($"  ... {collections.Count - endIndex} more collections");
                Console.ResetColor();
            }
        }
    }

    public void RenderCollectionItems(Collection collection, int selectedIndex, RenderOptions options)
    {
        var width = Math.Min(options.TerminalWidth, Console.WindowWidth - 2);

        _helpers.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        _helpers.WriteLine($"\u2554{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u2557");

        var itemCount = collection.Items.Count;
        var headerText = RenderHelpers.TruncateText($"{collection.Name} ({itemCount} item{(itemCount == 1 ? "" : "s")})", width - 4);
        _helpers.WriteLine($"\u2551 {headerText.PadRight(width - 4)} \u2551");

        _helpers.WriteLine($"\u255a{'\u2550'.ToString().PadRight(width - 2, '\u2550')}\u255d");
        Console.ResetColor();
        _helpers.WriteLine();

        var remainingHeight = Math.Max(3, options.TerminalHeight - _helpers.LinesWritten - 3);

        if (collection.Items.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            _helpers.WriteLine("  No items in this collection.");
            Console.ResetColor();
        }
        else
        {
            var maxItems = Math.Max(1, remainingHeight / 2);

            var startIndex = 0;
            if (selectedIndex >= maxItems)
            {
                startIndex = selectedIndex - maxItems + 1;
            }

            var endIndex = Math.Min(collection.Items.Count, startIndex + maxItems);
            for (var i = startIndex; i < endIndex; i++)
            {
                var item = collection.Items[i];
                var isSelected = i == selectedIndex;

                var prefix = isSelected ? "\u2192" : " ";
                var unreadMarker = item.IsRead ? " " : "\u2022";

                var domain = "";
                try
                {
                    var uri = new Uri(item.Url);
                    domain = uri.Host;
                }
                catch
                {
                    domain = item.Url;
                }

                if (isSelected)
                {
                    Console.ForegroundColor = ConsoleColor.Black;
                    Console.BackgroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.ForegroundColor = item.IsRead ? ConsoleColor.DarkGray : ConsoleColor.White;
                }

                var displayTitle = RenderHelpers.TruncateText(item.Title, options.MaxContentWidth - 10);
                _helpers.WriteLine($"  {prefix}{unreadMarker} {displayTitle}");

                Console.ResetColor();

                if (!isSelected)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                }

                var displayDomain = RenderHelpers.TruncateText(domain, options.MaxContentWidth - 10);
                _helpers.WriteLine($"      {displayDomain}");
                Console.ResetColor();
            }

            if (collection.Items.Count > endIndex)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                _helpers.WriteLine($"  ... {collection.Items.Count - endIndex} more items below");
                Console.ResetColor();
            }
        }
    }

    public void RenderCollectionStatusBar(ViewMode mode)
    {
        _helpers.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var separatorWidth = Math.Max(1, Console.WindowWidth - 1);
        _helpers.WriteLine(new string('\u2500', separatorWidth));

        Console.ForegroundColor = ConsoleColor.Yellow;

        var statusText = mode switch
        {
            ViewMode.CollectionList => "[Collections] j/k:move Enter:open s:set-default d:delete :new q:quit",
            ViewMode.CollectionItems => "[Items] j/k:move Enter:open d:remove J/K:reorder b:back :export q:quit",
            _ => "[Collections] q:quit"
        };

        _helpers.WriteLine(statusText);
        Console.ResetColor();
    }
}
