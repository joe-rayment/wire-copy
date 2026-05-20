// Licensed under the MIT License. See LICENSE in the repository root.

using FluentAssertions;
using WireCopy.Infrastructure.Browser;
using WireCopy.Infrastructure.Browser.UI;
using Xunit;

namespace WireCopy.Tests.Browser;

/// <summary>
/// Regression tests for workspace-umi7: speed-read advancing must never leave
/// the cursor invisible. The bead'\''s acceptance test (#4 in the plan): simulate
/// the cursor walking through an article at multiple terminal heights and
/// assert at every step that the cursor falls within the rendered viewport.
///
/// <para>
/// The renderer paints lines <c>[scroll, scroll + vpHeight)</c>. The invariant:
/// <c>scroll &lt;= cursor &lt; scroll + vpHeight</c>. If this holds for every
/// cursor position the symptom (underline disappears for several lines past
/// the bottom of the screen) cannot occur.
/// </para>
///
/// <para>
/// Driven against <see cref="BrowserOrchestrator.ScrollForCursor"/> (the pure
/// scroll-decision function extracted from <c>AdvanceSpeedReadCursor</c>) so
/// the test doesn'\''t need to stand up the full orchestrator + line cache +
/// navigation service.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class SpeedReadCursorVisibilityTests
{
    /// <summary>
    /// Cross-product of terminal heights (matches what the bead requires) and
    /// article lengths (short enough to fit, long enough to require many
    /// page-jumps). For every cursor position the invariant must hold.
    /// </summary>
    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(35)]
    [InlineData(50)]
    public void CursorStaysVisible_AcrossAllLines_AtTerminalHeight(int terminalHeight)
    {
        var vpHeight = ReaderLayout.ComputeContentHeight(terminalHeight);
        const int totalLines = 200;

        var scroll = 0;
        for (var cursor = 0; cursor < totalLines; cursor++)
        {
            scroll = BrowserOrchestrator.ScrollForCursor(cursor, scroll, vpHeight, totalLines);

            // The renderer paints lines [scroll, scroll + vpHeight). Cursor MUST
            // fall inside that range or the underline is invisible.
            cursor.Should().BeGreaterOrEqualTo(scroll,
                $"at cursor={cursor}, scroll={scroll}, vpHeight={vpHeight}: cursor went above viewport");
            cursor.Should().BeLessThan(scroll + vpHeight,
                $"at cursor={cursor}, scroll={scroll}, vpHeight={vpHeight}: cursor fell past bottom of viewport");

            // scroll must stay clamped.
            scroll.Should().BeGreaterOrEqualTo(0);
            scroll.Should().BeLessOrEqualTo(Math.Max(0, totalLines - vpHeight));
        }
    }

    [Fact]
    public void ScrollForCursor_CursorAtTopOfViewport_NoChange()
    {
        BrowserOrchestrator.ScrollForCursor(cursor: 5, scroll: 5, vpHeight: 30, totalLines: 200)
            .Should().Be(5);
    }

    [Fact]
    public void ScrollForCursor_CursorAtBottomLineOfViewport_NoChange()
    {
        // vpHeight=30, scroll=0 → visible lines [0, 30); cursor=29 is the last
        // visible line so no scroll yet.
        BrowserOrchestrator.ScrollForCursor(cursor: 29, scroll: 0, vpHeight: 30, totalLines: 200)
            .Should().Be(0);
    }

    [Fact]
    public void ScrollForCursor_CursorOnePastBottom_JumpsScrollToCursor()
    {
        // vpHeight=30, scroll=0 → visible [0, 30); cursor=30 just crossed below.
        // Expected scroll moves to 30 so the new viewport [30, 60) contains it.
        BrowserOrchestrator.ScrollForCursor(cursor: 30, scroll: 0, vpHeight: 30, totalLines: 200)
            .Should().Be(30);
    }

    [Fact]
    public void ScrollForCursor_CursorAboveViewport_SnapsToCursor()
    {
        // User jumped backwards. Bring the cursor to the top of the viewport.
        BrowserOrchestrator.ScrollForCursor(cursor: 12, scroll: 50, vpHeight: 30, totalLines: 200)
            .Should().Be(12);
    }

    [Fact]
    public void ScrollForCursor_ClampedAtMaxScroll_NearEndOfArticle()
    {
        // totalLines=100, vpHeight=30 → max scroll = 70. Cursor at 95 should
        // clamp scroll to 70, not 95.
        var scroll = BrowserOrchestrator.ScrollForCursor(cursor: 95, scroll: 0, vpHeight: 30, totalLines: 100);
        scroll.Should().Be(70);

        // Invariant still holds: cursor visible at lines [70, 100).
        (95 >= scroll).Should().BeTrue();
        (95 < scroll + 30).Should().BeTrue();
    }

    [Fact]
    public void ScrollForCursor_ClampedAtZero_WhenCursorIsBelowStart()
    {
        BrowserOrchestrator.ScrollForCursor(cursor: -3, scroll: 10, vpHeight: 30, totalLines: 200)
            .Should().Be(0);
    }

    [Fact]
    public void ScrollForCursor_ArticleShorterThanViewport_AlwaysScrollZero()
    {
        for (var cursor = 0; cursor < 15; cursor++)
        {
            BrowserOrchestrator.ScrollForCursor(cursor, scroll: 0, vpHeight: 30, totalLines: 15)
                .Should().Be(0);
        }
    }

    /// <summary>
    /// Load-bearing regression: pre-umi7 the scroller used <c>terminalHeight - 3</c>
    /// while the renderer reserved <c>terminalHeight - 5</c> lines (3-line header
    /// + 2-line status bar). This test reproduces that exact mismatch and shows
    /// the cursor goes invisible for several lines — proving that the
    /// "single source of truth" fix is necessary, not cosmetic.
    /// </summary>
    [Fact]
    public void PreFix_Mismatch_DemonstratesCursorInvisibility()
    {
        const int terminalHeight = 35;
        const int totalLines = 200;

        // The PRE-FIX values:
        const int scrollerVpHeight = terminalHeight - 3;          // 32
        const int rendererVpHeight = terminalHeight - 3 - 2;      // 30
        scrollerVpHeight.Should().Be(32);
        rendererVpHeight.Should().Be(30);

        var scroll = 0;
        var invisibleCount = 0;
        for (var cursor = 0; cursor < totalLines; cursor++)
        {
            // Scroller uses the LARGER (incorrect) vpHeight to decide when to scroll.
            scroll = BrowserOrchestrator.ScrollForCursor(cursor, scroll, scrollerVpHeight, totalLines);

            // But the renderer only paints [scroll, scroll + rendererVpHeight).
            // If cursor >= scroll + rendererVpHeight, the underline is invisible.
            if (cursor >= scroll + rendererVpHeight)
            {
                invisibleCount++;
            }
        }

        invisibleCount.Should().BeGreaterThan(0,
            "pre-fix the scroller/renderer mismatch produced invisible cursor lines — " +
            "this assertion documents that the bug was real. The fix (single ReaderLayout " +
            "source of truth) removes the mismatch by having both call sites use the " +
            "same value.");

        // Now run the same article through the POST-FIX path (same value
        // for both). The invariant must hold for EVERY cursor position.
        scroll = 0;
        var postFixVpHeight = ReaderLayout.ComputeContentHeight(terminalHeight);
        for (var cursor = 0; cursor < totalLines; cursor++)
        {
            scroll = BrowserOrchestrator.ScrollForCursor(cursor, scroll, postFixVpHeight, totalLines);
            cursor.Should().BeGreaterOrEqualTo(scroll);
            cursor.Should().BeLessThan(scroll + postFixVpHeight);
        }
    }
}
