// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class ChooserEntryTests
{
    private static SiteHierarchyConfig Config(
        LayoutKind kind = LayoutKind.AiCurated, int sections = 1, bool snapshot = false)
    {
        var secs = Enumerable.Range(0, sections)
            .Select(i => new HierarchySection { Name = $"S{i}", SortOrder = i, ParentSelectors = new List<string> { ".x" } })
            .ToList();
        return new SiteHierarchyConfig
        {
            Domain = "x.com",
            UrlPattern = "^x$",
            Sections = snapshot ? new List<HierarchySection>() : secs,
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "m",
            Kind = kind,
            Strategy = kind == LayoutKind.AiCurated ? "AiCurated" : null,
            Version = snapshot ? 2 : 3,
            AiResult = snapshot
                ? new AiCuratedResult { ExcludedLinkKeys = new(), StoryOrderLinkKeys = new() { "url:x" }, AnalyzedAt = DateTime.UtcNow }
                : null,
        };
    }

    [Fact]
    public void Decide_NullConfig_IsSetupAiFirst()
        => ChooserEntry.Decide(null).Should().Be(ChooserEntry.Mode.SetupAiFirst);

    [Fact]
    public void Decide_SavedConfig_IsConfiguredSummary()
        => ChooserEntry.Decide(Config()).Should().Be(ChooserEntry.Mode.ConfiguredSummary);

    [Theory]
    [InlineData(true, 5, true)]
    [InlineData(false, 5, false)]
    [InlineData(true, 2, false)]
    public void AiSetupAvailable_RequiresKeyAndEnoughLinks(bool configured, int links, bool expected)
        => ChooserEntry.AiSetupAvailable(configured, links).Should().Be(expected);

    [Fact]
    public void DescribeConfig_PatternConfig_SaysPatternBased()
        => ChooserEntry.DescribeConfig(Config(sections: 2)).Should().Contain("pattern-based");

    [Fact]
    public void DescribeConfig_LegacySnapshot_PromptsReRun()
        => ChooserEntry.DescribeConfig(Config(snapshot: true)).Should().Contain("press r to re-run");

    [Fact]
    public void DescribeConfig_DocumentOrder_SaysDocumentOrder()
        => ChooserEntry.DescribeConfig(Config(kind: LayoutKind.DocumentOrder, sections: 0)).Should().Contain("Document order");
}
