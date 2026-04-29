// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using TermReader.Domain.Enums.Browser;
using TermReader.Infrastructure.Browser.CommandHandlers;
using TermReader.Infrastructure.Browser.Themes;
using Xunit;

namespace TermReader.Tests.Browser;

/// <summary>
/// Layout tests for the Generate Podcast confirmation screen rows. These pin the
/// fix for two UX bugs:
///
/// * workspace-7via — selecting a row used to shift its label/value text right
///   by one column. The selection indicator now lives in a fixed-width gutter so
///   text columns are stable regardless of selection state.
/// * workspace-z4nl — row-level errors used to be rendered as a disconnected red
///   line at the bottom of the screen. They now render directly under the row
///   they refer to, indented to the content column, and the row itself is
///   colored in the warning color.
/// </summary>
[Trait("Category", "Unit")]
public class PodcastConfirmationRowTests
{
    private static ThemePalette Palette() => BuiltInThemes.Get(ThemeName.Phosphor);

    /// <summary>
    /// Returns the visible column where the first non-whitespace character starts,
    /// after stripping ANSI escapes.
    /// </summary>
    private static int FirstNonSpaceCol(string ansiLine)
    {
        var plain = PodcastConfirmationScreens.StripAnsi(ansiLine);
        for (var i = 0; i < plain.Length; i++)
        {
            if (plain[i] != ' ')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns the visible column where the given substring first appears in the
    /// stripped line, or -1 if not present.
    /// </summary>
    private static int IndexOfPlain(string ansiLine, string needle)
    {
        var plain = PodcastConfirmationScreens.StripAnsi(ansiLine);
        return plain.IndexOf(needle, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BuildConfirmationRow_SelectedAndUnselected_HaveIdenticalLabelColumn()
    {
        // Regression: workspace-7via — selection used to push the label right by 1.
        var p = Palette();

        var (unselected, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p,
            width: 100,
            isSelected: false,
            isWarning: false,
            statusIcon: "●",
            statusColor: p.PromptFg.AnsiFg,
            label: "OpenAI TTS API key",
            value: "configured",
            valueColor: p.PromptFg.AnsiFg,
            actionLabel: "Change");

        var (selected, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p,
            width: 100,
            isSelected: true,
            isWarning: false,
            statusIcon: "●",
            statusColor: p.PromptFg.AnsiFg,
            label: "OpenAI TTS API key",
            value: "configured",
            valueColor: p.PromptFg.AnsiFg,
            actionLabel: "Change");

        var unselectedLabelCol = IndexOfPlain(unselected, "OpenAI TTS API key");
        var selectedLabelCol = IndexOfPlain(selected, "OpenAI TTS API key");

        unselectedLabelCol.Should().BeGreaterThan(0, "label should appear in unselected row");
        selectedLabelCol.Should().BeGreaterThan(0, "label should appear in selected row");
        selectedLabelCol.Should().Be(unselectedLabelCol,
            "selecting a row must not horizontally shift its label");
    }

    [Fact]
    public void BuildConfirmationRow_SelectedAndUnselected_HaveIdenticalValueColumn()
    {
        var p = Palette();

        var (unselected, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, 100, false, false, "●", p.PromptFg.AnsiFg, "GCS bucket", "my-bucket", p.PromptFg.AnsiFg, "Change");
        var (selected, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, 100, true, false, "●", p.PromptFg.AnsiFg, "GCS bucket", "my-bucket", p.PromptFg.AnsiFg, "Change");

        IndexOfPlain(unselected, "my-bucket").Should().Be(IndexOfPlain(selected, "my-bucket"),
            "value column must be stable across selection state");
    }

    [Fact]
    public void BuildConfirmationRow_SelectedAndUnselected_HaveIdenticalIconColumn()
    {
        var p = Palette();

        var (unselected, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, 100, false, false, "○", p.SecondaryText.AnsiFg, "GCS bucket", "not set", p.SecondaryText.AnsiFg, "Set up");
        var (selected, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, 100, true, false, "○", p.SecondaryText.AnsiFg, "GCS bucket", "not set", p.SecondaryText.AnsiFg, "Set up");

        // The status icon ○ should be at the same column in both branches.
        IndexOfPlain(unselected, "○").Should().Be(IndexOfPlain(selected, "○"),
            "status-icon column must be stable across selection state");
    }

    [Fact]
    public void BuildConfirmationRow_Selected_PlacesIndicatorInFixedGutter()
    {
        var p = Palette();

        var (selected, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, 100, true, false, "●", p.PromptFg.AnsiFg, "Label", "value", p.PromptFg.AnsiFg, "Change");

        var plain = PodcastConfirmationScreens.StripAnsi(selected);
        // Indicator (▌) should sit at column 2 (after the 2-space outer margin).
        plain.IndexOf('▌').Should().Be(2,
            "selection indicator belongs in the fixed-width gutter at column 2");
    }

    [Fact]
    public void BuildConfirmationRow_Unselected_HasNoIndicatorChar()
    {
        var p = Palette();

        var (unselected, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, 100, false, false, "●", p.PromptFg.AnsiFg, "Label", "value", p.PromptFg.AnsiFg, "Change");

        var plain = PodcastConfirmationScreens.StripAnsi(unselected);
        plain.Should().NotContain("▌", "unselected rows must not show the selection indicator");
    }

    [Fact]
    public void BuildConfirmationRow_AlwaysShowsActionButton_EvenWhenUnselected()
    {
        // Acceptance for workspace-7via: action button must be visible on every
        // row (so the screen reads as a menu on entry, even before keystrokes).
        var p = Palette();

        var (unselected, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, 100, false, false, "●", p.PromptFg.AnsiFg, "Label", "value", p.PromptFg.AnsiFg, "Change");

        var plain = PodcastConfirmationScreens.StripAnsi(unselected);
        plain.Should().Contain("[Enter]", "every row must show its [Enter] action label");
        plain.Should().Contain("Change", "the action verb must be visible");
    }

    [Fact]
    public void BuildConfirmationRow_Warning_EmitsExplanationSubLine()
    {
        // Regression: workspace-z4nl — error explanation must be a sub-line under
        // the row it refers to, not a disconnected bottom-of-screen message.
        var p = Palette();

        var (_, sub) = PodcastConfirmationScreens.BuildConfirmationRow(
            p,
            width: 100,
            isSelected: false,
            isWarning: true,
            statusIcon: "○",
            statusColor: p.GetWarningFg().AnsiFg,
            label: "GCS bucket",
            value: "invalid-bucket",
            valueColor: p.GetWarningFg().AnsiFg,
            actionLabel: "Change",
            warningText: "Bucket does not exist");

        sub.Should().NotBeNull("warning text must produce a sub-line");
        var subPlain = PodcastConfirmationScreens.StripAnsi(sub!);
        subPlain.Should().Contain("Bucket does not exist", "warning text must be visible on the sub-line");
        FirstNonSpaceCol(sub!).Should().Be(6,
            "warning text must indent to the row content column (col 6) so it visually attaches to the row above");
    }

    [Fact]
    public void BuildConfirmationRow_Warning_RowItselfRendersInWarningColor()
    {
        // workspace-z4nl: the row that owns the error must visually carry the
        // warning color (label + indicator), not just a separate red message.
        var p = Palette();
        var warnFg = p.GetWarningFg().AnsiFg;

        var (mainLine, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, 100, isSelected: false, isWarning: true,
            statusIcon: "○",
            statusColor: p.GetWarningFg().AnsiFg,
            label: "GCS bucket",
            value: "invalid-bucket",
            valueColor: p.GetWarningFg().AnsiFg,
            actionLabel: "Change",
            warningText: "Bucket does not exist");

        // The label "GCS bucket" must appear immediately after the warning ANSI
        // foreground escape — i.e. the label is colored as a warning, not as
        // ordinary primary text.
        mainLine.Should().Contain($"{warnFg}GCS bucket",
            "the row label must be colored in the warning foreground when isWarning=true");
    }

    [Fact]
    public void BuildConfirmationRow_NotWarning_NoSubLineWhenNoText()
    {
        var p = Palette();

        var (_, sub) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, 100, false, false, "●", p.PromptFg.AnsiFg, "Label", "value", p.PromptFg.AnsiFg, "Change");

        sub.Should().BeNull("no sub-line when no warning or helper text was supplied");
    }

    [Fact]
    public void BuildConfirmationRow_HelperText_RendersAsSubLineAtContentColumn()
    {
        var p = Palette();

        var (_, sub) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, 100, false, false, "○", p.SecondaryText.AnsiFg, "GCS bucket", "not set", p.SecondaryText.AnsiFg, "Set up",
            helperText: "Optional — enables RSS feed");

        sub.Should().NotBeNull();
        FirstNonSpaceCol(sub!).Should().Be(6,
            "helper text must indent to the row content column (col 6)");
        PodcastConfirmationScreens.StripAnsi(sub!).Should().Contain("Optional");
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    [InlineData(120)]
    public void BuildConfirmationRow_VariousWidths_LabelStaysAtColumn6(int width)
    {
        var p = Palette();

        var (selected, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, width, true, false, "●", p.PromptFg.AnsiFg, "MyLabel", "v", p.PromptFg.AnsiFg, "Change");
        var (unselected, _) = PodcastConfirmationScreens.BuildConfirmationRow(
            p, width, false, false, "●", p.PromptFg.AnsiFg, "MyLabel", "v", p.PromptFg.AnsiFg, "Change");

        IndexOfPlain(selected, "MyLabel").Should().Be(6, "label always at content col 6 (selected)");
        IndexOfPlain(unselected, "MyLabel").Should().Be(6, "label always at content col 6 (unselected)");
    }

    [Fact]
    public void StripAnsi_RemovesCsiEscapes()
    {
        const string s = "\x1b[31mred\x1b[0m and \x1b[1mbold\x1b[0m";
        PodcastConfirmationScreens.StripAnsi(s).Should().Be("red and bold");
    }

    [Fact]
    public void StripAnsi_NullOrEmpty_ReturnsInput()
    {
        PodcastConfirmationScreens.StripAnsi(string.Empty).Should().Be(string.Empty);
        PodcastConfirmationScreens.StripAnsi("plain").Should().Be("plain");
    }
}
