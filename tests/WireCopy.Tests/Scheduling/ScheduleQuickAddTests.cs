// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using Xunit;

namespace WireCopy.Tests.Scheduling;

/// <summary>
/// workspace-42q8.4 — the pure WHAT-option builder behind the g s quick-add card:
/// cursor's section first, the rest in order, "All stories" always last.
/// </summary>
[Trait("Category", "Unit")]
public class ScheduleQuickAddTests
{
    private static SiteHierarchyConfig Config(params string[] sectionNames) => new()
    {
        Domain = "nyt.example",
        UrlPattern = "^https?://(www\\.)?nyt\\.example/?",
        Sections = sectionNames
            .Select((n, i) => new HierarchySection { Name = n, SortOrder = i })
            .ToList(),
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        ModelVersion = "test",
    };

    [Fact]
    public void CursorInsideASection_ThatSectionLeads_MarkedAsCurrent()
    {
        var choices = ScheduleCommandHandler.BuildQuickAddChoices(Config("Top Stories", "Business", "Opinion"), "Business");

        choices.Should().HaveCount(4);
        choices[0].Section!.Name.Should().Be("Business");
        choices[0].Label.Should().StartWith("▸").And.Contain("the section you're on");
        choices[1].Section!.Name.Should().Be("Top Stories");
        choices[2].Section!.Name.Should().Be("Opinion");
        choices[3].Section.Should().BeNull("the whole-page option is always last");
        choices[3].Label.Should().Contain(RecipeStep.WholePageSectionName);
    }

    [Fact]
    public void NoCursorSection_SectionsInSortOrder_WholePageLast()
    {
        var choices = ScheduleCommandHandler.BuildQuickAddChoices(Config("Top Stories", "Business"), null);

        choices.Select(c => c.Section?.Name).Should().Equal("Top Stories", "Business", null);
    }

    [Fact]
    public void CursorSectionUnknownToTheConfig_BehavesLikeNoCursor()
    {
        var choices = ScheduleCommandHandler.BuildQuickAddChoices(Config("Top Stories"), "Some Auto Group");

        choices.Select(c => c.Section?.Name).Should().Equal("Top Stories", null);
        choices.Should().OnlyContain(c => !c.Label.StartsWith("▸"));
    }

    [Fact]
    public void CursorSectionMatch_IsCaseInsensitive()
    {
        var choices = ScheduleCommandHandler.BuildQuickAddChoices(Config("Business"), "bUsInEsS");

        choices[0].Section!.Name.Should().Be("Business");
        choices[0].Label.Should().StartWith("▸");
    }

    [Fact]
    public void FlatConfig_OnlyTheWholePageOption()
    {
        var choices = ScheduleCommandHandler.BuildQuickAddChoices(Config(), null);

        choices.Should().ContainSingle().Which.Section.Should().BeNull();
    }
}
