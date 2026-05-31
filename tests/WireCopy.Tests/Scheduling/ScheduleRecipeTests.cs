// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

[Trait("Category", "Unit")]
public class ScheduleRecipeTests
{
    private static RecipeStep Step(string section = "Business", bool required = true, TakeMode mode = TakeMode.WholeSection, int? count = null) =>
        RecipeStep.Create(
            sourceUrl: "https://www.nytimes.com/",
            domain: "nytimes.com",
            configUrlPattern: "^https?://(www\\.)?nytimes\\.com/?$",
            sectionName: section,
            takeMode: mode,
            takeCount: count,
            required: required);

    private static Cadence Weekdays() => Cadence.Create(
        new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
        new TimeOnly(7, 0));

    [Fact]
    public void RecipeStep_StoresDurableIdentity_NoSelectorSnapshot_NoArticleUrls_NoHeadingKey()
    {
        var step = Step();

        step.ConfigUrlPattern.Should().NotBeNullOrEmpty();
        step.SectionName.Should().Be("Business");
        // The durable contract: a step is keyed on (UrlPattern, SectionName) +
        // SortOrderFallback — there is no place to stash a selector snapshot,
        // article URLs, or heading text as the primary key.
        var props = typeof(RecipeStep).GetProperties().Select(p => p.Name).ToList();
        props.Should().NotContain("ParentSelectors");
        props.Should().NotContain("Selectors");
        props.Should().NotContain("ArticleUrls");
        props.Should().Contain("SortOrderFallback");
        props.Should().Contain("HeadingAliases"); // fallback tier only
    }

    [Theory]
    [InlineData(TakeMode.SingleTopStory, null, 1)]   // normalised to 1
    [InlineData(TakeMode.WholeSection, 5, null)]     // normalised to null
    [InlineData(TakeMode.TopN, 3, 3)]
    public void RecipeStep_TakeMode_Invariants(TakeMode mode, int? given, int? expected)
    {
        RecipeStep.Create("https://x.com/", "x.com", "^x$", "S", takeMode: mode, takeCount: given)
            .TakeCount.Should().Be(expected);
    }

    [Fact]
    public void RecipeStep_TopNWithoutCount_Throws()
    {
        FluentActions.Invoking(() =>
                RecipeStep.Create("https://x.com/", "x.com", "^x$", "S", takeMode: TakeMode.TopN, takeCount: null))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RejectsEmptyName_AndZeroSteps()
    {
        FluentActions.Invoking(() => ScheduleRecipe.Create("  ", Weekdays(), new[] { Step() }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ScheduleRecipe.Create("NYT", Weekdays(), Array.Empty<RecipeStep>()))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_RequiresAtLeastOneRequiredStep()
    {
        FluentActions.Invoking(() => ScheduleRecipe.Create("NYT", Weekdays(), new[] { Step(required: false) }))
            .Should().Throw<ArgumentException>("the quality floor needs a content guarantee");

        var ok = ScheduleRecipe.Create("NYT", Weekdays(), new[] { Step("Top", required: true), Step("Business", required: false) });
        ok.HasRequiredStep.Should().BeTrue();
    }

    [Fact]
    public void StepOrder_IsStable_ThroughAddAndReorder()
    {
        var recipe = ScheduleRecipe.Create("Brief", Weekdays(), new[] { Step("Top"), Step("Business", required: false) });
        recipe.AddStep(Step("Tech", required: false));
        recipe.Steps.Select(s => s.SectionName).Should().Equal("Top", "Business", "Tech");

        recipe.MoveStepDown(0);
        recipe.Steps.Select(s => s.SectionName).Should().Equal("Business", "Top", "Tech");
        recipe.MoveStepUp(2);
        recipe.Steps.Select(s => s.SectionName).Should().Equal("Business", "Tech", "Top");

        recipe.RemoveStep(0);
        recipe.Steps.Select(s => s.SectionName).Should().Equal("Tech", "Top");
    }

    [Fact]
    public void RecordRun_UpdatesRunStateConvenienceCache()
    {
        var recipe = ScheduleRecipe.Create("Brief", Weekdays(), new[] { Step() });
        recipe.RunState.LastStatus.Should().Be(RunStatus.Never);

        recipe.RecordRun(new DateOnly(2026, 5, 31), "2026-05-31@07:00", RunStatus.Success);

        recipe.RunState.LastStatus.Should().Be(RunStatus.Success);
        recipe.RunState.LastRunOccurrenceKey.Should().Be("2026-05-31@07:00");
        recipe.RunState.AcknowledgedAtUtc.Should().BeNull();
    }

    [Fact]
    public void Cadence_RejectsEmptyDays_AndNegativeGrace()
    {
        FluentActions.Invoking(() => Cadence.Create(Array.Empty<DayOfWeek>(), new TimeOnly(7, 0)))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Cadence.Create(new[] { DayOfWeek.Monday }, new TimeOnly(7, 0), TimeSpan.FromHours(-1)))
            .Should().Throw<ArgumentException>();
        Cadence.Daily(new TimeOnly(6, 30)).Days.Should().HaveCount(7);
    }
}
