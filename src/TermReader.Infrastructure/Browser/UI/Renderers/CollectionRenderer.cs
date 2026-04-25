// Educational and personal use only.

using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Collections;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Components;

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

        // BoxHeader
        var title = "Collections";
        var collCount = collections.Count;
        var subtitle = $"{collCount} collection{(collCount == 1 ? string.Empty : "s")}";
        var borderColor = p.GetDimFg().AnsiFg;
        var boxWidth = Math.Max(title.Length + 6, Math.Min(width - 2, 78));

        var titlePad = Math.Max(0, boxWidth - title.Length - 5);
        _helpers.WriteLine(
            $" {borderColor}\u256d\u2500 {Reset}{p.PrimaryText.AnsiFg}\x1b[1m{title}{Reset}" +
            $" {borderColor}{new string('\u2500', titlePad)}\u256e{Reset}");

        var innerWidth = boxWidth - 2;
        var subtitlePad = Math.Max(0, innerWidth - subtitle.Length);
        _helpers.WriteLine(
            $" {borderColor}\u2502 {Reset}{p.SecondaryText.AnsiFg}{subtitle}{new string(' ', subtitlePad)}{Reset}" +
            $"{borderColor}\u2502{Reset}");

        _helpers.WriteLine(
            $" {borderColor}\u2570{new string('\u2500', boxWidth)}\u256f{Reset}");

        var remainingHeight = Math.Max(3, height - _helpers.LinesWritten - 1);
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
        var boxWidth = Math.Max(title.Length + 6, Math.Min(width - 2, 78));

        var titlePad = Math.Max(0, boxWidth - title.Length - 5);
        _helpers.WriteLine(
            $" {borderColor}\u256d\u2500 {Reset}{p.PrimaryText.AnsiFg}\x1b[1m{title}{Reset}" +
            $" {borderColor}{new string('\u2500', titlePad)}\u256e{Reset}");

        var innerWidth = boxWidth - 2;
        _helpers.WriteLine(
            $" {borderColor}\u2502 {Reset}{new string(' ', innerWidth)}{Reset}" +
            $"{borderColor}\u2502{Reset}");

        _helpers.WriteLine(
            $" {borderColor}\u2570{new string('\u2500', boxWidth)}\u256f{Reset}");

        // Podcast button (only shown when collection has items)
        if (collection.Items.Count > 0)
        {
            _podcastCtaRenderer.Render(options, (PodcastCtaState)options.PodcastButtonState);
        }

        var isCompact = string.Equals(options.LayoutVariant, "Compact", StringComparison.Ordinal);
        var linesPerItem = isCompact ? 1 : 2;
        var remainingHeight = Math.Max(3, height - _helpers.LinesWritten - 1);
        var maxItems = isCompact
            ? remainingHeight
            : Math.Max(1, (remainingHeight + 1) / linesPerItem);

        if (collection.Items.Count == 0)
        {
            _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}Nothing saved yet \u2014 press{Reset} {p.GetAccentFg().AnsiFg}s{Reset} {p.SecondaryText.AnsiFg}on any article to start your list{Reset}");
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
            return remainingHeight;
        }

        const int linesPerItem = 2;
        return Math.Max(1, (remainingHeight + 1) / linesPerItem);
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

            var marker = item.IsRead
                ? $"{p.ReadItemFg.AnsiFg}{Indicators.EmptyCircle}{Reset}"
                : $"{p.LinkContent.AnsiFg}{Indicators.FilledCircle}{Reset}";

            var domain = ExtractDomain(item.Url);

            var isCached = options.CachedUrls?.Contains(item.Url) == true;
            var cacheSuffix = isCached ? " \u00b7 cached" : string.Empty;
            var domainMaxWidth = Math.Max(1, width - 10 - cacheSuffix.Length);

            var displayTitle = RenderHelpers.TruncateText(item.Title, width - 10);
            var displayDomain = RenderHelpers.TruncateText(domain, domainMaxWidth);

            if (isSelected)
            {
                var markerChar = item.IsRead ? $"{Indicators.EmptyCircle}" : $"{Indicators.FilledCircle}";
                var selectedPad = Math.Max(0, width - 6 - displayTitle.Length);
                var plainCache = isCached ? " \u00b7 cached" : string.Empty;
                var domainPad = Math.Max(0, width - 9 - displayDomain.Length - plainCache.Length);
                _helpers.WriteLine($"  {Selection.SelectedAccentBar(p)}{Selection.Highlight(p, $" {markerChar} {displayTitle}{new string(' ', selectedPad)} ")}");
                _helpers.WriteLine($"  {Selection.SelectedAccentBar(p)}{Selection.Highlight(p, $"     {displayDomain}{plainCache}{new string(' ', domainPad)} ")}");
            }
            else
            {
                var titleColor = item.IsRead ? p.ReadItemFg.AnsiFg : p.PrimaryText.AnsiFg;
                _helpers.WriteLine($"   {marker} {titleColor}{displayTitle}{Reset}");
                var cacheLabel = isCached ? $" {p.PromptFg.AnsiFg}\u00b7 cached{Reset}" : string.Empty;
                _helpers.WriteLine($"       {p.SecondaryText.AnsiFg}{displayDomain}{Reset}{cacheLabel}");
            }
        }

        if (collection.Items.Count > endIndex)
        {
            _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}... {collection.Items.Count - endIndex} more items below{Reset}");
        }
    }

    /// <summary>
    /// Renders collection items in compact single-line format:
    /// marker + title + right-aligned domain on the same line.
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
            var cacheTag = isCached ? " \u00b7 cached" : string.Empty;

            // Layout: "  ▌ ● Title...          domain · cached "
            // Prefix takes 5 chars: "  ▌ ● " (2 spaces + accent bar + space + marker + space)
            // Suffix: " domain · cached " (space + domain + cache + space)
            // Available for title: width - 5 (prefix) - domain.Length - cacheTag.Length - 2 (spacing)
            var maxDomainWidth = Math.Min(domain.Length, Math.Max(8, width / 4));
            var displayDomain = RenderHelpers.TruncateText(domain, maxDomainWidth);
            var suffixLen = displayDomain.Length + cacheTag.Length;
            var titleMaxWidth = Math.Max(10, width - 7 - suffixLen);
            var displayTitle = RenderHelpers.TruncateText(item.Title, titleMaxWidth);

            // Padding between title and right-aligned domain
            var gap = Math.Max(1, width - 6 - displayTitle.Length - suffixLen);

            if (isSelected)
            {
                var markerChar = item.IsRead ? $"{Indicators.EmptyCircle}" : $"{Indicators.FilledCircle}";
                var lineContent = $" {markerChar} {displayTitle}{new string(' ', gap)}{displayDomain}{cacheTag} ";
                _helpers.WriteLine($"  {Selection.SelectedAccentBar(p)}{Selection.Highlight(p, lineContent)}");
            }
            else
            {
                var marker = item.IsRead
                    ? $"{p.ReadItemFg.AnsiFg}{Indicators.EmptyCircle}{Reset}"
                    : $"{p.LinkContent.AnsiFg}{Indicators.FilledCircle}{Reset}";
                var titleColor = item.IsRead ? p.ReadItemFg.AnsiFg : p.PrimaryText.AnsiFg;
                var cacheLabel = isCached ? $" {p.PromptFg.AnsiFg}\u00b7 cached{Reset}" : string.Empty;
                _helpers.WriteLine($"   {marker} {titleColor}{displayTitle}{Reset}{new string(' ', gap)}{p.SecondaryText.AnsiFg}{displayDomain}{Reset}{cacheLabel}");
            }
        }

        if (collection.Items.Count > endIndex)
        {
            _helpers.WriteLine($"  {p.SecondaryText.AnsiFg}... {collection.Items.Count - endIndex} more items below{Reset}");
        }
    }
}
