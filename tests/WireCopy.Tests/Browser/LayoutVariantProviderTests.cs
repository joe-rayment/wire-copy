// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using NSubstitute;
using WireCopy.Application.Interfaces;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for LayoutVariantProvider covering variant cycling, persistence, and edge cases.
/// </summary>
[Trait("Category", "Unit")]
public class LayoutVariantProviderTests
{
    private readonly IUserSettingsStore _settingsStore;

    public LayoutVariantProviderTests()
    {
        _settingsStore = Substitute.For<IUserSettingsStore>();
    }

    [Fact]
    public void GetCurrentVariant_DefaultsToFirstVariant_ForEachMode()
    {
        var sut = new LayoutVariantProvider(_settingsStore);

        sut.GetCurrentVariant(ViewMode.Launcher).Should().Be("Grid");
        sut.GetCurrentVariant(ViewMode.Hierarchical).Should().Be("Cards");
        sut.GetCurrentVariant(ViewMode.Readable).Should().Be("Comfortable");
        sut.GetCurrentVariant(ViewMode.CollectionItems).Should().Be("Standard");
        sut.GetCurrentVariant(ViewMode.CollectionList).Should().Be("Standard");
    }

    [Fact]
    public void GetCurrentIndex_DefaultsToZero()
    {
        var sut = new LayoutVariantProvider(_settingsStore);

        sut.GetCurrentIndex(ViewMode.Launcher).Should().Be(0);
        sut.GetCurrentIndex(ViewMode.Hierarchical).Should().Be(0);
    }

    [Fact]
    public void GetTotalVariants_ReturnsCorrectCounts()
    {
        var sut = new LayoutVariantProvider(_settingsStore);

        sut.GetTotalVariants(ViewMode.Launcher).Should().Be(3);
        sut.GetTotalVariants(ViewMode.Hierarchical).Should().Be(3);
        sut.GetTotalVariants(ViewMode.Readable).Should().Be(3);
        sut.GetTotalVariants(ViewMode.CollectionItems).Should().Be(2);
        sut.GetTotalVariants(ViewMode.CollectionList).Should().Be(1);
    }

    [Fact]
    public void GetAvailableVariants_ReturnsExpectedNames()
    {
        var sut = new LayoutVariantProvider(_settingsStore);

        sut.GetAvailableVariants(ViewMode.Launcher).Should().BeEquivalentTo(
            new[] { "Grid", "List", "Compact" }, opts => opts.WithStrictOrdering());

        sut.GetAvailableVariants(ViewMode.Hierarchical).Should().BeEquivalentTo(
            new[] { "Cards", "DenseList", "Magazine" }, opts => opts.WithStrictOrdering());

        sut.GetAvailableVariants(ViewMode.Readable).Should().BeEquivalentTo(
            new[] { "Comfortable", "FullWidth", "Narrow" }, opts => opts.WithStrictOrdering());

        sut.GetAvailableVariants(ViewMode.CollectionItems).Should().BeEquivalentTo(
            new[] { "Standard", "Compact" }, opts => opts.WithStrictOrdering());

        sut.GetAvailableVariants(ViewMode.CollectionList).Should().BeEquivalentTo(
            new[] { "Standard" }, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void CycleVariant_AdvancesToNextVariant()
    {
        var sut = new LayoutVariantProvider(_settingsStore);

        var result = sut.CycleVariant(ViewMode.Launcher);

        result.Should().Be("List");
        sut.GetCurrentIndex(ViewMode.Launcher).Should().Be(1);
        sut.GetCurrentVariant(ViewMode.Launcher).Should().Be("List");
    }

    [Fact]
    public void CycleVariant_WrapsAroundToFirst()
    {
        var sut = new LayoutVariantProvider(_settingsStore);

        sut.CycleVariant(ViewMode.Launcher); // Grid -> List
        sut.CycleVariant(ViewMode.Launcher); // List -> Compact
        var result = sut.CycleVariant(ViewMode.Launcher); // Compact -> Grid

        result.Should().Be("Grid");
        sut.GetCurrentIndex(ViewMode.Launcher).Should().Be(0);
    }

    [Fact]
    public void CycleVariant_SingleVariantMode_ReturnsOnly()
    {
        var sut = new LayoutVariantProvider(_settingsStore);

        var result = sut.CycleVariant(ViewMode.CollectionList);

        result.Should().Be("Standard");
        sut.GetCurrentIndex(ViewMode.CollectionList).Should().Be(0);
    }

    [Fact]
    public void CycleVariant_PersistsToSettingsStore()
    {
        var sut = new LayoutVariantProvider(_settingsStore);

        sut.CycleVariant(ViewMode.Launcher);

        _settingsStore.Received(1).Set("Layout:Launcher", "List", Arg.Any<bool>());
    }

    [Fact]
    public void CycleVariant_DoesNotAffectOtherModes()
    {
        var sut = new LayoutVariantProvider(_settingsStore);

        sut.CycleVariant(ViewMode.Launcher);

        sut.GetCurrentVariant(ViewMode.Hierarchical).Should().Be("Cards");
        sut.GetCurrentIndex(ViewMode.Hierarchical).Should().Be(0);
    }

    [Fact]
    public void Constructor_RestoresPersistedPreference()
    {
        _settingsStore.Get("Layout:Launcher").Returns("Compact");

        var sut = new LayoutVariantProvider(_settingsStore);

        sut.GetCurrentVariant(ViewMode.Launcher).Should().Be("Compact");
        sut.GetCurrentIndex(ViewMode.Launcher).Should().Be(2);
    }

    [Fact]
    public void Constructor_IgnoresInvalidPersistedValue()
    {
        _settingsStore.Get("Layout:Launcher").Returns("NonExistent");

        var sut = new LayoutVariantProvider(_settingsStore);

        sut.GetCurrentVariant(ViewMode.Launcher).Should().Be("Grid");
        sut.GetCurrentIndex(ViewMode.Launcher).Should().Be(0);
    }

    [Fact]
    public void Constructor_HandlesNullPersistedValues()
    {
        _settingsStore.Get(Arg.Any<string>()).Returns((string?)null);

        var sut = new LayoutVariantProvider(_settingsStore);

        sut.GetCurrentVariant(ViewMode.Launcher).Should().Be("Grid");
    }

    [Fact]
    public void CycleVariant_SurvivesPersistenceFailure()
    {
        _settingsStore.When(x => x.Set(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>()))
            .Do(_ => throw new IOException("Disk full"));

        var sut = new LayoutVariantProvider(_settingsStore);

        // Should not throw — persistence is best-effort
        var result = sut.CycleVariant(ViewMode.Launcher);

        result.Should().Be("List");
        sut.GetCurrentVariant(ViewMode.Launcher).Should().Be("List");
    }

    [Fact]
    public void CycleVariant_TwoVariantMode_Toggles()
    {
        var sut = new LayoutVariantProvider(_settingsStore);

        sut.CycleVariant(ViewMode.CollectionItems).Should().Be("Compact");
        sut.CycleVariant(ViewMode.CollectionItems).Should().Be("Standard");
        sut.CycleVariant(ViewMode.CollectionItems).Should().Be("Compact");
    }

    [Fact]
    public void Constructor_RestoresMultipleModes()
    {
        _settingsStore.Get("Layout:Launcher").Returns("List");
        _settingsStore.Get("Layout:Readable").Returns("Narrow");

        var sut = new LayoutVariantProvider(_settingsStore);

        sut.GetCurrentVariant(ViewMode.Launcher).Should().Be("List");
        sut.GetCurrentVariant(ViewMode.Readable).Should().Be("Narrow");
        // Unset modes default to first
        sut.GetCurrentVariant(ViewMode.Hierarchical).Should().Be("Cards");
    }
}
