// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Domain.ValueObjects.Scheduling;
using WireCopy.Infrastructure.Scheduling;
using Xunit;

namespace WireCopy.Tests.Scheduling;

[Trait("Category", "Unit")]
public class ScheduleStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly ScheduleStore _store;

    public ScheduleStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wirecopy-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new ScheduleStore(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static ScheduleRecipe MultiSourceRecipe()
    {
        var cadence = Cadence.Create(
            new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday },
            new TimeOnly(7, 30),
            TimeSpan.FromHours(2));

        var steps = new[]
        {
            RecipeStep.Create("https://www.nytimes.com/", "nytimes.com", "^nyt$", "Top Stories", TakeMode.WholeSection, required: true),
            RecipeStep.Create("https://techmeme.com/", "techmeme.com", "^tm$", "Top Story", TakeMode.SingleTopStory, required: false),
            RecipeStep.Create("https://www.nytimes.com/", "nytimes.com", "^nyt$", "Business", TakeMode.TopN, takeCount: 3, required: false,
                headingAliases: new[] { "Business Daily", "Sunday Business" }),
        };

        return ScheduleRecipe.Create("Morning Brief", cadence, steps);
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsFaithfully()
    {
        var recipe = MultiSourceRecipe();
        await _store.SaveAsync(recipe);

        var loaded = await _store.GetAsync(recipe.Id);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Morning Brief");
        loaded.Cadence.Days.Should().BeEquivalentTo(new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday });
        loaded.Cadence.LocalTime.Should().Be(new TimeOnly(7, 30));
        loaded.Cadence.GraceWindow.Should().Be(TimeSpan.FromHours(2));
        loaded.Steps.Select(s => s.SectionName).Should().Equal("Top Stories", "Top Story", "Business");
        loaded.Steps[1].TakeMode.Should().Be(TakeMode.SingleTopStory);
        loaded.Steps[1].TakeCount.Should().Be(1);
        loaded.Steps[2].TakeMode.Should().Be(TakeMode.TopN);
        loaded.Steps[2].TakeCount.Should().Be(3);
        loaded.Steps[2].HeadingAliases.Should().Contain("Sunday Business");
        loaded.Steps[0].Required.Should().BeTrue();
    }

    [Fact]
    public async Task EnumsPersistAsNames_NotOrdinals()
    {
        await _store.SaveAsync(MultiSourceRecipe());
        var json = await File.ReadAllTextAsync(Path.Combine(_dir, "schedules.json"));

        json.Should().Contain("SingleTopStory").And.Contain("WholeSection").And.Contain("TopN");
        json.Should().Contain("Monday").And.Contain("Friday");
        // A reordering-robust representation: no bare enum ordinals for takeMode.
        json.Should().NotContain("\"takeMode\": 0");
    }

    [Fact]
    public async Task UpdateRunState_DoesNotClobberAConcurrentDefinitionEdit()
    {
        var recipe = MultiSourceRecipe();
        await _store.SaveAsync(recipe);

        // A definition edit (rename) lands first...
        recipe.Rename("Renamed Brief");
        await _store.SaveAsync(recipe);

        // ...then a run-state cache write for the same id.
        await _store.UpdateRunStateAsync(recipe.Id,
            RecipeRunState.Initial with { LastRunOccurrenceKey = "2026-06-01@07:30", LastStatus = RunStatus.Success });

        var loaded = await _store.GetAsync(recipe.Id);
        loaded!.Name.Should().Be("Renamed Brief", "the run-state write must not revert the rename");
        loaded.RunState.LastStatus.Should().Be(RunStatus.Success);
        loaded.RunState.LastRunOccurrenceKey.Should().Be("2026-06-01@07:30");
    }

    [Fact]
    public async Task Delete_RemovesRecipe()
    {
        var recipe = MultiSourceRecipe();
        await _store.SaveAsync(recipe);
        (await _store.DeleteAsync(recipe.Id)).Should().BeTrue();
        (await _store.GetAsync(recipe.Id)).Should().BeNull();
        (await _store.DeleteAsync(recipe.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task CorruptFile_LoadsAsEmpty()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "schedules.json"), "not valid json {{{");
        (await _store.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task WholePageStep_RoundTripsWithItsScope()
    {
        // workspace-42q8.2
        var step = ScheduleEditing.BuildWholePageStep(
            "https://text.npr.org/", "text.npr.org", "^npr$", TakeMode.TopN, takeCount: 4, required: true);
        var recipe = ScheduleRecipe.Create(
            "NPR All", Cadence.Create(new[] { DayOfWeek.Monday }, new TimeOnly(7, 0)), new[] { step });
        await _store.SaveAsync(recipe);

        var loaded = await _store.GetAsync(recipe.Id);

        var loadedStep = loaded!.Steps.Single();
        loadedStep.Scope.Should().Be(StepScope.WholePage);
        loadedStep.SectionName.Should().Be(RecipeStep.WholePageSectionName);
        loadedStep.TakeCount.Should().Be(4);

        // Persisted as the enum NAME (reorder-safe), like every enum in this file.
        var raw = await File.ReadAllTextAsync(Path.Combine(_dir, "schedules.json"));
        raw.Should().Contain("\"WholePage\"");
    }

    [Fact]
    public async Task LegacyStepJson_WithoutScope_LoadsAsPinnedSection()
    {
        // workspace-42q8.2: schedules.json written before the Scope field existed
        // must keep meaning exactly what it meant.
        var legacy =
            "{\"version\":1,\"recipes\":[{\"id\":\"7e0f7e57-1111-2222-3333-444444444444\",\"name\":\"Old Brief\"," +
            "\"enabled\":true,\"days\":[\"Monday\"],\"localTime\":\"07:00\"," +
            "\"steps\":[{\"sourceUrl\":\"https://nyt.example/\",\"domain\":\"nyt.example\"," +
            "\"configUrlPattern\":\"^nyt$\",\"sectionName\":\"Front\",\"sortOrderFallback\":0,\"headingAliases\":[]," +
            "\"takeMode\":\"WholeSection\",\"required\":true}]," +
            "\"outputCollectionName\":\"Old Brief\",\"version\":1,\"lastStatus\":\"Never\"}]}";
        await File.WriteAllTextAsync(Path.Combine(_dir, "schedules.json"), legacy);

        var loaded = await _store.GetAllAsync();

        var step = loaded.Single().Steps.Single();
        step.Scope.Should().Be(StepScope.PinnedSection);
        step.SectionName.Should().Be("Front");
    }
}
