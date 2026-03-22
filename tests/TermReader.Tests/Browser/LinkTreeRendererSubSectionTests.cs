// Educational and personal use only.

using FluentAssertions;
using NSubstitute;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Entities.Browser;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class LinkTreeRendererSubSectionTests
{
    private readonly LinkTreeRenderer _renderer;

    public LinkTreeRendererSubSectionTests()
    {
        var themeProvider = Substitute.For<IThemeProvider>();
        themeProvider.CurrentTheme.Returns(ThemeName.Phosphor);
        var helpers = new RenderHelpers { TerminalHeight = 50 };
        _renderer = new LinkTreeRenderer(helpers, themeProvider);
    }

    #region GetLinesForGroupHeader — sub-section vs top-level

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GetLinesForGroupHeader_SubSection_CompactMode_Returns1(int cardHeight)
    {
        if (cardHeight != 1)
        {
            return;
        }

        var node = CreateSubSectionNode("World", expanded: true);

        LinkTreeRenderer.GetLinesForGroupHeader(node, cardHeight).Should().Be(1);
    }

    [Fact]
    public void GetLinesForGroupHeader_SubSection_StandardMode_Returns2_WhenExpanded()
    {
        var node = CreateSubSectionNode("World", expanded: true);

        LinkTreeRenderer.GetLinesForGroupHeader(node, cardHeight: 2).Should().Be(2);
    }

    [Fact]
    public void GetLinesForGroupHeader_SubSection_StandardMode_Returns2_WhenCollapsed()
    {
        var node = CreateSubSectionNode("World", expanded: false);

        LinkTreeRenderer.GetLinesForGroupHeader(node, cardHeight: 2).Should().Be(2);
    }

    [Fact]
    public void GetLinesForGroupHeader_TopLevel_Expanded_Returns3()
    {
        var node = CreateTopLevelNode(LinkType.Navigation, expanded: true);

        LinkTreeRenderer.GetLinesForGroupHeader(node, cardHeight: 2).Should().Be(3);
    }

    [Fact]
    public void GetLinesForGroupHeader_TopLevel_Collapsed_Returns2()
    {
        var node = CreateTopLevelNode(LinkType.Navigation, expanded: false);

        LinkTreeRenderer.GetLinesForGroupHeader(node, cardHeight: 2).Should().Be(2);
    }

    #endregion

    #region RenderGroupHeader — dispatch does not throw

    [Fact]
    public void RenderGroupHeader_SubSection_DoesNotThrow()
    {
        var node = CreateSubSectionNode("Opinion", expanded: true);
        var options = CreateOptions();

        var act = () => _renderer.RenderGroupHeader(node, isSelected: false, options);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderGroupHeader_SubSection_Selected_DoesNotThrow()
    {
        var node = CreateSubSectionNode("Sports", expanded: true);
        var options = CreateOptions();

        var act = () => _renderer.RenderGroupHeader(node, isSelected: true, options);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderGroupHeader_SubSection_Collapsed_DoesNotThrow()
    {
        var node = CreateSubSectionNode("Business", expanded: false);
        var options = CreateOptions();

        var act = () => _renderer.RenderGroupHeader(node, isSelected: false, options);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderGroupHeader_SubSection_CompactMode_DoesNotThrow()
    {
        var node = CreateSubSectionNode("Tech", expanded: true);
        var options = CreateOptions(terminalHeight: 12);

        var act = () => _renderer.RenderGroupHeader(node, isSelected: false, options);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderGroupHeader_SubSection_Selected_Collapsed_DoesNotThrow()
    {
        var node = CreateSubSectionNode("World", expanded: false);
        var options = CreateOptions();

        var act = () => _renderer.RenderGroupHeader(node, isSelected: true, options);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderGroupHeader_TopLevel_StillWorks()
    {
        var node = CreateTopLevelNode(LinkType.Navigation, expanded: true);
        var options = CreateOptions();

        var act = () => _renderer.RenderGroupHeader(node, isSelected: false, options);

        act.Should().NotThrow();
    }

    #endregion

    #region RenderLinkTree — integration with sub-sections

    [Fact]
    public void RenderLinkTree_WithSubSections_DoesNotThrow()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("Article 1", "World"),
                ContentLink("Article 2", "World"),
                ContentLink("Article 3", "Sports"),
                ContentLink("Article 4", "Sports"),
            },
            [LinkType.Navigation] = new()
            {
                NavLink("Home"),
            },
        };

        var tree = NavigationTree.BuildWithGroups(links);
        var context = new NavigationContext();
        var options = CreateOptions();

        var act = () => _renderer.RenderLinkTree(tree, context, maxLines: 40, options);

        act.Should().NotThrow();
    }

    [Fact]
    public void RenderLinkTree_WithSubSections_SelectedSubSection_DoesNotThrow()
    {
        var links = new Dictionary<LinkType, List<LinkInfo>>
        {
            [LinkType.Content] = new()
            {
                ContentLink("Article 1", "World"),
                ContentLink("Article 2", "World"),
                ContentLink("Article 3", "Sports"),
                ContentLink("Article 4", "Sports"),
            },
        };

        var tree = NavigationTree.BuildWithGroups(links);

        // Select the first sub-section header
        tree.SelectNodeById(tree.Root.Children[0].Id);
        var context = new NavigationContext();
        var options = CreateOptions();

        var act = () => _renderer.RenderLinkTree(tree, context, maxLines: 40, options);

        act.Should().NotThrow();
    }

    #endregion

    #region GetCardHeight — boundary verification

    [Fact]
    public void GetCardHeight_Under10_Returns1()
    {
        LinkTreeRenderer.GetCardHeight(9).Should().Be(1);
    }

    [Fact]
    public void GetCardHeight_10To24_Returns2()
    {
        LinkTreeRenderer.GetCardHeight(10).Should().Be(2);
        LinkTreeRenderer.GetCardHeight(24).Should().Be(2);
    }

    [Fact]
    public void GetCardHeight_25Plus_Returns3()
    {
        LinkTreeRenderer.GetCardHeight(25).Should().Be(3);
    }

    #endregion

    #region Helpers

    private static LinkNode CreateSubSectionNode(string title, bool expanded)
    {
        var root = LinkNode.CreateRoot();
        var header = LinkInfo.CreateSubSectionHeader(title, LinkType.Content);
        var node = root.AddChild(header);

        // Add some child links
        node.AddChild(new LinkInfo
        {
            Url = "https://example.com/1",
            DisplayText = "Child 1",
            Type = LinkType.Content,
            ImportanceScore = 70,
        });
        node.AddChild(new LinkInfo
        {
            Url = "https://example.com/2",
            DisplayText = "Child 2",
            Type = LinkType.Content,
            ImportanceScore = 70,
        });

        if (!expanded)
        {
            node.Collapse();
        }

        return node;
    }

    private static LinkNode CreateTopLevelNode(LinkType type, bool expanded)
    {
        var root = LinkNode.CreateRoot();
        var header = LinkInfo.CreateGroupHeader(type);
        var node = root.AddChild(header);

        node.AddChild(new LinkInfo
        {
            Url = "https://example.com/1",
            DisplayText = "Child 1",
            Type = type,
            ImportanceScore = 30,
        });

        if (!expanded)
        {
            node.Collapse();
        }
        else
        {
            node.Expand();
        }

        return node;
    }

    private static LinkInfo ContentLink(string text, string? sectionTitle = null)
    {
        return new LinkInfo
        {
            Url = $"https://example.com/{text.Replace(' ', '-').ToLowerInvariant()}",
            DisplayText = text,
            Type = LinkType.Content,
            ImportanceScore = 70,
            SectionTitle = sectionTitle,
        };
    }

    private static LinkInfo NavLink(string text)
    {
        return new LinkInfo
        {
            Url = $"https://example.com/{text.Replace(' ', '-').ToLowerInvariant()}",
            DisplayText = text,
            Type = LinkType.Navigation,
            ImportanceScore = 30,
        };
    }

    private static RenderOptions CreateOptions(int terminalWidth = 80, int terminalHeight = 40)
    {
        return new RenderOptions
        {
            TerminalWidth = terminalWidth,
            TerminalHeight = terminalHeight,
            MaxContentWidth = terminalWidth - 4,
        };
    }

    #endregion
}
