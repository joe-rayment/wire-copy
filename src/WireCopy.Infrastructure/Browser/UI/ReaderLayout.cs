// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI;

/// <summary>
/// Single source of truth for reader-view viewport dimensions (workspace-umi7).
///
/// <para>
/// Previously the codebase had two independent functions computing the reader
/// content viewport height:
/// </para>
///
/// <list type="bullet">
///   <item><c>BrowserOrchestrator.GetReaderViewportHeight</c> assumed
///   <c>terminalHeight - 3</c> (1 for header + 2 for status bar).</item>
///   <item><c>TerminalPageRenderer.RenderReadable</c> measured
///   <c>terminalHeight - _helpers.LinesWritten - 2</c> AFTER the header card
///   actually wrote its 3-line box (top rule + subtitle + bottom rule).</item>
/// </list>
///
/// <para>
/// The mismatch (3 vs 5 reserved lines) meant the speed-read scroller thought
/// the viewport was 2 lines taller than what the renderer actually painted —
/// the cursor underline could advance past the bottom of the rendered area
/// without triggering a scroll, becoming invisible for several lines until
/// the scroller's threshold caught up.
/// </para>
///
/// <para>
/// This helper consolidates the math. The renderer''s
/// <c>LinkTreeRenderer.RenderHeader</c> always writes exactly
/// <see cref="HeaderLines"/> lines (top rule, subtitle, bottom rule — no
/// wrapping); the status bar reserves <see cref="StatusBarReservedLines"/>
/// lines. Both consumers compute content height via
/// <see cref="ComputeContentHeight"/>.
/// </para>
///
/// <para>
/// An invariant test in the test suite asserts that <see cref="HeaderLines"/>
/// matches the actual line count written by <c>LinkTreeRenderer.RenderHeader</c>.
/// If a future change adds or removes a header line, that test fails
/// immediately — the speed-read regression cannot silently return.
/// </para>
/// </summary>
internal static class ReaderLayout
{
    /// <summary>
    /// Lines emitted by <c>LinkTreeRenderer.RenderHeader</c> for the reader-view
    /// title bar (top rounded rule + subtitle + bottom rounded rule).
    /// </summary>
    public const int HeaderLines = 3;

    /// <summary>
    /// Lines reserved at the bottom of the viewport for the status bar
    /// (1 separator rule + 1 status content line).
    /// </summary>
    public const int StatusBarReservedLines = 2;

    /// <summary>
    /// Floor on the returned viewport so the renderer never gets a zero or
    /// negative content area on degenerately small terminals.
    /// </summary>
    public const int MinimumContentHeight = 3;

    /// <summary>
    /// Computes the reader content viewport in lines for the given terminal
    /// height. Use this from both the renderer and any caller that needs to
    /// know "how many article lines does the user actually see at once" —
    /// notably the speed-read scroller (workspace-umi7).
    /// </summary>
    public static int ComputeContentHeight(int terminalHeight)
    {
        return System.Math.Max(MinimumContentHeight, terminalHeight - HeaderLines - StatusBarReservedLines);
    }
}
