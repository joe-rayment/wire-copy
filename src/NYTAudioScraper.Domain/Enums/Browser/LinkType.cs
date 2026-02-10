// Educational and personal use only.

namespace NYTAudioScraper.Domain.Enums.Browser;

/// <summary>
/// Categorizes links by their purpose and location on the page.
/// Used to determine initial collapse state in hierarchical view.
/// </summary>
public enum LinkType
{
    /// <summary>
    /// Main content links (articles, stories, primary page content).
    /// START EXPANDED in hierarchical view.
    /// </summary>
    Content,

    /// <summary>
    /// Navigation menu links (header, sidebar, nav bars).
    /// START COLLAPSED in hierarchical view.
    /// </summary>
    Navigation,

    /// <summary>
    /// Footer utility links (about, terms, privacy, etc.).
    /// START COLLAPSED in hierarchical view.
    /// </summary>
    Footer,

    /// <summary>
    /// External domain links (different host than current page).
    /// START COLLAPSED in hierarchical view.
    /// </summary>
    External
}
