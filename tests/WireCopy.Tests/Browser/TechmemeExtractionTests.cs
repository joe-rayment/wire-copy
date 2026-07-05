// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;
using Xunit.Abstractions;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-romy.9 — regression net for the "default techmeme shows no
/// articles" report, pinned to real techmeme markup (Fixtures/
/// techmeme-2026-06-12.html, captured live). The default (non-AI) flow must
/// surface the story river as Content links via the aggregator promotion pass.
/// </summary>
[Trait("Category", "Unit")]
public class TechmemeExtractionTests
{
    private readonly ITestOutputHelper _output;

    public TechmemeExtractionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "techmeme-2026-06-12.html");

    [Fact]
    public async Task RealMemeorandumMarkup_ContentLinksCarryDerivableParents()
    {
        var html = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "memeorandum-2026-06-12.html"));
        var extractor = new LinkExtractor(Substitute.For<Microsoft.Extensions.Logging.ILogger<LinkExtractor>>());

        var links = await extractor.ExtractLinksAsync(html, "https://www.memeorandum.com/");

        var content = links.Where(l => l.Type == LinkType.Content).ToList();
        var storyShaped = content.Where(l => l.DisplayText.Length >= LinkExtractor.MinStoryTextLength).ToList();
        _output.WriteLine($"total={links.Count} content={content.Count} storyShaped={storyShaped.Count}");
        foreach (var group in content.GroupBy(l => l.ParentSelector ?? "-").OrderByDescending(g => g.Count()).Take(12))
        {
            _output.WriteLine($"  {group.Count(),4} x parent: {group.Key}");
        }

        storyShaped.Count.Should().BeGreaterThanOrEqualTo(20);
    }

    [Fact]
    public async Task RealTechmemeMarkup_PromotesRiverStoriesToContent()
    {
        var html = await File.ReadAllTextAsync(FixturePath);
        var extractor = new LinkExtractor(Substitute.For<Microsoft.Extensions.Logging.ILogger<LinkExtractor>>());

        var links = await extractor.ExtractLinksAsync(html, "https://www.techmeme.com/");

        var content = links.Where(l => l.Type == LinkType.Content).ToList();
        var external = links.Where(l => l.Type == LinkType.External).ToList();
        var storyShaped = content.Where(l => l.DisplayText.Length >= LinkExtractor.MinStoryTextLength).ToList();

        _output.WriteLine($"total={links.Count} content={content.Count} external={external.Count} storyShaped={storyShaped.Count}");
        foreach (var l in content.Take(12))
        {
            _output.WriteLine($"  [{l.ImportanceScore}]{(l.IsSponsored ? " SPONSOR" : string.Empty)} {l.DisplayText[..Math.Min(70, l.DisplayText.Length)]} :: {l.ParentSelector}");
        }

        // The river must reach the tree as Content — this is the exact
        // user-visible failure ("default shows no articles") if it regresses.
        storyShaped.Count.Should().BeGreaterThanOrEqualTo(20,
            "techmeme's river stories must be promoted to Content for the default tree");
    }

    [Fact]
    public async Task SectionHeadings_AreCaptured_ForAds_News_AndPodcasts()
    {
        // workspace-rpop.1: the <h2> section labels (Sponsor Posts / Top News /
        // Featured Podcasts) sit in plain <div id="{random}"> wrappers, so they
        // used to be dropped. They are the strongest, DURABLE ad/rail signal — an
        // ad under "Sponsor Posts" is an ad regardless of its (volatile) markup.
        var html = await File.ReadAllTextAsync(FixturePath);
        var extractor = new LinkExtractor(Substitute.For<Microsoft.Extensions.Logging.ILogger<LinkExtractor>>());

        var links = await extractor.ExtractLinksAsync(html, "https://www.techmeme.com/");

        // The Sponsor Posts <h2> now labels the ad block (the durable ad signal).
        var sponsorLinks = links.Where(l => l.SectionTitle == "Sponsor Posts").ToList();
        sponsorLinks.Should().NotBeEmpty("the Sponsor Posts heading now labels the ad block");
        sponsorLinks.Should().Contain(
            l => l.DisplayText.Contains("Agentic AI", StringComparison.Ordinal) || l.DisplayText.Contains("Soxton", StringComparison.Ordinal),
            "the sponsor block's links carry sect=Sponsor Posts");

        links.Should().Contain(l => l.SectionTitle == "Top News", "the news column carries its Top News heading");
        links.Should().Contain(l => l.SectionTitle == "Featured Podcasts", "the podcast rail carries its heading");
        links.Count(l => l.SectionTitle != null).Should().BeGreaterThan(20,
            "headings are now captured broadly, not left empty as before");
    }
}
