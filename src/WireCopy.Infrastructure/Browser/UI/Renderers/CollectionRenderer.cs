// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Browser;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Components;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Renders collection list and collection items views with selection bars and separators.
/// </summary>
internal class CollectionRenderer
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
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

        // BoxHeader
        var title = "Collections";
        var collCount = collections.Count;
        var subtitle = $"{collCount} collection{(collCount == 1 ? string.Empty : "s")}";
        var borderColor = p.GetDimFg().AnsiFg;
        var titleWidth = RenderHelpers.GetDisplayWidth(title);
        var subtitleWidth = RenderHelpers.GetDisplayWidth(subtitle);
        var boxWidth = Math.Max(titleWidth + 6, Math.Min(width - 2, 78));

        // visible cells = " " + "╭─ " + title + " " + dashes + "╮" = boxWidth + 3
        var titlePad = Math.Max(0, boxWidth - titleWidth - 3);
        _helpers.WriteLine(
            $" {borderColor}╭─ {Reset}{p.PrimaryText.AnsiFg}{Bold}{title}{Reset}" +
            $" {borderColor}{new string('─', titlePad)}╮{Reset}");

        // visible cells = " " + "│ " + subtitle + spaces + "│" = boxWidth + 3
        var subtitlePad = Math.Max(0, boxWidth - subtitleWidth - 1);
        _helpers.WriteLine(
            $" {borderColor}│ {Reset}{p.SecondaryText.AnsiFg}{subtitle}{new string(' ', subtitlePad)}{Reset}" +
            $"{borderColor}│{Reset}");

        _helpers.WriteLine(
            $" {borderColor}╰{new string('─', boxWidth)}╯{Reset}");

        // Status bar is 2 lines (separator + content row) at the bottom; reserve both.
        var remainingHeight = Math.Max(3, height - _helpers.LinesWritten - 2);
        var maxVisible = remainingHeight;

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

                var star = isDefault ? $" {p.SecondaryText.AnsiFg}{Indicators.Star}{Reset}" : string.Empty;
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
                    var plainCount = countDisplay.Length > 0 ? $" {countText}" : string.Empty;
                    var plainStar = isDefault ? $" {Indicators.Star}" : string.Empty;
                    var selectedText = $" {displayName}{plainCount}{plainStar} ";
                    var selectedPad = Math.Max(0, width - 3 - selectedText.Length);
                    _helpers.WriteLine($"  {Selection.SelectedAccentBar(p)}{Selection.Highlight(p, $"{selectedText}{new string(' ', selectedPad)}")}");
                }
                else
                {
                    _helpers.WriteLine($"   {p.PrimaryText.AnsiFg}{displayName}{countDisplay}{star}{Reset}");
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

        // BoxHeader
        var itemCount = collection.Items.Count;
        var title = $"{RenderHelpers.TruncateText(collection.Name, Math.Max(1, width / 2))} ({itemCount} item{(itemCount == 1 ? string.Empty : "s")})";
        var borderColor = p.GetDimFg().AnsiFg;
        var titleWidth = RenderHelpers.GetDisplayWidth(title);
        var boxWidth = Math.Max(titleWidth + 6, Math.Min(width - 2, 78));

        var titlePad = Math.Max(0, boxWidth - titleWidth - 3);
        _helpers.WriteLine(
            $" {borderColor}╭─ {Reset}{p.PrimaryText.AnsiFg}{Bold}{title}{Reset}" +
            $" {borderColor}{new string('─', titlePad)}╮{Reset}");

        var innerWidth = boxWidth - 1;
        _helpers.WriteLine(
            $" {borderColor}│ {Reset}{new string(' ', innerWidth)}{Reset}" +
            $"{borderColor}│{Reset}");

        _helpers.WriteLine(
            $" {borderColor}╰{new string('─', boxWidth)}╯{Reset}");

        // Podcast button (only shown when collection has items)
        if (collection.Items.Count > 0)
        {
            _podcastCtaRenderer.Render(options, (PodcastCtaState)options.PodcastButtonState, collection.Items.Count);
        }

        var isCompact = string.Equals(options.LayoutVariant, "Compact", StringComparison.Ordinal);

        // Status bar is 2 lines (separator + content row) at the bottom; reserve both.
        var remainingHeight = Math.Max(3, height - _helpers.LinesWritten - 2);

        // Each item takes linesPerItem lines, plus 1 separator line between items.
        // Total lines for N items = N * linesPerItem + (N-1) separators.
        // So N = (remainingHeight + 1) / (linesPerItem + 1).
        var maxItems = isCompact
            ? Math.Max(1, (remainingHeight + 1) / 2)
            : Math.Max(1, (remainingHeight + 1) / 3);

        if (collection.Items.Count == 0)
        {
            // workspace-wyxx.2: name the prerequisite AND the way back to it —
            // the user is inside the collection view here, so "go back to your
            // articles" is the missing step, not just the `s` key.
            _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Nothing saved yet — go back to your articles ({Reset}{p.GetAccentFg().AnsiFg}b{Reset}{p.SecondaryText.AnsiFg}) and press{Reset} {p.GetAccentFg().AnsiFg}s{Reset} {p.SecondaryText.AnsiFg}to add them here{Reset}");
        }
        else if (isCompact)
        {
            RenderCompactItems(collection, selectedIndex, scrollOffset, maxItems, width, options, p);
        }
        else
        {
            RenderStandardItems(collection, selectedIndex, scrollOffset, maxItems, width, options, p);
        }
    }

    internal static int GetCollectionListVisibleCount(int terminalHeight)
    {
        const int headerLines = 3;
        const int statusBarLines = 2;
        var remainingHeight = Math.Max(3, terminalHeight - headerLines - statusBarLines);
        return remainingHeight;
    }

    internal static int GetCollectionItemsVisibleCount(int terminalHeight, int terminalWidth = 80, bool isCompact = false)
    {
        const int headerLines = 3;
        const int statusBarLines = 2;
        var podcastButtonLines = PodcastCtaRenderer.GetCtaLineCount(terminalWidth, terminalHeight);
        var remainingHeight = Math.Max(3, terminalHeight - headerLines - statusBarLines - podcastButtonLines);
        if (isCompact)
        {
            // 1 line per item + 1 separator line between items
            return Math.Max(1, (remainingHeight + 1) / 2);
        }

        // 2 lines per item + 1 separator line between items
        return Math.Max(1, (remainingHeight + 1) / 3);
    }

    private static string ExtractDomain(string url)
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

    /// <summary>
    /// Renders collection items in standard 2-line LinkList card style:
    /// title in pink bold (HeaderTitleFg) on line 1 (or ReadItemFg if read),
    /// domain on line 2 in green secondary text (or dim if read), no leading dot.
    /// </summary>
    private void RenderStandardItems(
        Collection collection,
        int selectedIndex,
        int scrollOffset,
        int maxItems,
        int width,
        RenderOptions options,
        ThemePalette p)
    {
        var startIndex = Math.Max(0, Math.Min(scrollOffset, collection.Items.Count - maxItems));
        var endIndex = Math.Min(collection.Items.Count, startIndex + maxItems);
        for (var i = startIndex; i < endIndex; i++)
        {
            var item = collection.Items[i];
            var isSelected = i == selectedIndex;

            var domain = ExtractDomain(item.Url);

            var isCached = options.CachedUrls?.Contains(item.Url) == true;
            var cacheSuffix = isCached ? " · cached" : string.Empty;
            var domainMaxWidth = Math.Max(1, width - 8 - cacheSuffix.Length);

            var displayTitle = RenderHelpers.TruncateText(item.Title, width - 4);
            var displayDomain = RenderHelpers.TruncateText(domain, domainMaxWidth);

            if (isSelected)
            {
                // Selected: accent bar + highlight bg covering both title and domain lines (LinkList style).
                var contentWidth = width - 1;
                var titleFg = item.IsRead ? p.ReadItemFg.AnsiFg : p.SelectedItemFg.AnsiFg;
                var titlePad = Math.Max(0, contentWidth - 1 - displayTitle.Length);
                _helpers.WriteLine(
                    $" {p.HeaderBorderFg.AnsiFg}▌{Reset}" +
                    $"{p.SelectedItemBg.AnsiBg}{titleFg}{Bold} {displayTitle}{new string(' ', titlePad)}{Reset}");

                var plainCache = isCached ? " · cached" : string.Empty;
                var domainContent = $"{displayDomain}{plainCache}";
                var domainPad = Math.Max(0, contentWidth - 5 - domainContent.Length);
                _helpers.WriteLine(
                    $" {p.HeaderBorderFg.AnsiFg}▌{Reset}" +
                    $"{p.SelectedItemBg.AnsiBg}{p.SecondaryText.AnsiFg}     {domainContent}{new string(' ', domainPad)}{Reset}");
            }
            else
            {
                // Normal: title in pink bold (HeaderTitleFg), or ReadItemFg if read.
                // Domain on next line indented 5 chars in green secondary (or dim if read).
                var titleColor = item.IsRead ? p.ReadItemFg.AnsiFg : p.HeaderTitleFg.AnsiFg;
                var domainColor = item.IsRead ? p.GetDimFg().AnsiFg : p.SecondaryText.AnsiFg;
                _helpers.WriteLine($"  {titleColor}{Bold}{displayTitle}{Reset}");
                var cacheLabel = isCached ? $" {p.PromptFg.AnsiFg}· cached{Reset}" : string.Empty;
                _helpers.WriteLine($"       {domainColor}{displayDomain}{Reset}{cacheLabel}");
            }

            // Item separator between items (not after the last visible one)
            if (i < endIndex - 1)
            {
                _helpers.WriteLine(Borders.ItemSeparator(p, 6));
            }
        }

        if (collection.Items.Count > endIndex)
        {
            _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}... {collection.Items.Count - endIndex} more items below{Reset}");
        }
    }

    /// <summary>
    /// Renders collection items in compact single-line LinkList card style:
    /// title in pink bold (or dim if read), right-aligned domain in green/dim, no leading dot.
    /// </summary>
    private void RenderCompactItems(
        Collection collection,
        int selectedIndex,
        int scrollOffset,
        int maxItems,
        int width,
        RenderOptions options,
        ThemePalette p)
    {
        var startIndex = Math.Max(0, Math.Min(scrollOffset, collection.Items.Count - maxItems));
        var endIndex = Math.Min(collection.Items.Count, startIndex + maxItems);
        for (var i = startIndex; i < endIndex; i++)
        {
            var item = collection.Items[i];
            var isSelected = i == selectedIndex;
            var domain = ExtractDomain(item.Url);
            var isCached = options.CachedUrls?.Contains(item.Url) == true;
            var cacheTag = isCached ? " · cached" : string.Empty;

            // LinkList card style: no leading dot. Title left, domain right.
            // Unselected prefix: 2 spaces. Selected prefix: " ▌ " accent bar + space.
            var maxDomainWidth = Math.Min(domain.Length, Math.Max(8, width / 4));
            var displayDomain = RenderHelpers.TruncateText(domain, maxDomainWidth);
            var suffixLen = displayDomain.Length + cacheTag.Length;
            var titleMaxWidth = Math.Max(10, width - 4 - suffixLen);
            var displayTitle = RenderHelpers.TruncateText(item.Title, titleMaxWidth);

            // Padding between title and right-aligned domain
            var gap = Math.Max(1, width - 3 - displayTitle.Length - suffixLen);

            if (isSelected)
            {
                var contentWidth = width - 1;
                var titleFg = item.IsRead ? p.ReadItemFg.AnsiFg : p.SelectedItemFg.AnsiFg;
                var visibleLen = 1 + displayTitle.Length + gap + suffixLen;
                var trailingPad = Math.Max(0, contentWidth - visibleLen);
                _helpers.WriteLine(
                    $" {p.HeaderBorderFg.AnsiFg}▌{Reset}" +
                    $"{p.SelectedItemBg.AnsiBg}{titleFg}{Bold} {displayTitle}{Reset}" +
                    $"{p.SelectedItemBg.AnsiBg}{new string(' ', gap)}" +
                    $"{p.SecondaryText.AnsiFg}{displayDomain}{cacheTag}" +
                    $"{new string(' ', trailingPad)}{Reset}");
            }
            else
            {
                var titleColor = item.IsRead ? p.ReadItemFg.AnsiFg : p.HeaderTitleFg.AnsiFg;
                var domainColor = item.IsRead ? p.GetDimFg().AnsiFg : p.SecondaryText.AnsiFg;
                var cacheLabel = isCached ? $" {p.PromptFg.AnsiFg}· cached{Reset}" : string.Empty;
                _helpers.WriteLine($"  {titleColor}{Bold}{displayTitle}{Reset}{new string(' ', gap)}{domainColor}{displayDomain}{Reset}{cacheLabel}");
            }

            // Item separator between items (not after the last visible one)
            if (i < endIndex - 1)
            {
                _helpers.WriteLine(Borders.ItemSeparator(p, 6));
            }
        }

        if (collection.Items.Count > endIndex)
        {
            _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}... {collection.Items.Count - endIndex} more items below{Reset}");
        }
    }
}
