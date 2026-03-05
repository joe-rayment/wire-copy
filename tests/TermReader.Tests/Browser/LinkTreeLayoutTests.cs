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

    #region ComputeLayout

    [Fact]
    public void ComputeLayout_Standard80x24_ReturnsCardHeightTwo()
    {
        // available = max(3, 24 - 6 - 3) = 15 → cardHeight = 2 (standard)
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        layout.StandardCardHeight.Should().Be(2);
    }

    [Fact]
    public void ComputeLayout_Large80x50_ReturnsCardHeightThree()
    {
        // available = max(3, 50 - 6 - 3) = 41 → cardHeight = 3 (expanded)
        var layout = LinkTreeRenderer.ComputeLayout(80, 50);
        layout.StandardCardHeight.Should().Be(3);
    }

    [Fact]
    public void ComputeLayout_Compact80x18_ReturnsCardHeightOne()
    {
        // available = max(3, 18 - 6 - 3) = 9 → cardHeight = 1 (compact)
        var layout = LinkTreeRenderer.ComputeLayout(80, 18);
        layout.StandardCardHeight.Should().Be(1);
    }

    [Fact]
    public void ComputeLayout_CompactThreshold_IsTen()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        layout.CompactThreshold.Should().Be(10);
    }

    [Fact]
    public void ComputeLayout_GroupHeaderHeight_MatchesCardHeight_WhenCompact()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 18);
        layout.GroupHeaderHeight.Should().Be(1);
    }

    [Fact]
    public void ComputeLayout_GroupHeaderHeight_EqualsCardHeight_WhenStandard()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 24);
        layout.GroupHeaderHeight.Should().Be(2);
    }

    [Fact]
    public void ComputeLayout_GroupHeaderHeight_IsThree_WhenExpanded()
    {
        var layout = LinkTreeRenderer.ComputeLayout(80, 50);
        layout.GroupHeaderHeight.Should().Be(3);
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
    public void BuildCardLine_NormalDomain_ContainsDomainText()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 2, 1, 80, TestPalette);

        line.Should().Contain("example.com");
    }

    [Fact]
    public void BuildCardLine_NormalDomain_ContainsDimEscape()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, false, 2, 1, 80, TestPalette);

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
    public void BuildCardLine_Selected_DomainLine_HasHighlightBg()
    {
        var node = CreateLinkNode("My Article", "https://example.com/article", LinkType.Content);

        var line = LinkTreeRenderer.BuildCardLine(node, true, 2, 1, 80, TestPalette);

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
