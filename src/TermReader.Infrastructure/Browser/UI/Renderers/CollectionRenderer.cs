// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.Themes;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders collection list and collection items views with selection bars and separators.
/// </summary>
internal class CollectionRenderer
{
    private const string Reset = "\x1b[0m";
    private readonly RenderHelpers _helpers;
    private readonly IThemeProvider _themeProvider;
    private readonly PodcastCtaRenderer _podcastCtaRenderer;

    public CollectionRenderer(RenderHelpers helpers, IThemeProvider themeProvider)
    {
        _helpers = helpers;
        _themeProvider = themeProvider;
        _podcastCtaRenderer = new PodcastCtaRenderer(helpers, themeProvider);
    }

    public void RenderCollectionList(List<Collection> collections, int selectedIndex, Guid? defaultCollectionId, int scrollOffset, RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var width = Math.Max(1, options.TerminalWidth - 2);
        var height = options.TerminalHeight;

        // Rounded box header
        _helpers.WriteLine();
        _helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u256d{new string('\u2500', width - 2)}\u256e{Reset}");
        var title = RenderHelpers.TruncateText("Collections", width - 4);
        _helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u2502 {p.HeaderTitleFg.AnsiFg}{title.PadRight(width - 4)}{p.HeaderBorderFg.AnsiFg} \u2502{Reset}");
        _helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u2570{new string('\u2500', width - 2)}\u256f{Reset}");
        _helpers.WriteLine();

        var useSeparators = height >= 30;
        var linesPerItem = useSeparators ? 2 : 1;
        var remainingHeight = Math.Max(3, height - _helpers.LinesWritten - 3);
        var maxVisible = useSeparators ? (remainingHeight + 1) / linesPerItem : remainingHeight;

        if (collections.Count == 0)
        {
            _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}No collections yet. Use :new <name> to create one.{Reset}");
        }
        else
        {
            var startIndex = Math.Max(0, Math.Min(scrollOffset, collections.Count - maxVisible));
            var endIndex = Math.Min(collections.Count, startIndex + maxVisible);
            for (var i = startIndex; i < endIndex; i++)
            {
                var collection = collections[i];
                var isSelected = i == selectedIndex;
                var isDefault = defaultCollectionId.HasValue && collection.Id == defaultCollectionId.Value;

                var star = isDefault ? $" {p.PromptFg.AnsiFg}\u2605{Reset}" : string.Empty;
                var itemCount = collection.Items.Count;
                var countText = $"({itemCount})";

                var availableWidth = width - 6;
                var starLen = isDefault ? 2 : 0;
                var countLen = countText.Length + 1;

                string displayName;
                string countDisplay;

                if (availableWidth < 30)
                {
                    displayName = RenderHelpers.TruncateText(collection.Name, availableWidth - starLen);
                    countDisplay = string.Empty;
                }
                else if (collection.Name.Length + countLen + starLen > availableWidth)
                {
                    var nameWidth = Math.Max(15, availableWidth - countLen - starLen);
                    displayName = RenderHelpers.TruncateText(collection.Name, nameWidth);
                    countDisplay = $" {p.SecondaryText.AnsiFg}{countText}{Reset}";
                }
                else
                {
                    displayName = collection.Name;
                    countDisplay = $" {p.SecondaryText.AnsiFg}{countText}{Reset}";
                }

                if (isSelected)
                {
                    _helpers.WriteLine($"  {p.SelectedItemFg.AnsiFg}\u258c{Reset}\x1b[7m {displayName}{countDisplay}{star} \x1b[27m{Reset}");
                }
                else
                {
                    _helpers.WriteLine($"   {p.PrimaryText.AnsiFg}{displayName}{countDisplay}{star}{Reset}");
                }

                if (useSeparators && i < endIndex - 1)
                {
                    _helpers.WriteLine($"   {p.SecondaryText.AnsiFg}\u2576\u2500\u2500\u2574{Reset}");
                }
            }

            if (collections.Count > endIndex)
            {
                _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}... {collections.Count - endIndex} more collections{Reset}");
            }
        }
    }

    public void RenderCollectionItems(Collection collection, int selectedIndex, int scrollOffset, RenderOptions options)
    {
        var p = BuiltInThemes.Get(_themeProvider.CurrentTheme);
        var width = Math.Max(1, options.TerminalWidth - 2);
        var height = options.TerminalHeight;

        // Rounded box header with collection name and item count
        _helpers.WriteLine();
        _helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u256d{new string('\u2500', width - 2)}\u256e{Reset}");
        var itemCount = collection.Items.Count;
        var headerText = RenderHelpers.TruncateText($"{collection.Name} ({itemCount} item{(itemCount == 1 ? string.Empty : "s")})", width - 4);
        _helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u2502 {p.HeaderTitleFg.AnsiFg}{headerText.PadRight(width - 4)}{p.HeaderBorderFg.AnsiFg} \u2502{Reset}");
        _helpers.WriteLine($"{p.HeaderBorderFg.AnsiFg}\u2570{new string('\u2500', width - 2)}\u256f{Reset}");
        _helpers.WriteLine();

        // Podcast button (only shown when collection has items)
        if (collection.Items.Count > 0)
        {
            _podcastCtaRenderer.Render(options, (PodcastCtaState)options.PodcastButtonState);
        }

        var useSeparators = height >= 30;
        var linesPerItem = useSeparators ? 3 : 2;
        var remainingHeight = Math.Max(3, height - _helpers.LinesWritten - 3);
        var maxItems = Math.Max(1, (remainingHeight + 1) / linesPerItem);

        if (collection.Items.Count == 0)
        {
            _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}No items in this collection.{Reset}");
        }
        else
        {
            var startIndex = Math.Max(0, Math.Min(scrollOffset, collection.Items.Count - maxItems));
            var endIndex = Math.Min(collection.Items.Count, startIndex + maxItems);
            for (var i = startIndex; i < endIndex; i++)
            {
                var item = collection.Items[i];
                var isSelected = i == selectedIndex;

                var marker = item.IsRead
                    ? $"{p.ReadItemFg.AnsiFg}\u25cb{Reset}"
                    : $"{p.LinkContent.AnsiFg}\u25cf{Reset}";

                var domain = string.Empty;
                try
                {
                    var uri = new Uri(item.Url);
                    domain = uri.Host;
                }
                catch
                {
                    domain = item.Url;
                }

                var displayTitle = RenderHelpers.TruncateText(item.Title, width - 10);
                var displayDomain = RenderHelpers.TruncateText(domain, width - 10);

                if (isSelected)
                {
                    _helpers.WriteLine($"  {p.SelectedItemFg.AnsiFg}\u258c{Reset}\x1b[7m {marker} {displayTitle} \x1b[27m{Reset}");
                    _helpers.WriteLine($"  {p.SelectedItemFg.AnsiFg}\u258c{Reset}\x1b[7m     {displayDomain} \x1b[27m{Reset}");
                }
                else
                {
                    var titleColor = item.IsRead ? p.ReadItemFg.AnsiFg : p.PrimaryText.AnsiFg;
                    _helpers.WriteLine($"   {marker} {titleColor}{displayTitle}{Reset}");
                    _helpers.WriteLine($"       {p.SecondaryText.AnsiFg}{displayDomain}{Reset}");
                }

                if (useSeparators && i < endIndex - 1)
                {
                    _helpers.WriteLine($"     {p.SecondaryText.AnsiFg}\u2576\u2500\u2500\u2574{Reset}");
                }
            }

            if (collection.Items.Count > endIndex)
            {
                _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}... {collection.Items.Count - endIndex} more items below{Reset}");
            }
        }
    }

    internal static int GetCollectionListVisibleCount(int terminalHeight)
    {
        const int headerLines = 5;
        const int statusBarLines = 3;
        var useSeparators = terminalHeight >= 30;
        var linesPerItem = useSeparators ? 2 : 1;
        var remainingHeight = Math.Max(3, terminalHeight - headerLines - statusBarLines);
        return useSeparators ? (remainingHeight + 1) / linesPerItem : remainingHeight;
    }

    internal static int GetCollectionItemsVisibleCount(int terminalHeight, int terminalWidth = 80)
    {
        const int headerLines = 5;
        const int statusBarLines = 3;
        var podcastButtonLines = PodcastCtaRenderer.GetCtaLineCount(terminalWidth, terminalHeight);
        var useSeparators = terminalHeight >= 30;
        var linesPerItem = useSeparators ? 3 : 2;
        var remainingHeight = Math.Max(3, terminalHeight - headerLines - statusBarLines - podcastButtonLines);
        return Math.Max(1, (remainingHeight + 1) / linesPerItem);
    }
}
