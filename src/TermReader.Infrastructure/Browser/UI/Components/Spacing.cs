// Educational and personal use only.

namespace TermReader.Infrastructure.Browser.UI.Components;

/// <summary>
/// Unified spacing constants for consistent layout across views.
/// </summary>
internal static class Spacing
{
    /// <summary>Base indent (2 spaces).</summary>
    public const int Indent1 = 2;

    /// <summary>Double indent (4 spaces).</summary>
    public const int Indent2 = 4;

    /// <summary>Section padding (1 blank line).</summary>
    public const int SectionPadding = 1;

    /// <summary>Standard left margin for content.</summary>
    public const int ContentMargin = 2;

    /// <summary>Indent for nested items under a group header.</summary>
    public const int GroupChildIndent = 4;

    /// <summary>Number of header lines reserved at top of scrollable views.</summary>
    public const int DefaultHeaderLines = 2;

    /// <summary>Number of status bar lines reserved at bottom.</summary>
    public const int StatusBarLines = 3;
}
