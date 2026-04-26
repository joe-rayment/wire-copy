// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.UI.Components;
using Xunit;

namespace TermReader.Tests.Browser;

[Trait("Category", "Unit")]
public class KeybindingPopupTests
{
    [Theory]
    [InlineData(ViewMode.Hierarchical)]
    [InlineData(ViewMode.Readable)]
    [InlineData(ViewMode.CollectionList)]
    [InlineData(ViewMode.CollectionItems)]
    [InlineData(ViewMode.Launcher)]
    public void GetBindings_ReturnsNonEmptyForAllModes(ViewMode mode)
    {
        var bindings = KeybindingPopup.GetBindings(mode);
        bindings.Should().NotBeEmpty();
    }

    [Fact]
    public void GetBindings_Hierarchical_ContainsEssentialBindings()
    {
        var bindings = KeybindingPopup.GetBindings(ViewMode.Hierarchical);
        var keys = bindings.Select(b => b.Key).ToList();

        keys.Should().Contain("Enter");
        keys.Should().Contain("s");
        keys.Should().Contain("v");
        keys.Should().Contain("b");
        keys.Should().Contain("q");
    }

    [Fact]
    public void GetBindings_Readable_ContainsEssentialBindings()
    {
        var bindings = KeybindingPopup.GetBindings(ViewMode.Readable);
        var keys = bindings.Select(b => b.Key).ToList();

        keys.Should().Contain("s");
        keys.Should().Contain("v");
        keys.Should().Contain("b");
        keys.Should().Contain("q");
    }

    [Fact]
    public void GetBindings_Launcher_ContainsEssentialBindings()
    {
        var bindings = KeybindingPopup.GetBindings(ViewMode.Launcher);
        var keys = bindings.Select(b => b.Key).ToList();

        keys.Should().Contain("Enter");
        keys.Should().Contain("o");
        keys.Should().Contain("q");
    }

    [Theory]
    [InlineData(ViewMode.Hierarchical)]
    [InlineData(ViewMode.Readable)]
    [InlineData(ViewMode.CollectionList)]
    [InlineData(ViewMode.CollectionItems)]
    [InlineData(ViewMode.Launcher)]
    public void GetBindings_AllBindingsHaveDescriptions(ViewMode mode)
    {
        var bindings = KeybindingPopup.GetBindings(mode);

        foreach (var (key, desc) in bindings)
        {
            key.Should().NotBeNullOrWhiteSpace($"key should not be empty for mode {mode}");
            desc.Should().NotBeNullOrWhiteSpace($"description should not be empty for key '{key}' in mode {mode}");
        }
    }

    [Fact]
    public void GetBindings_QuitAvailableInAllModes()
    {
        var modes = new[] { ViewMode.Hierarchical, ViewMode.Readable, ViewMode.CollectionList, ViewMode.CollectionItems, ViewMode.Launcher };

        foreach (var mode in modes)
        {
            var bindings = KeybindingPopup.GetBindings(mode);
            bindings.Should().Contain(b => b.Key == "q", $"quit binding should be available in {mode}");
        }
    }
}
