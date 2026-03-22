// Educational and personal use only.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TermReader.Domain.Enums.Browser;
using TermReader.Domain.ValueObjects.Browser;
using TermReader.Infrastructure.Browser;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class NavigationTreeBuilderHierarchyTests
{
    private readonly NavigationTreeBuilder _builder;

    public NavigationTreeBuilderHierarchyTests()
    {
        var logger = Substitute.For<ILogger<NavigationTreeBuilder>>();
        _builder = new NavigationTreeBuilder(logger);
    }

    private static SiteHierarchyConfig CreateConfig(params HierarchySection[] sections)
    {
        return new SiteHierarchyConfig
        {
            Domain = "example.com",
            UrlPattern = "^https?://example\\.com/?$",
            Sections = sections.ToList(),
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "test-model",
        };
    }

    private static LinkInfo CreateContentLink(
        string url = "https://example.com/article",
        string text = "Article",
        string? parentSelector = null,
        string? sectionTitle = null)
    {
        return new LinkInfo
        {
            Url = url,
            DisplayText = text,
            Type = LinkType.Content,
            ImportanceScore = 80,
            ParentSelector = parentSelector,
            SectionTitle = sectionTitle,
        };
    }

    private static LinkInfo CreateNavLink(string url = "https://example.com/nav", string text = "Nav")
    {
        return new LinkInfo
        {
            Url = url,
            DisplayText = text,
            Type = LinkType.Navigation,
            ImportanceScore = 30,
        };
    }

    [Fact]
    public async Task BuildTreeAsync_WithConfig_MatchesByParentSelector()
    {
        var links = new List<LinkInfo>
        {
            CreateContentLink(parentSelector: "article > h3 > a"),
            CreateContentLink(url: "https://example.com/other", text: "Other", parentSelector: "div.sidebar a"),
        };

        var config = CreateConfig(
            new HierarchySection
            {
                Name = "Main Articles",
                SortOrder = 0,
                ParentSelectors = new List<string> { "article > h3" },
            });

        var tree = await _builder.BuildTreeAsync(links, config);

        var nodes = tree.GetAllNodes().ToList();
        // Should have a section header "Main Articles" with 1 child, plus 1 unmatched link
        var sectionHeaders = nodes.Where(n => n.IsGroupHeader && n.Link.DisplayText == "Main Articles").ToList();
        sectionHeaders.Should().HaveCount(1);
        sectionHeaders[0].Children.Should().HaveCount(1);
    }

    [Fact]
    public async Task BuildTreeAsync_WithConfig_MatchesByUrlPattern()
    {
        var links = new List<LinkInfo>
        {
            CreateContentLink(url: "https://example.com/opinion/article1", text: "Opinion 1"),
            CreateContentLink(url: "https://example.com/news/article1", text: "News 1"),
        };

        var config = CreateConfig(
            new HierarchySection
            {
                Name = "Opinion",
                SortOrder = 0,
                UrlPatterns = new List<string> { "/opinion/" },
            });

        var tree = await _builder.BuildTreeAsync(links, config);

        var opinionHeader = tree.GetAllNodes()
            .FirstOrDefault(n => n.IsGroupHeader && n.Link.DisplayText == "Opinion");
        opinionHeader.Should().NotBeNull();
        opinionHeader!.Children.Should().HaveCount(1);
        opinionHeader.Children[0].Link.DisplayText.Should().Be("Opinion 1");
    }

    [Fact]
    public async Task BuildTreeAsync_WithConfig_MatchesBySectionTitle()
    {
        var links = new List<LinkInfo>
        {
            CreateContentLink(text: "Sports Article", sectionTitle: "Sports"),
            CreateContentLink(text: "Other Article"),
        };

        var config = CreateConfig(
            new HierarchySection
            {
                Name = "Sports",
                SortOrder = 0,
            });

        var tree = await _builder.BuildTreeAsync(links, config);

        var sportsHeader = tree.GetAllNodes()
            .FirstOrDefault(n => n.IsGroupHeader && n.Link.DisplayText == "Sports");
        sportsHeader.Should().NotBeNull();
        sportsHeader!.Children.Should().HaveCount(1);
        sportsHeader.Children[0].Link.DisplayText.Should().Be("Sports Article");
    }

    [Fact]
    public async Task BuildTreeAsync_WithConfig_UnmatchedLinksUnderRoot()
    {
        var links = new List<LinkInfo>
        {
            CreateContentLink(text: "Matched", parentSelector: "article > h3"),
            CreateContentLink(text: "Unmatched 1"),
            CreateContentLink(text: "Unmatched 2"),
        };

        var config = CreateConfig(
            new HierarchySection
            {
                Name = "Top",
                SortOrder = 0,
                ParentSelectors = new List<string> { "article > h3" },
            });

        var tree = await _builder.BuildTreeAsync(links, config);

        // Root's direct children: section header "Top" + 2 unmatched links
        tree.Root.Children.Should().HaveCount(3);
        tree.Root.Children.Count(c => !c.IsGroupHeader).Should().Be(2);
    }

    [Fact]
    public async Task BuildTreeAsync_WithConfig_SectionsOrderedBySortOrder()
    {
        var links = new List<LinkInfo>
        {
            CreateContentLink(url: "https://example.com/sports/1", text: "Sports 1"),
            CreateContentLink(url: "https://example.com/opinion/1", text: "Opinion 1"),
        };

        var config = CreateConfig(
            new HierarchySection
            {
                Name = "Opinion",
                SortOrder = 1,
                UrlPatterns = new List<string> { "/opinion/" },
            },
            new HierarchySection
            {
                Name = "Sports",
                SortOrder = 0,
                UrlPatterns = new List<string> { "/sports/" },
            });

        var tree = await _builder.BuildTreeAsync(links, config);

        var headers = tree.Root.Children.Where(c => c.IsGroupHeader).ToList();
        headers.Should().HaveCount(2);
        headers[0].Link.DisplayText.Should().Be("Sports");
        headers[1].Link.DisplayText.Should().Be("Opinion");
    }

    [Fact]
    public async Task BuildTreeAsync_WithConfig_AppliesStartCollapsed()
    {
        var links = new List<LinkInfo>
        {
            CreateContentLink(url: "https://example.com/sidebar/1", text: "Sidebar 1"),
        };

        var config = CreateConfig(
            new HierarchySection
            {
                Name = "Sidebar",
                SortOrder = 0,
                UrlPatterns = new List<string> { "/sidebar/" },
                StartCollapsed = true,
            });

        var tree = await _builder.BuildTreeAsync(links, config);

        var sidebarHeader = tree.GetAllNodes()
            .FirstOrDefault(n => n.IsGroupHeader && n.Link.DisplayText == "Sidebar");
        sidebarHeader.Should().NotBeNull();
        sidebarHeader!.CollapseState.Should().Be(NodeCollapseState.Collapsed);
    }

    [Fact]
    public async Task BuildTreeAsync_WithConfig_NonContentLinksGetGroupHeaders()
    {
        var links = new List<LinkInfo>
        {
            CreateContentLink(text: "Article"),
            CreateNavLink(),
            new LinkInfo { Url = "https://other.com/page", DisplayText = "External", Type = LinkType.External, ImportanceScore = 20 },
        };

        var config = CreateConfig();

        var tree = await _builder.BuildTreeAsync(links, config);

        var navHeader = tree.GetAllNodes()
            .FirstOrDefault(n => n.IsGroupHeader && n.Link.DisplayText == "Navigation");
        navHeader.Should().NotBeNull();

        var extHeader = tree.GetAllNodes()
            .FirstOrDefault(n => n.IsGroupHeader && n.Link.DisplayText == "External");
        extHeader.Should().NotBeNull();
    }

    [Fact]
    public async Task BuildTreeAsync_WithConfig_EmptySectionsSkipped()
    {
        var links = new List<LinkInfo>
        {
            CreateContentLink(text: "Article"),
        };

        var config = CreateConfig(
            new HierarchySection
            {
                Name = "Empty Section",
                SortOrder = 0,
                UrlPatterns = new List<string> { "/nonexistent/" },
            });

        var tree = await _builder.BuildTreeAsync(links, config);

        var emptyHeader = tree.GetAllNodes()
            .FirstOrDefault(n => n.IsGroupHeader && n.Link.DisplayText == "Empty Section");
        emptyHeader.Should().BeNull();
    }
}
