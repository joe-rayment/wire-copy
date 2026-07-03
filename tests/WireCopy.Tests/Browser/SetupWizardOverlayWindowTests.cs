// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Application.DTOs.Browser;
using WireCopy.Domain.Enums.Browser;
using WireCopy.Domain.ValueObjects.Browser;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.CommandHandlers;
using WireCopy.Infrastructure.Browser.UI.Components;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// workspace-f25j.4 — a card taller than the terminal must not drop trailing
/// rows silently: the option list scrolls to keep the focused row visible and
/// the footnote carries an explicit "N more" overflow note. Plus the
/// workspace-f25j.1 honest failure prompt.
/// </summary>
[Trait("Category", "Unit")]
public class SetupWizardOverlayWindowTests
{
    private static SetupWizardOverlay.WizardCard Card(int optionCount, int cursor, string? footnote = "note")
    {
        return new SetupWizardOverlay.WizardCard
        {
            Title = "Your new layout",
            Prompt = "Pick one.",
            Options = Enumerable.Range(0, optionCount)
                .Select(i => new SetupWizardOverlay.CardOption { Label = $"Option {i}" })
                .ToList(),
            Cursor = cursor,
            Footnote = footnote,
            Hint = "hint",
        };
    }

    [Fact]
    public void ComputeOptionWindow_EverythingFits_ShowsAll()
    {
        SetupWizardOverlay.ComputeOptionWindow(optionCount: 4, cursor: 2, maxVisible: 10)
            .Should().Be((0, 4));
    }

    [Fact]
    public void ComputeOptionWindow_ScrollsToKeepCursorVisible()
    {
        // 10 options, 3 visible: cursor 0 → window [0..2], cursor 7 → window ends at 7.
        SetupWizardOverlay.ComputeOptionWindow(10, cursor: 0, maxVisible: 3).Should().Be((0, 3));
        SetupWizardOverlay.ComputeOptionWindow(10, cursor: 7, maxVisible: 3).Should().Be((5, 3));
        SetupWizardOverlay.ComputeOptionWindow(10, cursor: 9, maxVisible: 3).Should().Be((7, 3));
    }

    [Fact]
    public void ComputeCardWindow_NoOverflow_LeavesFootnoteAlone()
    {
        var window = SetupWizardOverlay.ComputeCardWindow(Card(3, cursor: 1), maxContentLines: 20);

        window.OptionOffset.Should().Be(0);
        window.OptionCount.Should().Be(3);
        window.Footnote.Should().Be("note");
    }

    [Fact]
    public void ComputeCardWindow_Overflow_KeepsFocusedOptionVisible_AndSaysHowManyMore()
    {
        // prompt(2) + footnote(2) + hint(1) = 5 fixed rows; 10 content rows
        // leave 5 option rows for 12 options.
        var window = SetupWizardOverlay.ComputeCardWindow(Card(12, cursor: 0), maxContentLines: 10);

        window.OptionOffset.Should().Be(0);
        window.OptionCount.Should().Be(5);
        window.Footnote.Should().Be("note · ↓ 7 more below", "hidden rows are announced, not silently clipped");
    }

    [Fact]
    public void ComputeCardWindow_Overflow_CursorDeepInList_ShowsAboveAndBelowCounts()
    {
        var window = SetupWizardOverlay.ComputeCardWindow(Card(12, cursor: 7), maxContentLines: 10);

        var visible = Enumerable.Range(window.OptionOffset, window.OptionCount);
        visible.Should().Contain(7, "the focused option scrolls into view");
        window.Footnote.Should().Contain("more above").And.Contain("more below");
    }

    [Fact]
    public void ComputeCardWindow_Overflow_NoFootnote_CreatesOneForTheNote()
    {
        var window = SetupWizardOverlay.ComputeCardWindow(Card(12, cursor: 11, footnote: null), maxContentLines: 10);

        window.Footnote.Should().Be("↑ 7 more above");
        Enumerable.Range(window.OptionOffset, window.OptionCount).Should().Contain(11);
    }

    [Fact]
    public void DescribeCard_Windowed_RendersFocusedRow_AndOverflowNote()
    {
        var lines = SetupWizardOverlay.DescribeCard(Card(12, cursor: 11), maxContentLines: 10);

        lines.Should().Contain("Option 11", "the focused option is on screen");
        lines.Should().NotContain("Option 0", "rows scrolled out are not rendered");
        lines.Should().Contain(l => l.Contains("more above", StringComparison.Ordinal));
    }

    [Fact]
    public void DescribeCard_NoLimit_IsUnchanged()
    {
        var lines = SetupWizardOverlay.DescribeCard(Card(12, cursor: 11));

        lines.Should().Contain("Option 0");
        lines.Should().Contain("Option 11");
        lines.Should().NotContain(l => l.Contains("more above", StringComparison.Ordinal));
    }

    // ---- workspace-f25j.1: failure prompt no longer contradicts the footnote ----

    [Fact]
    public void FailureCard_PromptClaimsUnreliableCoverage_NotZeroMatches()
    {
        var config = new SiteHierarchyConfig
        {
            Domain = "x.com",
            UrlPattern = "^x$",
            Sections = new List<HierarchySection>
            {
                new() { Name = "Ghost", SortOrder = 0, ParentSelectors = new List<string> { "section.lead" } },
            },
            CreatedAt = DateTime.UtcNow,
            ModelVersion = "gpt-5-mini",
            Kind = LayoutKind.AiCurated,
            Version = 3,
        };
        var links = new List<LinkInfo>
        {
            new() { Url = "https://x.com/a", DisplayText = "A", Type = LinkType.Content, ImportanceScore = 80, ParentSelector = "section.lead a" },
            new() { Url = "https://x.com/b", DisplayText = "B", Type = LinkType.Content, ImportanceScore = 60, ParentSelector = "div.x a" },
        };

        var shape = SetupWizard.BuildFailureCardShape(config, links, new ModelRoundTripBudget());

        shape.Prompt.Should().Be("I couldn't find a layout that reliably covers this page's stories.");
        shape.Prompt.Should().NotContain("that matches",
            "the footnote may show a nonzero match count — the prompt must not claim zero matches");
        shape.Footnote.Should().Contain("1 of 2", "the footnote keeps the honest exact count");
    }
}
