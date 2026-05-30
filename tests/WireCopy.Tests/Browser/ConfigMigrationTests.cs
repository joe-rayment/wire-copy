// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

[Trait("Category", "Unit")]
public class ConfigMigrationTests
{
    private static SiteHierarchyConfig Cfg(
        int version, LayoutKind kind, int sectionCount, bool hasAiResult, bool needsReanalyze = false)
    {
        var sections = Enumerable.Range(0, sectionCount)
            .Select(i => new HierarchySection { Name = $"S{i}", SortOrder = i, ParentSelectors = new List<string> { ".x" } })
            .ToList();

        return new SiteHierarchyConfig
        {
            Domain = "x.com",
            UrlPattern = "^x$",
            Sections = sections,
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "m",
            Kind = kind,
            Version = version,
            Strategy = kind == LayoutKind.AiCurated ? "AiCurated" : null,
            AiResult = hasAiResult
                ? new AiCuratedResult { ExcludedLinkKeys = new(), StoryOrderLinkKeys = new() { "url:x" }, AnalyzedAt = DateTime.UtcNow }
                : null,
            NeedsReanalyze = needsReanalyze,
        };
    }

    [Fact]
    public void LegacyV2Snapshot_IsLegacyAndNeedsReanalysis()
    {
        var config = Cfg(version: 2, kind: LayoutKind.AiCurated, sectionCount: 0, hasAiResult: true);
        ConfigMigration.IsLegacySnapshot(config).Should().BeTrue();
        ConfigMigration.NeedsReanalysis(config).Should().BeTrue();
    }

    [Fact]
    public void V1AiHierarchicalWithSections_IsNotSweptIn()
    {
        // Sections present, no AiResult => already on the pattern path.
        var config = Cfg(version: 1, kind: LayoutKind.AiHierarchical, sectionCount: 2, hasAiResult: false);
        ConfigMigration.IsLegacySnapshot(config).Should().BeFalse();
        ConfigMigration.NeedsReanalysis(config).Should().BeFalse();
    }

    [Fact]
    public void V3PatternConfig_IsNotLegacy()
    {
        var config = Cfg(version: 3, kind: LayoutKind.AiCurated, sectionCount: 2, hasAiResult: false);
        ConfigMigration.IsLegacySnapshot(config).Should().BeFalse();
        ConfigMigration.NeedsReanalysis(config).Should().BeFalse();
    }

    [Fact]
    public void V3SelfTestFailedFallback_NeedsReanalysisButIsNotLegacy()
    {
        // The fresh-analysis self-test-fail shape from B5: Version 3, no
        // sections, snapshot kept, NeedsReanalyze flag set.
        var config = Cfg(version: 3, kind: LayoutKind.AiCurated, sectionCount: 0, hasAiResult: true, needsReanalyze: true);
        ConfigMigration.IsLegacySnapshot(config).Should().BeFalse("Version 3 is not a legacy snapshot");
        ConfigMigration.NeedsReanalysis(config).Should().BeTrue("the explicit flag still requests a re-run");
    }
}
