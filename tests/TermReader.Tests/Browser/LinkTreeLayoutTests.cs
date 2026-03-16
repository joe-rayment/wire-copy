// Educational and personal use only.

using FluentAssertions;
using NSubstitute;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.Themes;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

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
    public void ComputeLayout_Width40_ReturnsOneColumn()
    {
        var layout = LinkTreeRenderer.ComputeLayout(40, 24);
        layout.Columns.Should().Be(1);
    }

    [Fact]
    public void CellWidth_IsHalfMinusOneForTwoColumns()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        // For 2 columns: (width-1)/2
        layout.CellWidth.Should().Be((layout.Width - 1) / 2);
    }

    [Fact]
    public void CellWidth_IsFullWidthForOneColumn()
    {
        var layout = LinkTreeRenderer.ComputeLayout(40, 24);
        layout.CellWidth.Should().Be(layout.Width);
    }

    [Fact]
    public void CellHeight_IsCompactWhenShort()
    {
        // availableHeight = max(4, 18 - 2 - 3) = 13 < 15 → compact (3)
        var layout = LinkTreeRenderer.ComputeLayout(80, 18);
        layout.CellHeight.Should().Be(3);
    }

    [Fact]
    public void CellHeight_IsStandardWhenTall()
    {
        // availableHeight = max(4, 30 - 2 - 3) = 25 >= 15 → standard (5)
        var layout = LinkTreeRenderer.ComputeLayout(80, 30);
        layout.CellHeight.Should().Be(5);
    }

    [Fact]
    public void VisibleRows_CalculatedFromAvailableHeight()
    {
        // availableHeight = max(4, 30 - 2 - 3) = 25, cellHeight = 5 → 25/5 = 5
        var layout = LinkTreeRenderer.ComputeLayout(80, 30);
        layout.VisibleRows.Should().Be(5);
    }

    [Fact]
    public void ComputeLayout_HeaderAndStatusBarLines()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        layout.HeaderLines.Should().Be(2);
        layout.StatusBarLines.Should().Be(3);
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
    public void BuildCardLine_NormalMetadata_ContainsDimEscape()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 3, 1, 80, TestPalette);

        line.Should().Contain("\x1b[2m"); // Dim
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

        line.Should().Contain("...");
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
    public void BuildCardLine_ContentLink_UsesContentColor()
    {
        var node = CreateLinkNode("Article", "https://example.com", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 2, 0, 80, TestPalette);

        line.Should().Contain(TestPalette.LinkContent.AnsiFg);
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

        line2.Should().Contain("...");
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

        titleLine.Should().Contain("...");
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
    public void BuildCardLine_Selected_SeparatorRule_HasHighlightBg()
    {
        var node = CreateLinkNode("Article", "https://example.com", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, true, 3, 2, 40, TestPalette);

        line.Should().Contain(TestPalette.SelectedItemBg.AnsiBg);
        line.Should().Contain("\u2500");
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
        result.Should().Contain("...");
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

    #endregion

    #region Header rendering - compact style layout

    [Fact]
    public void ComputeLayout_HeaderIs2Lines_MatchesCompactStyle()
    {
        // Compact header style: line 1 = title + domain, line 2 = thin rule
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        layout.HeaderLines.Should().Be(2, "compact header is title line + thin rule");
    }

    [Fact]
    public void ComputeLayout_StatusBarIs3Lines_MatchesCompactStyle()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        layout.StatusBarLines.Should().Be(3, "status bar is separator + line 1 + line 2");
    }

    [Fact]
    public void ComputeLayout_AvailableHeight_AccountsForCompactHeader()
    {
        // availableHeight = terminalHeight - headerLines(2) - statusBarLines(3)
        var layout = LinkTreeRenderer.ComputeLayout(80, 30);
        var expectedAvailable = 30 - 2 - 3; // 25
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
