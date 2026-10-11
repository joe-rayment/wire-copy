// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using FluentAssertions;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Infrastructure.Browser.Themes;
using WireCopy.Infrastructure.Browser.UI;
using WireCopy.Infrastructure.Browser.UI.Components;
using WireCopy.Infrastructure.Browser.UI.Renderers;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-rgf1i — the wizard / layout-chooser cards must fit the terminal
/// they are drawn in. In the narrow docked terminal (~72 cols) an over-long row
/// used to overrun the right border, and xterm wrapped the tail into column 0 of
/// the next row (stray glyphs, missing right border). The overlays expose their
/// laid-out frame (cursor position + text per write) so the geometry is checked
/// without a real terminal.
/// </summary>
[Trait("Category", "Unit")]
public class OverlayCardWidthTests
{
    private static readonly ThemePalette Palette = BuiltInThemes.Get(ThemeName.Phosphor);
    private static readonly Regex Ansi = new("\x1b\\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    private const int TerminalHeight = 40;

    private static readonly string Long = new('x', 150);

    private static SetupWizardOverlay.State LongWizardCard() => new()
    {
        Mode = SetupWizardOverlay.Mode.Card,
        Card = new SetupWizardOverlay.WizardCard
        {
            Title = "Which of these is the main story on this front page of the site?",
            Prompt = "I found several candidates for the lead story, pick the one that matches what you see " + Long,
            Options =
            {
                new SetupWizardOverlay.CardOption { Label = "The first and most prominent headline", Identifier = "section.lead > article:nth-child(1) > h2 > a.headline-link--primary" },
                new SetupWizardOverlay.CardOption { Label = "Short", Identifier = "div.x" },
            },
            Cursor = 0,
            Footnote = "Highlighted in the browser pane: every link matching this selector, " + Long,
            Hint = "↑/↓ choose  Enter confirm  Esc cancel  Shift+I re-run the AI setup from scratch " + Long,
        },
    };

    private static SetupWizardOverlay.State LongAnalyzing() => new()
    {
        Mode = SetupWizardOverlay.Mode.Analyzing,
        AnalyzingLabel = "Asking the model to look at this very long page structure " + Long,
    };

    private static StrategyChooserOverlay.State LongChooser(StrategyChooserOverlay.Phase phase)
    {
        var state = new StrategyChooserOverlay.State
        {
            CurrentPhase = phase,
            Footnote = "A very long footnote explaining which strategies were skipped and why " + Long,
        };
        state.Rows.Add(new StrategyChooserOverlay.Row { Id = "a", DisplayName = "Document order", Detail = "348 links found across the whole page " + Long, State = StrategyChooserOverlay.RowState.Available });
        state.Rows.Add(new StrategyChooserOverlay.Row { Id = "b", DisplayName = "RSS", Detail = "No RSS/Atom feed", State = StrategyChooserOverlay.RowState.Unavailable });
        return state;
    }

    private static string Strip(string text) => Ansi.Replace(text, string.Empty);

    /// <summary>Every write must sit inside the terminal, form one rectangle, and close with a border glyph.</summary>
    private static void AssertFitsTerminal(IReadOnlyList<OverlayBox.Op> frame, int terminalWidth)
    {
        frame.Should().NotBeEmpty();
        var right = OverlayViewport.Left + terminalWidth;
        var firstWidth = RenderHelpers.GetDisplayWidth(Strip(frame[0].Text));

        foreach (var op in frame)
        {
            var visible = Strip(op.Text);
            var width = RenderHelpers.GetDisplayWidth(visible);

            (op.Col + width).Should().BeLessThanOrEqualTo(right, $"row at y={op.Row} ({visible}) must not run past the last column, or the terminal wraps it");
            "│╮╯".Should().Contain(visible[^1].ToString(), $"row at y={op.Row} must end in its right border, not clipped text");
            width.Should().Be(firstWidth, $"row at y={op.Row} must be as wide as the top border so the box stays rectangular");
            op.Col.Should().Be(frame[0].Col, "every row starts in the box's left column");
        }
    }

    // Guards:   a wizard card with a long title/prompt/option/footnote/hint still fits a narrow (docked) terminal.
    // Fails if: a card row is written wider than the terminal, so xterm wraps its tail into column 0 of the next row.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~OverlayCardWidthTests.SetupWizardCard"
    [Theory]
    [InlineData(72)]
    [InlineData(60)]
    [InlineData(44)]
    public void SetupWizardCard_LongContent_FitsNarrowTerminal(int terminalWidth)
    {
        var frame = SetupWizardOverlay.BuildFrame(LongWizardCard(), Palette, terminalWidth, TerminalHeight);

        AssertFitsTerminal(frame, terminalWidth);
    }

    // Guards:   the wizard's "analyzing" spinner box fits a narrow terminal even with a long status label.
    // Fails if: the spinner row is written wider than the terminal.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~OverlayCardWidthTests.SetupWizardAnalyzing"
    [Theory]
    [InlineData(72)]
    [InlineData(44)]
    public void SetupWizardAnalyzing_LongLabel_FitsNarrowTerminal(int terminalWidth)
    {
        var frame = SetupWizardOverlay.BuildFrame(LongAnalyzing(), Palette, terminalWidth, TerminalHeight);

        AssertFitsTerminal(frame, terminalWidth);
    }

    // Guards:   the layout chooser (all three phases) fits a narrow terminal with long details, footnote and hint.
    // Fails if: any chooser row, the footnote or the hint is written wider than the terminal.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~OverlayCardWidthTests.StrategyChooser"
    [Theory]
    [InlineData("Preflight", 72)]
    [InlineData("Probing", 60)]
    [InlineData("Preview", 72)]
    [InlineData("Preview", 60)]
    [InlineData("Preflight", 44)]
    public void StrategyChooser_LongContent_FitsNarrowTerminal(string phaseName, int terminalWidth)
    {
        var frame = StrategyChooserOverlay.BuildFrame(LongChooser(Enum.Parse<StrategyChooserOverlay.Phase>(phaseName)), Palette, terminalWidth, TerminalHeight);

        AssertFitsTerminal(frame, terminalWidth);
    }

    // Guards:   truncation only kicks in when needed: a wide terminal still shows the card text in full, untouched.
    // Fails if: the fix cuts or ellipsizes rows that already fit.
    // Run:      ./dotnet test tests/WireCopy.Tests/WireCopy.Tests.csproj --filter "FullyQualifiedName~OverlayCardWidthTests.WideTerminal"
    [Fact]
    public void WideTerminal_ShortContent_IsNotTruncated()
    {
        var state = new SetupWizardOverlay.State
        {
            Mode = SetupWizardOverlay.Mode.Card,
            Card = new SetupWizardOverlay.WizardCard
            {
                Title = "Your new layout",
                Prompt = "Pick one of these.",
                Options = { new SetupWizardOverlay.CardOption { Label = "Lead story", Identifier = "section.lead" } },
                Hint = "Enter confirm  Esc cancel",
            },
        };

        var frame = SetupWizardOverlay.BuildFrame(state, Palette, 200, TerminalHeight);

        AssertFitsTerminal(frame, 200);
        var text = string.Join("\n", frame.Select(o => Strip(o.Text)));
        text.Should().Contain("Pick one of these.").And.Contain("section.lead").And.Contain("Enter confirm  Esc cancel").And.NotContain("…");
    }
}
