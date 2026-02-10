// Educational and personal use only.

namespace NYTAudioScraper.Domain.Enums.Browser;

/// <summary>
/// Represents the collapse/expand state of a tree node in hierarchical view.
/// </summary>
public enum NodeCollapseState
{
    /// <summary>
    /// Node is expanded, showing all child nodes.
    /// Display indicator: ▼
    /// </summary>
    Expanded,

    /// <summary>
    /// Node is collapsed, hiding child nodes.
    /// Display indicator: ▶
    /// </summary>
    Collapsed
}
