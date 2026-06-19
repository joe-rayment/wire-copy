// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Pure mapping from the current TUI view state to what the web pane should show. Kept separate from
/// the orchestrator so the "never-empty / reveal-on-content / live-only" policy is unit-testable
/// without a browser. Reuses <see cref="DockSpotlight.ResolveTarget"/> as the single source of truth
/// for "is there anything worth showing" (it already returns null for launcher/collections/group
/// headers and non-summonable URLs).
/// </summary>
public static class WebPaneDecision
{
    /// <summary>
    /// Decides the pane mode for a view (workspace-8a5y — the web pane is ALWAYS the LIVE real site, or
    /// hidden; it never renders a reader-fied snapshot):
    /// <list type="bullet">
    /// <item>no displayable target (launcher, collections, data: pages, cache-only reads) → <see cref="WebPaneMode.Hidden"/>;</item>
    /// <item>anything worth showing (link lists, live articles, reader views with a live page) → <see cref="WebPaneMode.Live"/>.</item>
    /// </list>
    /// </summary>
    public static WebPaneMode Decide(ViewMode viewMode, Page? page)
    {
        if (page is null)
        {
            return WebPaneMode.Hidden;
        }

        if (DockSpotlight.ResolveTarget(viewMode, page, page.LinkTree) is null)
        {
            return WebPaneMode.Hidden;
        }

        // Reader view streams the LIVE site too — the spotlight drives the display page to follow the
        // article being read, so the pane is the real page, complementing (not duplicating) the TUI
        // reader. The retired Snapshot branch rendered a sanitized fake site here (workspace-8a5y).
        return WebPaneMode.Live;
    }
}
