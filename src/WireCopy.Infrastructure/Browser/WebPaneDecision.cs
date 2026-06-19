// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;
using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Infrastructure.Browser;

/// <summary>
/// Pure mapping from the current TUI view state to what the web pane should show. Kept separate from
/// the orchestrator so the "never-empty / reveal-on-content / snapshot-vs-live" policy is unit-testable
/// without a browser. Reuses <see cref="DockSpotlight.ResolveTarget"/> as the single source of truth
/// for "is there anything worth showing" (it already returns null for launcher/collections/group
/// headers and non-summonable URLs).
/// </summary>
public static class WebPaneDecision
{
    /// <summary>
    /// Decides the pane mode for a view:
    /// <list type="bullet">
    /// <item>no displayable target (launcher, collections, data: pages) → <see cref="WebPaneMode.Hidden"/>;</item>
    /// <item>reader view with extracted article content → <see cref="WebPaneMode.Snapshot"/>;</item>
    /// <item>everything else worth showing (link lists, live pages) → <see cref="WebPaneMode.Live"/>.</item>
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

        if (viewMode == ViewMode.Readable && page.HasReadableContent())
        {
            return WebPaneMode.Snapshot;
        }

        return WebPaneMode.Live;
    }
}
