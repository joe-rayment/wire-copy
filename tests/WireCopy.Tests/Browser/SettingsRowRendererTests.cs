// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the shared <see cref="SettingsRowRenderer"/> extracted from
/// <c>PodcastConfirmationScreens.BuildConfirmationRow</c> (workspace-fn1u).
/// Both the Generate Podcast confirmation screen and the unified <c>:config</c>
/// Setup screen consume this renderer so row layout stays in sync.
/// </summary>
[Trait("Category", "Unit")]
public class SettingsRowRendererTests
{
    private static ThemePalette Palette() => BuiltInThemes.Get(ThemeName.Phosphor);

    private static int IndexOfPlain(string ansiLine, string needle)
    {
        var plain = SettingsRowRenderer.StripAnsi(ansiLine);
        return plain.IndexOf(needle, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SelectedAndUnselected_HaveIdenticalLabelColumn()
    {
        var p = Palette();

        var (unselected, _) = SettingsRowRenderer.Build(
            p, 100, isSelected: false, isWarning: false,
            statusIcon: "●", statusColor: p.PromptFg.AnsiFg,
            label: "Anthropic API key", value: "configured",
            valueColor: p.PromptFg.AnsiFg, actionLabel: "Change");

        var (selected, _) = SettingsRowRenderer.Build(
            p, 100, isSelected: true, isWarning: false,
            statusIcon: "●", statusColor: p.PromptFg.AnsiFg,
            label: "Anthropic API key", value: "configured",
            valueColor: p.PromptFg.AnsiFg, actionLabel: "Change");

        IndexOfPlain(unselected, "Anthropic API key")
            .Should().Be(IndexOfPlain(selected, "Anthropic API key"),
                "selecting a row must not horizontally shift its label");
    }

    [Fact]
    public void Build_Selected_PlacesIndicatorInFixedGutter()
    {
        var p = Palette();

        var (selected, _) = SettingsRowRenderer.Build(
            p, 100, true, false, "●", p.PromptFg.AnsiFg,
            "Label", "value", p.PromptFg.AnsiFg, "Change");

        var plain = SettingsRowRenderer.StripAnsi(selected);
        plain.IndexOf('▌').Should().Be(2,
            "selection indicator belongs in the fixed-width gutter at column 2");
    }

    [Fact]
    public void Build_AlwaysShowsActionButton_EvenWhenUnselected()
    {
        var p = Palette();

        var (unselected, _) = SettingsRowRenderer.Build(
            p, 100, false, false, "●", p.PromptFg.AnsiFg,
            "Label", "value", p.PromptFg.AnsiFg, "Change");

        var plain = SettingsRowRenderer.StripAnsi(unselected);
        plain.Should().Contain("[Enter]");
        plain.Should().Contain("Change");
    }

    [Fact]
    public void Build_HelperText_RendersAsSubLineAtContentColumn()
    {
        var p = Palette();

        var (_, sub) = SettingsRowRenderer.Build(
            p, 100, false, false, "○", p.SecondaryText.AnsiFg,
            "GCS bucket", "not set", p.SecondaryText.AnsiFg, "Set up",
            helperText: "Optional — enables RSS feed");

        sub.Should().NotBeNull();
        SettingsRowRenderer.StripAnsi(sub!).TrimStart().Should().StartWith("Optional");
        SettingsRowRenderer.StripAnsi(sub!).Should().Contain("Optional");
    }

    [Fact]
    public void Build_Warning_EmitsExplanationSubLine()
    {
        var p = Palette();

        var (_, sub) = SettingsRowRenderer.Build(
            p, 100, isSelected: false, isWarning: true,
            statusIcon: "○", statusColor: p.GetWarningFg().AnsiFg,
            label: "GCS bucket", value: "invalid-bucket",
            valueColor: p.GetWarningFg().AnsiFg, actionLabel: "Change",
            warningText: "Bucket does not exist");

        sub.Should().NotBeNull();
        SettingsRowRenderer.StripAnsi(sub!).Should().Contain("Bucket does not exist");
    }

    [Fact]
    public void StripAnsi_RemovesCsiEscapes()
    {
        const string s = "\x1b[31mred\x1b[0m and \x1b[1mbold\x1b[0m";
        SettingsRowRenderer.StripAnsi(s).Should().Be("red and bold");
    }
}
