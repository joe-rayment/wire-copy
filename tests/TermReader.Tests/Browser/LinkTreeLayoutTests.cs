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
        // availableHeight = max(4, 18 - 6 - 3) = 9 < 15 → compact (3)
        var layout = LinkTreeRenderer.ComputeLayout(80, 18);
        layout.CellHeight.Should().Be(3);
    }

    [Fact]
    public void CellHeight_IsStandardWhenTall()
    {
        // availableHeight = max(4, 30 - 6 - 3) = 21 >= 15 → standard (5)
        var layout = LinkTreeRenderer.ComputeLayout(80, 30);
        layout.CellHeight.Should().Be(5);
    }

    [Fact]
    public void VisibleRows_CalculatedFromAvailableHeight()
    {
        // availableHeight = max(4, 30 - 6 - 3) = 21, cellHeight = 5 → 21/5 = 4
        var layout = LinkTreeRenderer.ComputeLayout(80, 30);
        layout.VisibleRows.Should().Be(4);
    }

    [Fact]
    public void ComputeLayout_HeaderAndStatusBarLines()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        layout.HeaderLines.Should().Be(6);
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
    public void BuildCardLine_NormalMetadata_ContainsDomainText()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        // cardHeight=3 (compact): metadata line is at index 1
        var line = LinkTreeRenderer.BuildCardLine(node, false, 3, 1, 80, TestPalette);

        line.Should().Contain("example.com");
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

        // cardHeight=1, lineIndex=1 is a blank separator, not domain
        line.Should().BeEmpty();
    }

    [Fact]
    public void BuildCardLine_ExpandedMode_ThirdLineIsBlank()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 3, 2, 80, TestPalette);

        line.Should().BeEmpty();
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
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        // cardHeight=3 (compact): metadata line is at index 1
        var line = LinkTreeRenderer.BuildCardLine(node, true, 3, 1, 80, TestPalette);

        line.Should().Contain(TestPalette.SelectedItemBg.AnsiBg);
        line.Should().Contain("example.com");
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
    public void BuildCardLine_Standard5Line_MetadataOnLine3()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        // cardHeight=5: metadata line is at index 3
        var line = LinkTreeRenderer.BuildCardLine(node, false, 5, 3, 80, TestPalette);

        line.Should().Contain("example.com");
    }

    [Fact]
    public void BuildCardLine_Standard5Line_PaddingLinesAreBlank()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        // Lines 0, 2, 4 should be blank padding
        LinkTreeRenderer.BuildCardLine(node, false, 5, 0, 80, TestPalette).Should().BeEmpty();
        LinkTreeRenderer.BuildCardLine(node, false, 5, 2, 80, TestPalette).Should().BeEmpty();
        LinkTreeRenderer.BuildCardLine(node, false, 5, 4, 80, TestPalette).Should().BeEmpty();
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
}
