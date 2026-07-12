// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Outcome of <see cref="IBrowserSession.OpenInPaneAsync"/> — opening a URL inside the
/// single-window shell's live pane. The three-way split matters: once a shell owns the
/// session, "open in browser" must NEVER bounce to an external OS browser, so a failure
/// is reported distinctly from "no shell here" (where the OS browser is the right tool).
/// </summary>
public enum PaneOpenResult
{
    /// <summary>Not attached to a desktop shell — terminal mode; the caller may use the OS browser.</summary>
    NotAttached,

    /// <summary>The pane revealed, the lens navigated to the URL, and the page owns the keyboard.</summary>
    Opened,

    /// <summary>Shell session, but the reveal/navigation failed — the caller reports it; no OS-browser fallback.</summary>
    Failed,
}
