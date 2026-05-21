// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Tests for the shared <see cref="SettingsRowRenderer"/> extracted from
/// <c>PodcastSetupHelpers.BuildConfirmationRow</c> (workspace-fn1u).
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

    [Fact]
    public void Build_LongValueAtWidth80_DoesNotOverflowTerminal()
    {
        // workspace-l6w0: the "Output folder" row with a real-world path
        // (40+ chars) plus the "[Enter] Change" tail used to total more than
        // 80 visible columns at width 80. The bleed showed up as `[Enter] Ch`
        // truncated by the next paint pass. Fix: middle-truncate the value
        // when needed.
        var p = Palette();

        var (main, _) = SettingsRowRenderer.Build(
            p,
            width: 80,
            isSelected: false,
            isWarning: false,
            statusIcon: "●",
            statusColor: p.PromptFg.AnsiFg,
            label: "Output folder",
            value: "/Users/joe/Library/Application Support/WireCopy/podcasts",
            valueColor: p.PromptFg.AnsiFg,
            actionLabel: "Change");

        SettingsRowRenderer.StripAnsi(main).Length.Should().BeLessThanOrEqualTo(80,
            because: "the rendered row at width 80 must NOT exceed 80 visible columns");
        SettingsRowRenderer.StripAnsi(main).Should().Contain("[Enter] Change",
            because: "the action label must remain intact — value gets truncated first");
        SettingsRowRenderer.StripAnsi(main).Should().Contain("…",
            because: "a value that doesn't fit gets middle-truncated with an ellipsis");
    }

    [Fact]
    public void Build_LongValueAtWidth80_HeadAndTailOfValueRemainVisible()
    {
        // For filesystem paths the user needs to see both the parent (so they
        // know where it lives) and the basename (so they recognise the file).
        // Middle-truncation must preserve both ends.
        var p = Palette();

        var (main, _) = SettingsRowRenderer.Build(
            p, 80, isSelected: false, isWarning: false,
            statusIcon: "●", statusColor: p.PromptFg.AnsiFg,
            label: "Output folder",
            value: "/Users/joe/Library/Application Support/WireCopy/podcasts/reading-list.m4b",
            valueColor: p.PromptFg.AnsiFg, actionLabel: "Change");

        var plain = SettingsRowRenderer.StripAnsi(main);
        plain.Should().Contain("/Users",
            because: "the head of the path must remain visible after middle truncation");
        plain.Should().Contain("reading-list.m4b",
            because: "the basename must remain visible after middle truncation");
    }

    [Fact]
    public void Build_ShortValueAtWidth80_IsNotTruncated()
    {
        // Counterpart: when the row already fits at width 80, the value must
        // render unchanged — no spurious ellipsis.
        var p = Palette();

        var (main, _) = SettingsRowRenderer.Build(
            p, 80, isSelected: false, isWarning: false,
            statusIcon: "●", statusColor: p.PromptFg.AnsiFg,
            label: "TTS voice", value: "alloy",
            valueColor: p.PromptFg.AnsiFg, actionLabel: "Change");

        var plain = SettingsRowRenderer.StripAnsi(main);
        plain.Should().NotContain("…",
            because: "a value that fits within the row budget must render unchanged");
        plain.Should().Contain("alloy");
        plain.Length.Should().BeLessThanOrEqualTo(80);
    }

    [Fact]
    public void Build_LongLabelAndLongValue_DropsActionToPrioritiseValue()
    {
        // When the action would push the row past the terminal width, the
        // renderer drops the action label so the value (which the user
        // actually cares about reading) still gets the available space.
        // Pins the action-drop branch; uses a realistic 24-col label so the
        // test scenario isn't pathological.
        var p = Palette();

        var (main, _) = SettingsRowRenderer.Build(
            p,
            width: 40,
            isSelected: false,
            isWarning: false,
            statusIcon: "●",
            statusColor: p.PromptFg.AnsiFg,
            label: "Output folder",
            value: "/Users/joe/Library/Application Support/WireCopy/podcasts/reading-list.m4b",
            valueColor: p.PromptFg.AnsiFg,
            actionLabel: "Change");

        var plain = SettingsRowRenderer.StripAnsi(main);
        plain.Should().NotContain("[Enter] Change",
            because: "at a tight width the action gets dropped so the value can keep more of its width");
        plain.Should().Contain("…",
            because: "the value gets middle-truncated to fit within the row");
    }

    [Fact]
    public void TruncateValueMiddle_FitsWithinMaxLength()
    {
        // Direct helper test covering the boundary cases used by the row
        // builder.
        SettingsRowRenderer.TruncateValueMiddle("short", 10).Should().Be("short");
        SettingsRowRenderer.TruncateValueMiddle("hello world", 10).Length.Should().BeLessThanOrEqualTo(10);
        SettingsRowRenderer.TruncateValueMiddle("hello world", 10).Should().Contain("…");
        SettingsRowRenderer.TruncateValueMiddle("anything", 0).Should().BeEmpty(
            "the row builder passes maxLen=0 when even an empty value would overflow");
        SettingsRowRenderer.TruncateValueMiddle("anything", 1).Should().Be("…");
    }
}
