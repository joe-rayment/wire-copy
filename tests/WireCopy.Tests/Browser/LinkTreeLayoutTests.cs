// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.Interfaces.Browser;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class LinkTreeLayoutTests
{
    private static readonly ThemePalette TestPalette = BuiltInThemes.Get(ThemeName.Phosphor);

    #region GetCardHeight

    [Theory]
    [InlineData(3, 1)]
    [InlineData(5, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(15, 2)]
    [InlineData(24, 2)]
    [InlineData(25, 3)]
    [InlineData(40, 3)]
    [InlineData(100, 3)]
    public void GetCardHeight_ReturnsCorrectValue(int availableLines, int expected)
    {
        LinkTreeRenderer.GetCardHeight(availableLines).Should().Be(expected);
    }

    [Fact]
    public void GetCardHeight_BoundaryAtTen_ReturnsTwo()
    {
        LinkTreeRenderer.GetCardHeight(10).Should().Be(2);
        LinkTreeRenderer.GetCardHeight(9).Should().Be(1);
    }

    [Fact]
    public void GetCardHeight_BoundaryAtTwentyFive_ReturnsThree()
    {
        LinkTreeRenderer.GetCardHeight(25).Should().Be(3);
        LinkTreeRenderer.GetCardHeight(24).Should().Be(2);
    }

    #endregion

    #region ComputeLayout - 2-column grid

    [Fact]
    public void ComputeLayout_Width80_ReturnsTwoColumns()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        layout.Columns.Should().Be(2);
    }

    [Fact]
    public void ComputeLayout_Width40_StaysTwoColumns()
    {
        // workspace-21uy: a sidecar-docked narrow terminal must NOT collapse to 1.
        var layout = LinkTreeRenderer.ComputeLayout(40, 24);
        layout.Columns.Should().Be(2);
    }

    [Fact]
    public void CellWidth_IsHalfMinusOneForTwoColumns()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        // For 2 columns: (width-1)/2
        layout.CellWidth.Should().Be((layout.Width - 1) / 2);
    }

    [Fact]
    public void CellWidth_NarrowTerminal_IsHalfMinusDivider()
    {
        var layout = LinkTreeRenderer.ComputeLayout(40, 24);
        layout.Columns.Should().Be(2);
        layout.CellWidth.Should().Be((layout.Width - 1) / 2);
    }

    // Fixed 2-column grid (workspace-21uy): wide windows get WIDER tiles, never more
    // columns; narrow (sidecar-docked) windows get narrower tiles, never fewer.
    [Fact]
    public void ComputeLayout_Width160_StaysTwoColumns()
    {
        var layout = LinkTreeRenderer.ComputeLayout(160, 40);
        layout.Columns.Should().Be(2);
    }

    [Fact]
    public void ComputeLayout_UltraWide_StaysTwoColumns()
    {
        var layout = LinkTreeRenderer.ComputeLayout(212, 40);
        layout.Columns.Should().Be(2);
    }

    [Fact]
    public void CellHeight_IsCompactWhenShort()
    {
        // availableHeight = max(4, 18 - 3 - 2) = 13 < 15 → compact (3)
        var layout = LinkTreeRenderer.ComputeLayout(80, 18);
        layout.CellHeight.Should().Be(3);
    }

    [Fact]
    public void CellHeight_IsStandardWhenTall()
    {
        // availableHeight = max(4, 30 - 3 - 2) = 25 >= 15 → standard (5)
        var layout = LinkTreeRenderer.ComputeLayout(80, 30);
        layout.CellHeight.Should().Be(5);
    }

    [Fact]
    public void VisibleRows_CalculatedFromAvailableHeight()
    {
        // availableHeight = max(4, 30 - 3 - 2) = 25, cellHeight = 5 → 25/5 = 5
        var layout = LinkTreeRenderer.ComputeLayout(80, 30);
        layout.VisibleRows.Should().Be(5);
    }

    [Fact]
    public void ComputeLayout_HeaderAndStatusBarLines()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        layout.HeaderLines.Should().Be(3);
        layout.StatusBarLines.Should().Be(2);
    }

    #endregion

    #region BuildCardLine - Normal state

    [Fact]
    public void BuildCardLine_NormalTitle_ContainsDisplayText()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 2, 0, 80, TestPalette);

        line.Should().Contain("My Article");
    }

    [Fact]
    public void BuildCardLine_NormalMetadata_ContainsAuthorText()
    {
        var node = CreateLinkNodeWithMetadata("My Article", "https://example.com/article", LinkType.Content, "Jane Doe", null);

        // cardHeight=3 (compact): metadata line is at index 1
        var line = LinkTreeRenderer.BuildCardLine(node, false, 3, 1, 80, TestPalette);

        line.Should().Contain("Jane Doe");
    }

    [Fact]
    public void BuildCardLine_NormalMetadata_UsesSecondaryTextColor()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 3, 1, 80, TestPalette);

        // Metadata uses SecondaryText color (no Dim modifier for readability)
        line.Should().Contain(TestPalette.SecondaryText.AnsiFg);
        line.Should().NotContain("\x1b[2m"); // No Dim
    }

    [Fact]
    public void BuildCardLine_CompactMode_NoDomainLine()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 1, 1, 80, TestPalette);

        // cardHeight=1, lineIndex=1 is a blank separator padded to full width
        line.Should().HaveLength(80).And.Match(s => s.Trim().Length == 0);
    }

    [Fact]
    public void BuildCardLine_ExpandedMode_ThirdLineIsSeparator()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 3, 2, 80, TestPalette);

        line.Should().Contain("\u2500"); // horizontal rule separator
    }

    #endregion

    #region BuildCardLine - Selected state

    [Fact]
    public void BuildCardLine_Selected_ContainsAccentBar()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, true, 2, 0, 80, TestPalette);

        line.Should().Contain("\u258c"); // ▌ accent bar
    }

    [Fact]
    public void BuildCardLine_Selected_ContainsBold()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, true, 2, 0, 80, TestPalette);

        line.Should().Contain("\x1b[1m"); // Bold
    }

    [Fact]
    public void BuildCardLine_Selected_ContainsSelectedItemBg()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, true, 2, 0, 80, TestPalette);

        line.Should().Contain(TestPalette.SelectedItemBg.AnsiBg);
    }

    [Fact]
    public void BuildCardLine_NotSelected_DoesNotContainAccentBar()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 2, 0, 80, TestPalette);

        line.Should().NotContain("\u258c");
    }

    [Fact]
    public void BuildCardLine_Selected_MetadataLine_HasHighlightBg()
    {
        var node = CreateLinkNodeWithMetadata("My Article", "https://example.com/article", LinkType.Content, "Jane Doe", null);

        // cardHeight=3 (compact): metadata line is at index 1
        var line3 = LinkTreeRenderer.BuildCardLine(node, true, 3, 1, 80, TestPalette);
        line3.Should().Contain(TestPalette.SelectedItemBg.AnsiBg);
        line3.Should().Contain("Jane Doe");

        // cardHeight=5 (standard): metadata line is at index 3
        var line5 = LinkTreeRenderer.BuildCardLine(node, true, 5, 3, 80, TestPalette);
        line5.Should().Contain(TestPalette.SelectedItemBg.AnsiBg);
        line5.Should().Contain("Jane Doe");
    }

    #endregion

    #region BuildCardLine - Title truncation

    [Fact]
    public void BuildCardLine_LongTitle_IsTruncated()
    {
        var longTitle = new string('A', 200);
        var node = CreateLinkNode(longTitle, "https://example.com", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 2, 0, 40, TestPalette);

        line.Should().Contain("\u2026");
        line.Length.Should().BeLessThan(200);
    }

    #endregion

    #region BuildCardLine - Standard 5-line cell mode

    [Fact]
    public void BuildCardLine_Standard5Line_TitleOnLine1()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        // cardHeight=5: title line is at index 1
        var line = LinkTreeRenderer.BuildCardLine(node, false, 5, 1, 80, TestPalette);

        line.Should().Contain("My Article");
    }

    [Fact]
    public void BuildCardLine_Standard5Line_AuthorDateOnLine3()
    {
        var node = CreateLinkNodeWithMetadata("My Article", "https://example.com/article", LinkType.Content, "Jane Doe", DateTime.Now);

        // cardHeight=5: author+date line is at index 3 (after title line 1 + title line 2)
        var line = LinkTreeRenderer.BuildCardLine(node, false, 5, 3, 80, TestPalette);

        line.Should().Contain("Jane Doe").And.Contain("Today");
    }

    [Fact]
    public void BuildCardLine_Standard5Line_PaddingLinesAreBlankExceptSeparator()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        // Line 0 is blank padding; line 2 is title overflow (blank for short title);
        // line 3 is author/date (blank for no metadata); line 4 is separator
        LinkTreeRenderer.BuildCardLine(node, false, 5, 0, 80, TestPalette).Should().HaveLength(80).And.Match(s => s.Trim().Length == 0);
        LinkTreeRenderer.BuildCardLine(node, false, 5, 2, 80, TestPalette).Should().HaveLength(80).And.Match(s => s.Trim().Length == 0);
        LinkTreeRenderer.BuildCardLine(node, false, 5, 4, 80, TestPalette).Should().Contain("\u2500");
    }

    #endregion

    #region Metadata subtitle fallback

    [Fact]
    public void BuildCardLine_MetadataWithAuthor_ShowsAuthor()
    {
        var root = LinkNode.CreateRoot();
        var link = new LinkInfo
        {
            DisplayText = "Article",
            Url = "https://example.com/article",
            Type = LinkType.Content,
            ImportanceScore = 50,
            Author = "Jane Doe"
        };
        var node = root.AddChild(link);

        // cardHeight=3: metadata at index 1
        var line = LinkTreeRenderer.BuildCardLine(node, false, 3, 1, 80, TestPalette);

        line.Should().Contain("Jane Doe");
    }

    [Fact]
    public void BuildCardLine_MetadataWithDateAndAuthor_ShowsBoth()
    {
        var root = LinkNode.CreateRoot();
        var link = new LinkInfo
        {
            DisplayText = "Article",
            Url = "https://example.com/article",
            Type = LinkType.Content,
            ImportanceScore = 50,
            Author = "Jane Doe",
            PublishedDate = new DateTime(2024, 3, 15)
        };
        var node = root.AddChild(link);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 3, 1, 80, TestPalette);

        line.Should().Contain("Jane Doe");
        line.Should().Contain("\u00b7"); // middle dot separator
    }

    #endregion

    #region FormatDate

    [Fact]
    public void FormatDate_Today_ReturnsToday()
    {
        LinkTreeRenderer.FormatDate(DateTime.Now).Should().Be("Today");
    }

    [Fact]
    public void FormatDate_Yesterday_ReturnsYesterday()
    {
        LinkTreeRenderer.FormatDate(DateTime.Now.AddDays(-1)).Should().Be("Yesterday");
    }

    [Fact]
    public void FormatDate_ThisYear_ReturnsShortMonth()
    {
        var date = new DateTime(DateTime.Now.Year, 3, 15);
        if (date.Date == DateTime.Now.Date || date.Date == DateTime.Now.Date.AddDays(-1))
        {
            // Skip if it happens to be today/yesterday
            return;
        }

        var result = LinkTreeRenderer.FormatDate(date);
        result.Should().Contain("Mar");
        result.Should().Contain("15");
    }

    [Fact]
    public void FormatDate_PreviousYear_IncludesYear()
    {
        var date = new DateTime(2023, 6, 20);
        var result = LinkTreeRenderer.FormatDate(date);
        result.Should().Contain("2023");
        result.Should().Contain("Jun");
    }

    [Fact]
    public void FormatDate_Null_ReturnsNull()
    {
        LinkTreeRenderer.FormatDate(null).Should().BeNull();
    }

    #endregion

    #region BuildCardLine - Link type colors

    [Fact]
    public void BuildCardLine_ContentLink_UsesBrightPinkColor()
    {
        var node = CreateLinkNode("Article", "https://example.com", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 2, 0, 80, TestPalette);

        line.Should().Contain(TestPalette.HeaderTitleFg.AnsiFg);
    }

    [Fact]
    public void BuildCardLine_NavigationLink_UsesNavigationColor()
    {
        var node = CreateLinkNode("Home", "https://example.com", LinkType.Navigation);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 2, 0, 80, TestPalette);

        line.Should().Contain(TestPalette.LinkNavigation.AnsiFg);
    }

    [Fact]
    public void BuildCardLine_ExternalLink_UsesExternalColor()
    {
        var node = CreateLinkNode("Partner", "https://external.com", LinkType.External);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 2, 0, 80, TestPalette);

        line.Should().Contain(TestPalette.LinkExternal.AnsiFg);
    }

    #endregion

    #region Cache dot indicator

    [Fact]
    public void BuildCardLine_Normal_CachedUrl_NosDotPrefix()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);
        var cachedUrls = new HashSet<string> { "https://example.com/article" };

        var line = LinkTreeRenderer.BuildCardLine(node, false, 2, 0, 80, TestPalette, cachedUrls);

        line.Should().NotContain("\u25cf", "cache dot indicator was removed");
        line.Should().StartWith(" ");
    }

    #endregion

    #region Title wrapping in 5-line mode

    [Fact]
    public void BuildCardLine_5Line_LongTitleWrapsToLine2()
    {
        // Title wider than textWidth wraps to line 2 instead of truncating on line 1
        var textWidth = LinkTreeRenderer.GetTitleTextWidth(40);
        var longTitle = string.Join(" ", Enumerable.Repeat("word", textWidth / 3));
        var node = CreateLinkNode(longTitle, "https://example.com", LinkType.Content);

        var line1 = LinkTreeRenderer.BuildCardLine(node, false, 5, 1, 40, TestPalette);
        var line2 = LinkTreeRenderer.BuildCardLine(node, false, 5, 2, 40, TestPalette);

        // Line 1 has first part, line 2 has overflow
        line1.Should().Contain("word");
        StripAnsi(line2).Trim().Should().NotBeEmpty();
    }

    [Fact]
    public void BuildCardLine_5Line_NoMetadata_Line3IsBlankPadding()
    {
        var node = CreateLinkNode("Short", "https://example.com", LinkType.Content);

        var line3 = LinkTreeRenderer.BuildCardLine(node, false, 5, 3, 40, TestPalette);

        // Line 3 is author/date metadata (blank when no author/date)
        StripAnsi(line3).Trim().Should().BeEmpty();
    }

    [Fact]
    public void BuildCardLine_5Line_VeryLongTitle_Line2HasEllipsis()
    {
        // A title so long it needs 3+ wrapped lines — line 2 should have ellipsis
        var textWidth = LinkTreeRenderer.GetTitleTextWidth(40);
        var veryLongTitle = string.Join(" ", Enumerable.Repeat("longword", textWidth));
        var node = CreateLinkNode(veryLongTitle, "https://example.com", LinkType.Content);

        var line2 = LinkTreeRenderer.BuildCardLine(node, false, 5, 2, 40, TestPalette);

        line2.Should().Contain("\u2026");
    }

    [Fact]
    public void BuildCardLine_5Line_Selected_LongTitleWrapsToLine2()
    {
        var textWidth = LinkTreeRenderer.GetTitleTextWidth(40);
        var longTitle = string.Join(" ", Enumerable.Repeat("word", textWidth / 3));
        var node = CreateLinkNode(longTitle, "https://example.com", LinkType.Content);

        var line1 = LinkTreeRenderer.BuildCardLine(node, true, 5, 1, 40, TestPalette);
        var line2 = LinkTreeRenderer.BuildCardLine(node, true, 5, 2, 40, TestPalette);

        // Selected title should have highlight background and wrap
        line1.Should().Contain(TestPalette.SelectedItemBg.AnsiBg);
        line2.Should().Contain(TestPalette.SelectedItemBg.AnsiBg);
        StripAnsi(line2).Trim().Should().NotBeEmpty();
    }

    #endregion

    #region No wrapping in 3-line mode

    [Fact]
    public void BuildCardLine_3Line_LongTitleTruncatedNotWrapped()
    {
        var longTitle = new string('A', 200);
        var node = CreateLinkNode(longTitle, "https://example.com", LinkType.Content);

        var titleLine = LinkTreeRenderer.BuildCardLine(node, false, 3, 0, 40, TestPalette);

        titleLine.Should().Contain("\u2026");
    }

    [Fact]
    public void BuildCardLine_3Line_NoOverflowLine()
    {
        var longTitle = new string('A', 200);
        var node = CreateLinkNode(longTitle, "https://example.com", LinkType.Content);

        // In 3-line mode, line index 2 is the separator rule, not title overflow
        var line2 = LinkTreeRenderer.BuildCardLine(node, false, 3, 2, 40, TestPalette);

        line2.Should().Contain("\u2500"); // separator rule, not overflow text
    }

    #endregion

    #region Visible char width consistency

    [Fact]
    public void BuildCardLine_SelectedAndNormal_SameVisibleTextWidth()
    {
        var node = CreateLinkNode("My Article Title", "https://example.com", LinkType.Content);
        const int cellWidth = 40;

        var normalLine = LinkTreeRenderer.BuildCardLine(node, false, 5, 1, cellWidth, TestPalette);
        var selectedLine = LinkTreeRenderer.BuildCardLine(node, true, 5, 1, cellWidth, TestPalette);

        // Both should produce same visible character count for the title area
        var normalVisible = StripAnsi(normalLine);
        var selectedVisible = StripAnsi(selectedLine);

        normalVisible.Length.Should().Be(selectedVisible.Length);
    }

    [Fact]
    public void BuildCardLine_SelectedAndNormal_TitleLine2_SameVisibleWidth()
    {
        var textWidth = LinkTreeRenderer.GetTitleTextWidth(40);
        var longTitle = string.Join(" ", Enumerable.Repeat("word", textWidth / 3));
        var node = CreateLinkNode(longTitle, "https://example.com", LinkType.Content);
        const int cellWidth = 40;

        // Line 2 is now title line 2 (overflow) in 5-line mode
        var normalLine = LinkTreeRenderer.BuildCardLine(node, false, 5, 2, cellWidth, TestPalette);
        var selectedLine = LinkTreeRenderer.BuildCardLine(node, true, 5, 2, cellWidth, TestPalette);

        var normalVisible = StripAnsi(normalLine);
        var selectedVisible = StripAnsi(selectedLine);

        normalVisible.Length.Should().Be(selectedVisible.Length);
    }

    #endregion

    #region Empty metadata

    [Fact]
    public void BuildCardLine_NoAuthorNoDate_MetadataIsBlank()
    {
        var node = CreateLinkNode("Article", "https://example.com", LinkType.Content);

        // cardHeight=3: metadata at index 1
        var line = LinkTreeRenderer.BuildCardLine(node, false, 3, 1, 80, TestPalette);

        // Should contain dim escape but visible text should be blank
        StripAnsi(line).Trim().Should().BeEmpty();
    }

    [Fact]
    public void BuildCardLine_5Line_NoAuthorNoDate_MetadataIsBlank()
    {
        var node = CreateLinkNode("Article", "https://example.com", LinkType.Content);

        // cardHeight=5: author/date at index 3
        var line = LinkTreeRenderer.BuildCardLine(node, false, 5, 3, 80, TestPalette);

        StripAnsi(line).Trim().Should().BeEmpty();
    }

    #endregion

    #region Separator rule rendering

    [Fact]
    public void BuildCardLine_SeparatorRule_SpansFullWidth()
    {
        var node = CreateLinkNode("Article", "https://example.com", LinkType.Content);
        const int width = 40;

        // cardHeight=3: separator is line 2; cardHeight=5: separator is line 4
        var line3 = LinkTreeRenderer.BuildCardLine(node, false, 3, 2, width, TestPalette);
        var line5 = LinkTreeRenderer.BuildCardLine(node, false, 5, 4, width, TestPalette);

        var visible3 = StripAnsi(line3);
        var visible5 = StripAnsi(line5);

        // Separator should span the full cell width in visible characters
        visible3.Should().HaveLength(width);
        visible5.Should().HaveLength(width);
    }

    [Fact]
    public void BuildCardLine_Selected_SeparatorRule_RendersDimRule()
    {
        // workspace-63jj: the separator row keeps the dim \u2500 rule even
        // when the card is selected. Painting it with selBg (the previous
        // workspace-mj9x behaviour) made the selection rectangle visually
        // overshoot the cell box and eat the divider between cell rows.
        var node = CreateLinkNode("Article", "https://example.com", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, true, 3, 2, 40, TestPalette);

        line.Should().NotContain(TestPalette.SelectedItemBg.AnsiBg);
        line.Should().Contain("\u2500");
    }

    [Fact]
    public void BuildCardLine_Selected_TopPadding_FilledWithHighlightBg()
    {
        // workspace-zlv0: the top-padding row IS part of the selection
        // rectangle so the green box reaches the cell's top edge. Only the
        // separator row (cell's bottom border, shared with the next row) is
        // excluded \u2014 see BuildCardLine_Selected_SeparatorRule_RendersDimRule.
        var node = CreateLinkNode("Article", "https://example.com", LinkType.Content);

        // 5-line card: titleLineIdx=1, so lineIndex=0 is top padding.
        var line = LinkTreeRenderer.BuildCardLine(node, true, 5, 0, 40, TestPalette);

        line.Should().Contain(TestPalette.SelectedItemBg.AnsiBg);
        line.Should().Contain("\u258c");
    }

    #endregion

    #region GetTitleTextWidth and GetWrappedTitleLine

    [Theory]
    [InlineData(40, 38)]
    [InlineData(20, 18)]
    [InlineData(3, 1)]
    public void GetTitleTextWidth_ReturnsWidthMinusTwo(int cellWidth, int expected)
    {
        LinkTreeRenderer.GetTitleTextWidth(cellWidth).Should().Be(expected);
    }

    [Fact]
    public void GetWrappedTitleLine_ShortTitle_Line0ReturnsFullTitle()
    {
        var result = LinkTreeRenderer.GetWrappedTitleLine("Hello World", 40, 0);
        result.Should().Be("Hello World");
    }

    [Fact]
    public void GetWrappedTitleLine_ShortTitle_Line1ReturnsEmpty()
    {
        var result = LinkTreeRenderer.GetWrappedTitleLine("Hello World", 40, 1);
        result.Should().BeEmpty();
    }

    [Fact]
    public void GetWrappedTitleLine_LongTitle_Line1ReturnsOverflow()
    {
        // "Hello World Foo Bar" at width=10 should wrap
        var result = LinkTreeRenderer.GetWrappedTitleLine("Hello World Foo Bar", 10, 1);
        result.Should().NotBeEmpty();
    }

    [Fact]
    public void GetWrappedTitleLine_VeryLongTitle_Line1HasEllipsis()
    {
        // Many words that need 3+ lines at width=10
        var title = "one two three four five six seven eight nine ten";
        var result = LinkTreeRenderer.GetWrappedTitleLine(title, 10, 1);
        result.Should().Contain("\u2026");
    }

    #endregion

    #region Composed grid row total width

    [Fact]
    public void ComposedGridRow_TotalVisibleWidth_EqualsLayoutWidth_TwoColumns()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 30);
        layout.Columns.Should().Be(2);

        var leftNode = CreateLinkNode("Left Article", "https://example.com/left", LinkType.Content);
        var rightNode = CreateLinkNode("Right Article", "https://example.com/right", LinkType.Content);

        for (var lineIdx = 0; lineIdx < layout.CellHeight; lineIdx++)
        {
            var left = LinkTreeRenderer.BuildCardLine(leftNode, false, layout.CellHeight, lineIdx, layout.CellWidth, TestPalette);
            var rightWidth = layout.Width - layout.CellWidth - 1;
            var right = LinkTreeRenderer.BuildCardLine(rightNode, false, layout.CellHeight, lineIdx, rightWidth, TestPalette);

            var isSeparatorLine = lineIdx == layout.CellHeight - 1 && layout.CellHeight > 1;
            var divider = isSeparatorLine ? "\u253c" : "\u2502";

            var composed = left + divider + right;
            var visibleWidth = StripAnsi(composed).Length;

            visibleWidth.Should().Be(layout.Width,
                $"line {lineIdx}: visible width should equal layout width {layout.Width}");
        }
    }

    [Fact]
    public void ComposedGridRow_SelectedLeft_TotalVisibleWidth_EqualsLayoutWidth()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 30);
        layout.Columns.Should().Be(2);

        var leftNode = CreateLinkNode("Left Selected", "https://example.com/left", LinkType.Content);
        var rightNode = CreateLinkNode("Right Normal", "https://example.com/right", LinkType.Content);

        // Test title line (lineIdx 1 for 5-line mode)
        var titleLine = layout.CellHeight >= 5 ? 1 : 0;
        var left = LinkTreeRenderer.BuildCardLine(leftNode, true, layout.CellHeight, titleLine, layout.CellWidth, TestPalette);
        var rightWidth = layout.Width - layout.CellWidth - 1;
        var right = LinkTreeRenderer.BuildCardLine(rightNode, false, layout.CellHeight, titleLine, rightWidth, TestPalette);
        var composed = left + "\u2502" + right;
        var visibleWidth = StripAnsi(composed).Length;

        visibleWidth.Should().Be(layout.Width);
    }

    [Fact]
    public void ComposedGridRow_TotalVisibleWidth_EqualsLayoutWidth_RemainderWidth()
    {
        // Width 170 gives a NONZERO remainder (inner 168 \u2192 base cell 83, last cell 84), so the
        // test actually distinguishes LastCellWidthFor from CellWidth \u2014 at a zero-remainder
        // width the two coincide and a last-column bug would slip through (workspace-ehon review).
        var layout = LinkTreeRenderer.ComputeLayout(170, 40);
        layout.Columns.Should().Be(2);
        var lastWidth = layout.Width - (layout.CellWidth * (layout.Columns - 1)) - (layout.Columns - 1);
        lastWidth.Should().BeGreaterThan(layout.CellWidth, "the last column must absorb the remainder");

        var nodes = new[]
        {
            CreateLinkNode("Alpha", "https://example.com/a", LinkType.Content),
            CreateLinkNode("Beta", "https://example.com/b", LinkType.Content),
        };

        for (var lineIdx = 0; lineIdx < layout.CellHeight; lineIdx++)
        {
            var isSeparatorLine = lineIdx == layout.CellHeight - 1 && layout.CellHeight > 1;
            var divider = isSeparatorLine ? "\u253c" : "\u2502";

            var composed = string.Empty;
            for (var col = 0; col < layout.Columns; col++)
            {
                if (col > 0)
                {
                    composed += divider;
                }

                var isLastCol = col == layout.Columns - 1;
                var cellW = isLastCol ? lastWidth : layout.CellWidth;
                composed += LinkTreeRenderer.BuildCardLine(nodes[col], false, layout.CellHeight, lineIdx, cellW, TestPalette);
            }

            StripAnsi(composed).Length.Should().Be(layout.Width,
                $"line {lineIdx}: three cells + two dividers should fill layout width {layout.Width}");
        }
    }

    #endregion

    #region Spotlight column math (workspace-ehon)

    // The dock/spotlight targets the selected story via TryGetSelectedRowScreenPosition.
    // This is the exact "spotlight highlights the WRONG element" bug class the Verification
    // Doctrine calls out — the flash must land on the SELECTED cell's column and row,
    // not a fixed position.
    [Fact]
    public void TryGetSelectedRowScreenPosition_XOffsetTracksSelectedColumn()
    {
        var nodes = new List<LinkNode>
        {
            CreateLinkNode("A", "https://example.com/a", LinkType.Content),
            CreateLinkNode("B", "https://example.com/b", LinkType.Content),
            CreateLinkNode("C", "https://example.com/c", LinkType.Content),
            CreateLinkNode("D", "https://example.com/d", LinkType.Content),
        };
        var layout = LinkTreeRenderer.ComputeLayout(160, 40);
        layout.Columns.Should().Be(2);

        // Row 0 = nodes[0,1] in columns 0,1; row 1 starts at nodes[2].
        var p0 = LinkTreeRenderer.TryGetSelectedRowScreenPosition(nodes, 0, 0, layout, 100);
        var p1 = LinkTreeRenderer.TryGetSelectedRowScreenPosition(nodes, 1, 0, layout, 100);
        var p2 = LinkTreeRenderer.TryGetSelectedRowScreenPosition(nodes, 2, 0, layout, 100);

        p0.Should().NotBeNull();
        p1.Should().NotBeNull();
        p2.Should().NotBeNull();

        // Each column c starts at c*(CellWidth+1); the title text sits 2 cells in.
        p0!.Value.Col.Should().Be(2);
        p1!.Value.Col.Should().Be(layout.CellWidth + 1 + 2);

        // nodes[0] and nodes[1] share row 0's title line; nodes[2] wraps to row 1, column 0.
        p0.Value.Row.Should().Be(p1.Value.Row);
        p2!.Value.Col.Should().Be(2);
        p2.Value.Row.Should().Be(p0.Value.Row + layout.CellHeight);
    }

    #endregion

    #region Header rendering - compact style layout

    [Fact]
    public void ComputeLayout_HeaderIs3Lines_MatchesBoxHeaderStyle()
    {
        // BoxHeader: top border + subtitle + bottom border = 3 lines
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        layout.HeaderLines.Should().Be(3, "BoxHeader is 3 lines");
    }

    [Fact]
    public void ComputeLayout_StatusBarIs2Lines_MatchesSeparatorPlusContent()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        layout.StatusBarLines.Should().Be(2, "status bar is separator + content line");
    }

    [Fact]
    public void ComputeLayout_AvailableHeight_AccountsForBoxHeader()
    {
        // availableHeight = terminalHeight - headerLines(3) - statusBarLines(2)
        var layout = LinkTreeRenderer.ComputeLayout(80, 30);
        var expectedAvailable = 30 - 3 - 2; // 25
        var expectedVisibleRows = expectedAvailable / layout.CellHeight;
        layout.VisibleRows.Should().Be(expectedVisibleRows);
    }

    #endregion

    private static string StripAnsi(string input) =>
        System.Text.RegularExpressions.Regex.Replace(input, @"\x1b\[[0-9;]*m", string.Empty);

    private static LinkNode CreateLinkNode(string displayText, string url, LinkType type)
    {
        var root = LinkNode.CreateRoot();
        var link = new LinkInfo
        {
            DisplayText = displayText,
            Url = url,
            Type = type,
            ImportanceScore = 50
        };
        return root.AddChild(link);
    }

    private static LinkNode CreateLinkNodeWithMetadata(string displayText, string url, LinkType type, string? author, DateTime? publishedDate)
    {
        var root = LinkNode.CreateRoot();
        var link = new LinkInfo
        {
            DisplayText = displayText,
            Url = url,
            Type = type,
            ImportanceScore = 50,
            Author = author,
            PublishedDate = publishedDate
        };
        return root.AddChild(link);
    }
}
