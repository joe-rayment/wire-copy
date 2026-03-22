// Educational and personal use only.

namespace TermReader.Domain.Enums.Browser;

/// <summary>
/// Classifies whether a LinkInfo is a regular link or a section header.
/// </summary>
public enum HeaderType
{
    /// <summary>
    /// Not a header — a regular link.
    /// </summary>
    None,

    /// <summary>
    /// Top-level group header (e.g., Content, Navigation, External, Footer).
    /// </summary>
    TopLevelGroup,

    /// <summary>
    /// Sub-section header within a group (e.g., article section titles).
    /// </summary>
    SubSection,
}
