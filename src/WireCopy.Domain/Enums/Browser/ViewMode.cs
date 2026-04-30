// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Browser;

/// <summary>
/// Defines the rendering mode for displaying a web page in the terminal browser.
/// </summary>
public enum ViewMode
{
    /// <summary>
    /// Hierarchical link tree view with collapsible sections.
    /// Shows all links categorized by type (Content, Navigation, Footer).
    /// </summary>
    Hierarchical,

    /// <summary>
    /// Clean, readable article view with just text content.
    /// Removes ads, navigation, and formatting. Only available for article pages.
    /// </summary>
    Readable,

    /// <summary>
    /// Shows all collections as a list.
    /// </summary>
    CollectionList,

    /// <summary>
    /// Shows items within a selected collection.
    /// </summary>
    CollectionItems,

    /// <summary>
    /// Launcher home screen with bookmark tiles.
    /// </summary>
    Launcher
}
