// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

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
    public void GetBindings_CollectionList_DocumentsSetDefaultOverloadOfS()
    {
        // workspace-yejq.3: `s` in the Collections list sets the default
        // collection — the popup must teach the overload where it applies.
        var bindings = KeybindingPopup.GetBindings(ViewMode.CollectionList);

        bindings.Should().Contain(b => b.Key == "s" && b.Description.Contains("default"),
            "the Collections list popup must document `s` = set as default collection");
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

    [Theory]
    [InlineData(ViewMode.Hierarchical)]
    [InlineData(ViewMode.Readable)]
    [InlineData(ViewMode.CollectionList)]
    [InlineData(ViewMode.CollectionItems)]
    [InlineData(ViewMode.Launcher)]
    public void ComputeInnerWidth_FitsEveryRowOnNormalTerminal(ViewMode mode)
    {
        var bindings = KeybindingPopup.GetBindings(mode);
        var maxKeyWidth = bindings.Max(b => RenderHelpers.GetDisplayWidth(b.Key));
        var innerWidth = KeybindingPopup.ComputeInnerWidth(
            bindings, StatusBarRenderer.GetModeLabel(mode), maxKeyWidth, terminalWidth: 120);

        var rows = KeybindingPopup.BuildRows(bindings, maxKeyWidth, innerWidth);
        for (var i = 0; i < rows.Length; i++)
        {
            var (keyPadded, desc, padding) = rows[i];

            // No truncation should be needed: the box must grow to fit content.
            desc.Should().Be(bindings[i].Description,
                $"row '{bindings[i].Key}' must not be truncated when the terminal is wide enough");

            var visible = RenderHelpers.GetDisplayWidth(keyPadded)
                          + RenderHelpers.GetDisplayWidth(desc) + padding;
            visible.Should().Be(innerWidth,
                $"row '{bindings[i].Key}' in {mode} must align exactly with the border");
        }
    }

    [Theory]
    [InlineData(ViewMode.Hierarchical)]
    [InlineData(ViewMode.Readable)]
    [InlineData(ViewMode.CollectionList)]
    [InlineData(ViewMode.CollectionItems)]
    [InlineData(ViewMode.Launcher)]
    public void BuildRows_TruncatesInsteadOfOverflowingOnNarrowTerminal(ViewMode mode)
    {
        const int narrowTerminal = 40;
        var bindings = KeybindingPopup.GetBindings(mode);
        var maxKeyWidth = bindings.Max(b => RenderHelpers.GetDisplayWidth(b.Key));
        var innerWidth = KeybindingPopup.ComputeInnerWidth(
            bindings, StatusBarRenderer.GetModeLabel(mode), maxKeyWidth, narrowTerminal);

        innerWidth.Should().BeLessThanOrEqualTo(narrowTerminal - 4);

        foreach (var (keyPadded, desc, padding) in KeybindingPopup.BuildRows(bindings, maxKeyWidth, innerWidth))
        {
            var visible = RenderHelpers.GetDisplayWidth(keyPadded)
                          + RenderHelpers.GetDisplayWidth(desc) + padding;
            visible.Should().Be(innerWidth, "no row may exceed the box border at narrow widths");
        }
    }

    [Fact]
    public void ComputeInnerWidth_GrowsForLongDescriptions()
    {
        // The longest Hierarchical row today: "O — dock browser right (follows selection)".
        var bindings = KeybindingPopup.GetBindings(ViewMode.Hierarchical);
        var maxKeyWidth = bindings.Max(b => RenderHelpers.GetDisplayWidth(b.Key));
        var longest = bindings.Max(b => maxKeyWidth + 1 + RenderHelpers.GetDisplayWidth(b.Description));

        var innerWidth = KeybindingPopup.ComputeInnerWidth(
            bindings, StatusBarRenderer.GetModeLabel(ViewMode.Hierarchical), maxKeyWidth, terminalWidth: 200);

        innerWidth.Should().BeGreaterThanOrEqualTo(longest);
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
