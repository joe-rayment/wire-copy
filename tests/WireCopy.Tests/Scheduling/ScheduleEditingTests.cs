// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-frpl.14 (B12a) — the render-free editor rules: an unpinned site is
/// never persisted as a step, an existing recipe whose config was deleted degrades
/// to "needs reconfigure" instead of crashing, and step/cadence assembly captures
/// the durable identity.
/// </summary>
[Trait("Category", "Unit")]
public class ScheduleEditingTests
{
    private static SiteHierarchyConfig Config(bool needsReanalyze = false, params string[] sectionNames) => new()
    {
        Domain = "nytimes.com",
        UrlPattern = "^https?://(www\\.)?nytimes\\.com/?$",
        NeedsReanalyze = needsReanalyze,
        Sections = sectionNames.Select((n, i) => new HierarchySection { Name = n, SortOrder = i, ParentSelectors = new() { "section." + n.ToLowerInvariant() } }).ToList(),
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ModelVersion = "t",
    };

    [Theory]
    [InlineData(false, 1, true)]   // configured, has sections → pinnable
    [InlineData(true, 1, false)]   // flagged for re-analysis → blocked
    [InlineData(false, 0, false)]  // no sections → nothing to PIN (whole-page still schedulable, below)
    public void HasPinnableSections_OnlyWhenConfiguredWithSections(bool needsReanalyze, int sectionCount, bool expected)
    {
        var cfg = sectionCount == 0 ? Config(needsReanalyze) : Config(needsReanalyze, "Front");
        ScheduleEditing.HasPinnableSections(cfg).Should().Be(expected);
    }

    [Theory]
    [InlineData(false, 0, true)]   // workspace-42q8.2: a FLAT layout is usable — whole-page steps
    [InlineData(false, 1, true)]
    [InlineData(true, 1, false)]   // re-analysis flag still blocks everything
    public void UsableConfig_AcceptsFlatLayouts_RejectsReanalyzeFlag(bool needsReanalyze, int sectionCount, bool expected)
    {
        var cfg = sectionCount == 0 ? Config(needsReanalyze) : Config(needsReanalyze, "Front");
        ScheduleEditing.UsableConfig(cfg).Should().Be(expected);
    }

    [Fact]
    public void UsableConfig_NullConfig_IsBlocked() =>
        ScheduleEditing.UsableConfig(null).Should().BeFalse("an unconfigured site can never persist a step");

    [Fact]
    public void StepNeedsReconfigure_WhenConfigDeleted_OrSectionGone()
    {
        var step = ScheduleEditing.BuildStep(
            "https://www.nytimes.com/", "nytimes.com", "^x$",
            new HierarchySection { Name = "Business", SortOrder = 1 },
            TakeMode.WholeSection, null, required: true);

        ScheduleEditing.StepNeedsReconfigure(null, step).Should().BeTrue("deleted config");
        ScheduleEditing.StepNeedsReconfigure(Config(false, "Front"), step).Should().BeTrue("section removed (no Business, no SortOrder 1)");
        ScheduleEditing.StepNeedsReconfigure(Config(true, "Front", "Business"), step).Should().BeTrue("flagged for re-analysis");

        ScheduleEditing.StepNeedsReconfigure(Config(false, "Front", "Business"), step).Should().BeFalse("Business still present");
    }

    [Fact]
    public void StepNeedsReconfigure_WholePageStep_FineOnFlatConfig_BrokenOnlyWithoutUsableConfig()
    {
        // workspace-42q8.2: whole-page steps reference no section, so a flat layout
        // (or one whose sections all changed) never flags them.
        var step = ScheduleEditing.BuildWholePageStep(
            "https://www.nytimes.com/", "nytimes.com", "^x$", TakeMode.WholeSection, null, required: true);

        ScheduleEditing.StepNeedsReconfigure(Config(false), step).Should().BeFalse("flat config is exactly what a whole-page step wants");
        ScheduleEditing.StepNeedsReconfigure(Config(false, "Front"), step).Should().BeFalse("sections are irrelevant to a whole-page step");
        ScheduleEditing.StepNeedsReconfigure(null, step).Should().BeTrue("deleted config still breaks it");
        ScheduleEditing.StepNeedsReconfigure(Config(true), step).Should().BeTrue("re-analysis flag still breaks it");
    }

    [Fact]
    public void BuildWholePageStep_CarriesTheWholePageIdentity()
    {
        var step = ScheduleEditing.BuildWholePageStep(
            "https://www.nytimes.com/section/todayspaper", "NYTimes.com", "^nyt$",
            TakeMode.TopN, takeCount: 5, required: false);

        step.Scope.Should().Be(StepScope.WholePage);
        step.SectionName.Should().Be(RecipeStep.WholePageSectionName);
        step.ConfigUrlPattern.Should().Be("^nyt$");
        step.Domain.Should().Be("nytimes.com");
        step.TakeMode.Should().Be(TakeMode.TopN);
        step.TakeCount.Should().Be(5);
        step.Required.Should().BeFalse();
    }

    [Fact]
    public void BuildStep_CapturesDurableSectionIdentity()
    {
        var section = new HierarchySection { Name = "Business", SortOrder = 3, ParentSelectors = new() { "section.business" } };

        var step = ScheduleEditing.BuildStep(
            "https://www.nytimes.com/", "NYTimes.com", "^nyt$", section,
            TakeMode.TopN, takeCount: 5, required: true, headingAliases: new[] { "Sunday Business" });

        step.SectionName.Should().Be("Business");
        step.SortOrderFallback.Should().Be(3);
        step.ConfigUrlPattern.Should().Be("^nyt$");
        step.Domain.Should().Be("nytimes.com");
        step.TakeMode.Should().Be(TakeMode.TopN);
        step.TakeCount.Should().Be(5);
        step.Required.Should().BeTrue();
        step.HeadingAliases.Should().Contain("Sunday Business");
    }

    [Fact]
    public void BuildCadence_RejectsEmptyDaySelection()
    {
        var act = () => ScheduleEditing.BuildCadence(Array.Empty<DayOfWeek>(), new TimeOnly(7, 0));
        act.Should().Throw<ArgumentException>("the UI must keep the user on the cadence step until a day is chosen");
    }

    [Theory]
    [InlineData(7, "Daily 07:00")]
    [InlineData(5, "Mon–Fri 07:00")]
    public void DescribeCadence_SummarizesCommonPatterns(int dayCount, string expected)
    {
        var days = dayCount == 7
            ? Enum.GetValues<DayOfWeek>()
            : new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

        ScheduleEditing.DescribeCadence(ScheduleEditing.BuildCadence(days, new TimeOnly(7, 0))).Should().Be(expected);
    }
}
