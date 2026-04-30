// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// End-to-end integration tests that validate section extraction through
/// tree building using curated HTML fixtures from real-world page structures.
/// </summary>
[Trait("Category", "Unit")]
public class SectionExtractionIntegrationTests
{
    private readonly LinkExtractor _extractor;

    public SectionExtractionIntegrationTests()
    {
        var logger = Substitute.For<ILogger<LinkExtractor>>();
        _extractor = new LinkExtractor(logger);
    }

    #region NYT-style: semantic sections with aria-labels

    [Fact]
    public async Task NytStyle_SemanticSections_ExtractsSectionTitles()
    {
        var html = NytStyleFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://www.nytimes.com");

        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        contentLinks.Should().HaveCountGreaterOrEqualTo(4);

        // Links should have SectionTitle from aria-label
        contentLinks.Where(l => l.SectionTitle == "Top Stories").Should().NotBeEmpty();
        contentLinks.Where(l => l.SectionTitle == "Opinion").Should().NotBeEmpty();
    }

    [Fact]
    public async Task NytStyle_TreeBuilding_CreatesSubSectionHeaders()
    {
        var html = NytStyleFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://www.nytimes.com");
        var grouped = GroupByType(links);
        var tree = NavigationTree.BuildWithGroups(grouped);

        var flatNodes = FlattenNodes(tree.Root);

        // Should have sub-section headers for each section
        var subHeaders = flatNodes
            .Where(n => n.Link.HeaderType == HeaderType.SubSection)
            .Select(n => n.Link.DisplayText)
            .ToList();

        subHeaders.Should().Contain("Top Stories");
        subHeaders.Should().Contain("Opinion");
    }

    [Fact]
    public async Task NytStyle_LinksGroupedUnderCorrectSections()
    {
        var html = NytStyleFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://www.nytimes.com");
        var grouped = GroupByType(links);
        var tree = NavigationTree.BuildWithGroups(grouped);

        var flatNodes = FlattenNodes(tree.Root);

        // Find "Top Stories" header and verify its children are the right articles
        var topStoriesHeader = flatNodes.FirstOrDefault(
            n => n.Link.HeaderType == HeaderType.SubSection && n.Link.DisplayText == "Top Stories");
        topStoriesHeader.Should().NotBeNull();

        var topStoriesChildren = topStoriesHeader!.Children
            .Where(c => !c.Link.IsGroupHeader)
            .Select(c => c.Link.DisplayText)
            .ToList();
        topStoriesChildren.Should().Contain(t => t.Contains("Breaking News"));
        topStoriesChildren.Should().Contain(t => t.Contains("Economy Report"));
    }

    #endregion

    #region BBC-style: mixed semantic and div sections

    [Fact]
    public async Task BbcStyle_MixedSections_ExtractsSectionTitles()
    {
        var html = BbcStyleFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://www.bbc.com");

        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        contentLinks.Should().HaveCountGreaterOrEqualTo(4);

        contentLinks.Where(l => l.SectionTitle == "World News").Should().NotBeEmpty();
        contentLinks.Where(l => l.SectionTitle == "Business").Should().NotBeEmpty();
    }

    [Fact]
    public async Task BbcStyle_TreeBuilding_GroupsCorrectly()
    {
        var html = BbcStyleFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://www.bbc.com");
        var grouped = GroupByType(links);
        var tree = NavigationTree.BuildWithGroups(grouped);

        var flatNodes = FlattenNodes(tree.Root);
        var subHeaders = flatNodes
            .Where(n => n.Link.HeaderType == HeaderType.SubSection)
            .Select(n => n.Link.DisplayText)
            .ToList();

        subHeaders.Should().Contain("World News");
        subHeaders.Should().Contain("Business");
    }

    #endregion

    #region Flat structure: no meaningful sections (Macleans-style)

    [Fact]
    public async Task FlatStructure_NoSections_ExtractsLinksWithoutSectionTitles()
    {
        var html = FlatDivFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://macleans.ca");

        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        contentLinks.Should().NotBeEmpty();

        // No section titles should be detected (plain divs, not semantic sections)
        contentLinks.All(l => l.SectionTitle == null).Should().BeTrue(
            "flat div structure has no semantic section containers");
    }

    [Fact]
    public async Task FlatStructure_TreeBuilding_NoSubSectionHeaders()
    {
        var html = FlatDivFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://macleans.ca");
        var grouped = GroupByType(links);
        var tree = NavigationTree.BuildWithGroups(grouped);

        var flatNodes = FlattenNodes(tree.Root);
        var subHeaders = flatNodes
            .Where(n => n.Link.HeaderType == HeaderType.SubSection)
            .ToList();

        subHeaders.Should().BeEmpty("flat structure should not produce sub-section headers");
    }

    #endregion

    #region Minimal HTML: Hacker News style

    [Fact]
    public async Task MinimalHtml_NoSections_ExtractsLinks()
    {
        var html = HackerNewsStyleFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://news.ycombinator.com");

        links.Should().NotBeEmpty();
        links.All(l => l.SectionTitle == null).Should().BeTrue(
            "minimal table-based layout has no semantic sections");
    }

    [Fact]
    public async Task MinimalHtml_TreeBuilding_FlatLinkList()
    {
        var html = HackerNewsStyleFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://news.ycombinator.com");
        var grouped = GroupByType(links);
        var tree = NavigationTree.BuildWithGroups(grouped);

        var flatNodes = FlattenNodes(tree.Root);
        flatNodes.Where(n => n.Link.HeaderType == HeaderType.SubSection).Should().BeEmpty();
    }

    #endregion

    #region Blog: single h1, no sub-sections

    [Fact]
    public async Task BlogStyle_SingleSection_NoSubGrouping()
    {
        var html = BlogStyleFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://blog.example.com");

        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        contentLinks.Should().NotBeEmpty();

        // Only one section = no sub-grouping (needs 2+ distinct sections)
        var distinctSections = contentLinks
            .Where(l => l.SectionTitle != null)
            .Select(l => l.SectionTitle)
            .Distinct()
            .ToList();
        distinctSections.Should().HaveCountLessOrEqualTo(1,
            "single section should not trigger sub-grouping");
    }

    [Fact]
    public async Task BlogStyle_TreeBuilding_NoSubSectionHeaders()
    {
        var html = BlogStyleFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://blog.example.com");
        var grouped = GroupByType(links);
        var tree = NavigationTree.BuildWithGroups(grouped);

        var flatNodes = FlattenNodes(tree.Root);
        flatNodes.Where(n => n.Link.HeaderType == HeaderType.SubSection).Should().BeEmpty(
            "single section does not meet 2+ distinct sections threshold");
    }

    #endregion

    #region Partial extraction: some sections detected, some not

    [Fact]
    public async Task PartialExtraction_MixedSectionsAndFlat_GracefulDegradation()
    {
        var html = PartialSectionFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://news.example.com");

        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();
        contentLinks.Should().HaveCountGreaterOrEqualTo(5);

        // Some links have sections, some don't
        var withSection = contentLinks.Where(l => l.SectionTitle != null).ToList();
        var withoutSection = contentLinks.Where(l => l.SectionTitle == null).ToList();

        withSection.Should().NotBeEmpty("semantic sections should be detected");
        withoutSection.Should().NotBeEmpty("links outside sections should have null SectionTitle");
    }

    [Fact]
    public async Task PartialExtraction_TreeBuilding_HeaderlessFallthrough()
    {
        var html = PartialSectionFixture;
        var links = await _extractor.ExtractLinksAsync(html, "https://news.example.com");
        var grouped = GroupByType(links);
        var tree = NavigationTree.BuildWithGroups(grouped);

        var flatNodes = FlattenNodes(tree.Root);

        // Should have sub-section headers for the detected sections
        var subHeaders = flatNodes
            .Where(n => n.Link.HeaderType == HeaderType.SubSection)
            .Select(n => n.Link.DisplayText)
            .ToList();
        subHeaders.Should().Contain("Breaking News");
        subHeaders.Should().Contain("Technology");

        // Links without sections should appear as direct children of root (headerless flow)
        var directContentChildren = tree.Root.Children
            .Where(c => !c.Link.IsGroupHeader && c.Link.Type == LinkType.Content)
            .ToList();
        directContentChildren.Should().NotBeEmpty(
            "links outside semantic sections appear as headerless flow");
    }

    #endregion

    #region Generic heading filtering

    [Fact]
    public async Task GenericHeadings_AreFiltered()
    {
        var html = @"
<html><body><main>
    <section><h2>Trending</h2>
        <a href=""https://example.com/t1"">Trending Article One With a Longer Title for Testing</a>
        <a href=""https://example.com/t2"">Trending Article Two With a Longer Title for Testing</a>
    </section>
    <section><h2>Science</h2>
        <a href=""https://example.com/s1"">Science Article One About New Research Discovery</a>
        <a href=""https://example.com/s2"">Science Article Two About Climate Change Impact</a>
    </section>
    <section><h2>Health</h2>
        <a href=""https://example.com/h1"">Health Article One About Medical Breakthroughs Today</a>
        <a href=""https://example.com/h2"">Health Article Two About Public Health Policy Update</a>
    </section>
</main></body></html>";

        var links = await _extractor.ExtractLinksAsync(html, "https://example.com");
        var contentLinks = links.Where(l => l.Type == LinkType.Content).ToList();

        // "Trending" should be filtered out as a generic heading
        contentLinks.Where(l => l.SectionTitle == "Trending").Should().BeEmpty();

        // "Science" and "Health" should be kept
        contentLinks.Where(l => l.SectionTitle == "Science").Should().NotBeEmpty();
        contentLinks.Where(l => l.SectionTitle == "Health").Should().NotBeEmpty();
    }

    #endregion

    #region Helpers

    private static Dictionary<LinkType, List<LinkInfo>> GroupByType(List<LinkInfo> links)
    {
        return links.GroupBy(l => l.Type).ToDictionary(g => g.Key, g => g.ToList());
    }

    private static List<LinkNode> FlattenNodes(LinkNode root)
    {
        var result = new List<LinkNode>();
        foreach (var child in root.Children)
        {
            result.Add(child);
            result.AddRange(FlattenNodes(child));
        }

        return result;
    }

    #endregion

    #region HTML Fixtures

    private const string NytStyleFixture = @"
<html>
<head><title>The New York Times</title></head>
<body>
    <header>
        <nav><a href=""/section/us"">U.S.</a><a href=""/section/world"">World</a></nav>
    </header>
    <main>
        <section aria-label=""Top Stories"">
            <h2>Top Stories</h2>
            <a href=""https://www.nytimes.com/2024/01/15/us/breaking-news.html"">Breaking News Article About Major Events Unfolding Today</a>
            <a href=""https://www.nytimes.com/2024/01/15/business/economy-report.html"">Economy Report Shows Quarterly Growth Exceeding Expectations</a>
            <a href=""https://www.nytimes.com/2024/01/15/us/politics/policy-update.html"">Policy Update on Infrastructure Bill Moving Through Congress</a>
        </section>
        <section aria-label=""Opinion"">
            <h2>Opinion</h2>
            <a href=""https://www.nytimes.com/2024/01/15/opinion/editorial-board.html"">Editorial Board Column on the State of American Democracy</a>
            <a href=""https://www.nytimes.com/2024/01/15/opinion/guest-essay.html"">Guest Essay on Climate Change and Its Global Consequences</a>
            <a href=""https://www.nytimes.com/2024/01/15/opinion/letters.html"">Letters to the Editor: Readers Respond to Recent Coverage</a>
        </section>
    </main>
    <footer>
        <a href=""/privacy"">Privacy Policy</a>
        <a href=""/terms"">Terms of Service</a>
    </footer>
</body>
</html>";

    private const string BbcStyleFixture = @"
<html>
<head><title>BBC News</title></head>
<body>
    <header><nav><a href=""/news"">Home</a><a href=""/sport"">Sport</a></nav></header>
    <main>
        <section>
            <h2>World News</h2>
            <article><a href=""https://www.bbc.com/news/world-europe-123"">European Leaders Gather for Critical Summit on Trade Policy</a></article>
            <article><a href=""https://www.bbc.com/news/world-asia-456"">Asia Pacific Region Report on Economic Growth and Development</a></article>
            <article><a href=""https://www.bbc.com/news/world-africa-789"">African Union Meeting Addresses Continental Security Concerns</a></article>
        </section>
        <div role=""region"" aria-label=""Business"">
            <a href=""https://www.bbc.com/news/business-101"">Global Markets Today Show Strong Recovery Across All Sectors</a>
            <a href=""https://www.bbc.com/news/business-102"">Tech Earnings Report Shows Record Quarter for Major Companies</a>
            <a href=""https://www.bbc.com/news/business-103"">International Trade Update: New Agreements Shape Global Commerce</a>
        </div>
    </main>
    <footer><a href=""/terms"">Terms</a></footer>
</body>
</html>";

    private const string FlatDivFixture = @"
<html>
<head><title>Macleans Magazine</title></head>
<body>
    <div class=""site-header""><a href=""/"">Home</a></div>
    <main>
        <div class=""content-grid"">
            <div class=""card""><a href=""https://macleans.ca/politics/federal-budget"">Federal Budget Analysis: What It Means for Canadians</a></div>
            <div class=""card""><a href=""https://macleans.ca/culture/film-review"">Film Review: The Latest Release You Need to See</a></div>
            <div class=""card""><a href=""https://macleans.ca/society/healthcare-reform"">Healthcare Reform Debate Heats Up Across the Country</a></div>
            <div class=""card""><a href=""https://macleans.ca/economy/housing-crisis"">Housing Crisis Deep Dive Into Urban Markets Nationwide</a></div>
        </div>
    </main>
    <footer><a href=""/about"">About</a></footer>
</body>
</html>";

    private const string HackerNewsStyleFixture = @"
<html>
<head><title>Hacker News</title></head>
<body>
    <table>
        <tr><td><a href=""https://news.ycombinator.com/item?id=1"">Show HN: A New Database Engine</a></td></tr>
        <tr><td><a href=""https://news.ycombinator.com/item?id=2"">Why Rust is the Future</a></td></tr>
        <tr><td><a href=""https://news.ycombinator.com/item?id=3"">Launch HN: AI Code Review Tool</a></td></tr>
    </table>
</body>
</html>";

    private const string BlogStyleFixture = @"
<html>
<head><title>Tech Blog</title></head>
<body>
    <header><h1>My Tech Blog</h1><nav><a href=""/"">Home</a><a href=""/about"">About</a></nav></header>
    <main>
        <article>
            <h2>Recent Posts</h2>
            <a href=""https://blog.example.com/2024/rust-guide"">Getting Started with Rust: A Comprehensive Beginner Guide</a>
            <a href=""https://blog.example.com/2024/docker-tips"">Docker Tips and Tricks for Production Deployment Success</a>
            <a href=""https://blog.example.com/2024/go-patterns"">Go Design Patterns Every Developer Should Know and Practice</a>
        </article>
    </main>
    <footer><a href=""/privacy"">Privacy</a></footer>
</body>
</html>";

    private const string PartialSectionFixture = @"
<html>
<head><title>News Aggregator</title></head>
<body>
    <header><a href=""/"">Home</a></header>
    <main>
        <!-- Links outside any section (headerless flow) -->
        <div class=""featured"">
            <a href=""https://news.example.com/featured-story"">Featured Story of the Day: An In-Depth Investigation</a>
            <a href=""https://news.example.com/editors-pick"">Editor's Pick: The Most Important Story This Week</a>
        </div>

        <section aria-label=""Breaking News"">
            <h2>Breaking News</h2>
            <a href=""https://news.example.com/breaking/1"">Breaking Story One: Major Events Unfolding Right Now</a>
            <a href=""https://news.example.com/breaking/2"">Breaking Story Two: Government Response to the Crisis</a>
        </section>

        <!-- More links outside sections -->
        <div class=""content"">
            <a href=""https://news.example.com/sidebar-item"">Sidebar Article: An Analysis of Current Political Trends</a>
        </div>

        <section aria-label=""Technology"">
            <h2>Technology</h2>
            <a href=""https://news.example.com/tech/1"">Tech Story One: New Advances in Artificial Intelligence Research</a>
            <a href=""https://news.example.com/tech/2"">Tech Story Two: How Quantum Computing Changes Everything</a>
        </section>
    </main>
    <footer><a href=""/contact"">Contact</a></footer>
</body>
</html>";

    #endregion
}
