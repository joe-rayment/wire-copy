// Educational and personal use only.

namespace TermReader.Domain.Enums.Browser;

/// <summary>
/// Priority levels for background page pre-loading.
/// Lower numeric value = higher priority.
/// </summary>
public enum PreloadPriority
{
    /// <summary>
    /// The currently selected/highlighted link. Highest priority.
    /// </summary>
    Focused = 0,

    /// <summary>
    /// Links within a few positions of the focused link.
    /// </summary>
    Nearby = 1,

    /// <summary>
    /// Remaining content links on the page. Lowest priority.
    /// </summary>
    Speculative = 2
}
