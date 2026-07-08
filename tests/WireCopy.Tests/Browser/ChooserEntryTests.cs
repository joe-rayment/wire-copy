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

    [Theory]
    // analyzerPresent, analyzerConfigured, startWithLabelMode -> blocked?
    [InlineData(true, true, false, false)]   // configured, AI entry -> proceeds
    [InlineData(true, true, true, false)]    // configured, label mode -> proceeds
    [InlineData(true, false, false, true)]   // no key, AI entry -> BLOCKED (key required)
    [InlineData(true, false, true, false)]   // no key, label mode -> PROCEEDS (deterministic, ss5y fix)
    [InlineData(false, false, true, true)]   // no analyzer at all -> blocked even in label mode
    [InlineData(false, true, true, true)]    // no analyzer instance -> blocked regardless
    public void WizardBlockedWithoutKey_GatesAiEntriesOnly(
        bool present, bool configured, bool labelMode, bool expectedBlocked)
        => ChooserEntry.WizardBlockedWithoutKey(present, configured, labelMode).Should().Be(expectedBlocked);

    [Fact]
    public void DescribeConfig_PatternConfig_SaysPatternBased()
        => ChooserEntry.DescribeConfig(Config(sections: 2)).Should().Contain("pattern-based");

    [Fact]
    public void DescribeConfig_LegacySnapshot_PromptsReRun()
        => ChooserEntry.DescribeConfig(Config(snapshot: true)).Should().Contain("press r to re-run");

    [Fact]
    public void DescribeConfig_DocumentOrder_SaysDocumentOrder()
        => ChooserEntry.DescribeConfig(Config(kind: LayoutKind.DocumentOrder, sections: 0)).Should().Contain("Document order");

    // ---- workspace-v2m8.3: the Refine label claims fix-keeping only when true ----

    [Fact]
    public void RefineOptionLabel_NoFixes_MakesNoKeepClaim()
        => ChooserEntry.RefineOptionLabel(Config())
            .Should().Be("Refine the layout with AI", "there are no fixes to keep");

    [Fact]
    public void RefineOptionLabel_OneLabel_SaysOneFix()
    {
        var config = Config() with
        {
            UserLabels = new List<UserLinkLabel>
            {
                new() { Url = "https://x.com/a", Text = "t", Kind = LinkLabelKind.Ad, LabeledAt = DateTime.UtcNow },
            },
        };

        ChooserEntry.RefineOptionLabel(config).Should().Contain("keeps your 1 fix").And.NotContain("fixes");
    }

    [Fact]
    public void RefineOptionLabel_LabelsPlusInstructions_CountsBoth()
    {
        var config = Config() with
        {
            UserLabels = new List<UserLinkLabel>
            {
                new() { Url = "https://x.com/a", Text = "t", Kind = LinkLabelKind.Ad, LabeledAt = DateTime.UtcNow },
                new() { Url = "https://x.com/b", Text = "t", Kind = LinkLabelKind.Menu, LabeledAt = DateTime.UtcNow },
            },
            UserInstructions = new List<string> { "hide the podcasts" },
        };

        ChooserEntry.RefineOptionLabel(config).Should().Contain("keeps your 3 fixes");
    }
}
