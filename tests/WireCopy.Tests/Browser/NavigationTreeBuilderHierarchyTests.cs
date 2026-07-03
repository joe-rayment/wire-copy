// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

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
        // workspace-vwkt: only the matched link is kept; unmatched links are dropped.
        var sectionHeaders = nodes.Where(n => n.IsGroupHeader && n.Link.DisplayText == "Main Articles").ToList();
        sectionHeaders.Should().HaveCount(1);
        sectionHeaders[0].Children.Should().HaveCount(1);
    }

    [Fact]
    public async Task BuildTreeAsync_LeadSectionMaxLinks_CapsLeadAndReoffersOverflow()
    {
        // workspace-9wm6: a capped lead keeps only the top story; the greedy
        // builder re-offers the overflow to the next section, so nothing is lost.
        var links = new List<LinkInfo>
        {
            CreateContentLink(url: "https://example.com/a", text: "A", parentSelector: "div.col > div.item > strong"),
            CreateContentLink(url: "https://example.com/b", text: "B", parentSelector: "div.col > div.item > strong"),
            CreateContentLink(url: "https://example.com/c", text: "C", parentSelector: "div.col > div.item > strong"),
        };

        var config = CreateConfig(
            new HierarchySection { Name = "Lead", SortOrder = 0, ParentSelectors = new() { "div.item > strong" }, MaxLinks = 1 },
            new HierarchySection { Name = "Feed", SortOrder = 1, ParentSelectors = new() { "div.col" } });

        var tree = await _builder.BuildTreeAsync(links, config);

        var nodes = tree.GetAllNodes().ToList();
        var lead = nodes.Single(n => n.IsGroupHeader && n.Link.DisplayText == "Lead");
        var feed = nodes.Single(n => n.IsGroupHeader && n.Link.DisplayText == "Feed");
        lead.Children.Should().HaveCount(1, "the capped lead keeps only the top story");
        feed.Children.Should().HaveCount(2, "the overflow is re-offered to the next section, not dropped");
    }

    [Fact]
    public async Task BuildTreeAsync_WithConfig_DropsLinksMatchingExcludeRules()
    {
        // workspace-5oe9.1: durable exclusion drops ads/promos by selector and
        // url-pattern BEFORE tiering, so they never reach the tree.
        var links = new List<LinkInfo>
        {
            CreateContentLink(url: "https://example.com/story1", text: "Real Story", parentSelector: "main .lead a"),
            CreateContentLink(url: "https://example.com/sponsored/ad1", text: "Sponsored", parentSelector: "main .lead a"),
            CreateContentLink(url: "https://example.com/story2", text: "Promo Box", parentSelector: "div.promo a"),
        };

        var config = CreateConfig(
            new HierarchySection
            {
                Name = "Lead",
                SortOrder = 0,
                ParentSelectors = new List<string> { "main .lead" },
            }) with
        {
            ExcludeUrlPatterns = new List<string> { "/sponsored/" },
            ExcludeSelectors = new List<string> { ".promo" },
        };

        var tree = await _builder.BuildTreeAsync(links, config);

        var allText = tree.GetAllNodes().Select(n => n.Link.DisplayText).ToList();
        allText.Should().Contain("Real Story");
        allText.Should().NotContain("Sponsored");
        allText.Should().NotContain("Promo Box");

        var leadHeader = tree.GetAllNodes()
            .FirstOrDefault(n => n.IsGroupHeader && n.Link.DisplayText == "Lead");
        leadHeader.Should().NotBeNull();
        leadHeader!.Children.Should().HaveCount(1);
        leadHeader.Children[0].Link.DisplayText.Should().Be("Real Story");
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
    public async Task BuildTreeAsync_WithConfig_UnmatchedLinksDropped()
    {
        // workspace-vwkt: legacy AiHierarchical used to append unmatched content links
        // at the root bottom. That allowed ad/junk links to leak into an AI-curated tree,
        // so the new behavior drops unmatched content links to match AiCurated semantics.
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

        // Root's direct children: only the "Top" section header — unmatched links are dropped.
        tree.Root.Children.Should().HaveCount(1);
        tree.Root.Children.Count(c => !c.IsGroupHeader).Should().Be(0);

        var topHeader = tree.Root.Children.Single(c => c.IsGroupHeader && c.Link.DisplayText == "Top");
        topHeader.Children.Should().HaveCount(1);
        topHeader.Children[0].Link.DisplayText.Should().Be("Matched");
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

    [Fact]
    public async Task BuildTreeAsync_StaleConfig_FallsBackToDocumentOrder_AndFlagsIt()
    {
        // workspace-9k27.1: a redesign killed every saved selector — the tree
        // must NOT render empty. Fall back to document order and flag stale.
        var links = Enumerable.Range(0, 20)
            .Select(i => CreateContentLink(
                url: $"https://example.com/story{i}",
                text: $"Story {i}",
                parentSelector: "div.newLayout2026 a"))
            .ToList();

        var config = CreateConfig(
            new HierarchySection { Name = "Lead", SortOrder = 0, ParentSelectors = new() { "div.oldLayout a" } });

        var tree = await _builder.BuildTreeAsync(links, config);

        tree.HierarchyConfigStale.Should().BeTrue("zero sections matched — the config is stale");
        tree.GetAllNodes().Count(n => !n.IsGroupHeader).Should().Be(20, "document-order fallback keeps every story visible");
    }

    [Fact]
    public async Task BuildTreeAsync_HealthyConfig_IsNotFlaggedStale()
    {
        var links = Enumerable.Range(0, 10)
            .Select(i => CreateContentLink(url: $"https://example.com/s{i}", text: $"S{i}", parentSelector: "main .feed a"))
            .ToList();
        var config = CreateConfig(
            new HierarchySection { Name = "Feed", SortOrder = 0, ParentSelectors = new() { ".feed" } });

        var tree = await _builder.BuildTreeAsync(links, config);

        tree.HierarchyConfigStale.Should().BeFalse();
    }

    [Fact]
    public async Task BuildTreeAsync_ExcludeRuleThatBroadenedSinceSave_IsSkippedOnRevisit()
    {
        // workspace-9k27.1: a save-time-surgical exclude that now matches most of
        // the page (redesign) is skipped at build time — stories stay visible.
        var links = Enumerable.Range(0, 12)
            .Select(i => CreateContentLink(
                url: $"https://example.com/story{i}",
                text: $"Story {i}",
                parentSelector: "div.content div.item a"))
            .ToList();

        var config = CreateConfig(
            new HierarchySection { Name = "Feed", SortOrder = 0, ParentSelectors = new() { "div.item" } }) with
        {
            // Was surgical at save time; after the redesign it matches EVERY story.
            ExcludeSelectors = new List<string> { "div.content" },
        };

        var tree = await _builder.BuildTreeAsync(links, config);

        tree.HierarchyConfigStale.Should().BeFalse();
        tree.GetAllNodes().Count(n => !n.IsGroupHeader).Should().Be(12, "the over-broad exclude is skipped this visit, not applied");
    }

    [Fact]
    public async Task BuildTreeAsync_CappedLead_LiftsCap_WhenOverflowNoLongerClaimedDownstream()
    {
        // workspace-9k27.3: the save-time "every overflow link is claimed by a
        // later section" promise broke (downstream selector died in a redesign).
        // The cap must be lifted so N-1 stories aren't silently dropped.
        var links = new List<LinkInfo>
        {
            CreateContentLink(url: "https://example.com/a", text: "A", parentSelector: "div.col > div.item > strong"),
            CreateContentLink(url: "https://example.com/b", text: "B", parentSelector: "div.col > div.item > strong"),
            CreateContentLink(url: "https://example.com/c", text: "C", parentSelector: "div.col > div.item > strong"),
        };

        var config = CreateConfig(
            new HierarchySection { Name = "Lead", SortOrder = 0, ParentSelectors = new() { "div.item > strong" }, MaxLinks = 1 },
            new HierarchySection { Name = "Feed", SortOrder = 1, ParentSelectors = new() { "div.deadSelector2020" } });

        var tree = await _builder.BuildTreeAsync(links, config);

        var nodes = tree.GetAllNodes().ToList();
        var lead = nodes.Single(n => n.IsGroupHeader && n.Link.DisplayText == "Lead");
        lead.Children.Should().HaveCount(3, "the cap is lifted when the overflow would otherwise be dropped");
        nodes.Count(n => !n.IsGroupHeader).Should().Be(3, "no story is ever lost — now verified per-visit, not just at save time");
    }
}
