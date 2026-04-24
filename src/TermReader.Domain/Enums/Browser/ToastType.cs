// Educational and personal use only.

namespace TermReader.Domain.Enums.Browser;

/// <summary>
/// Defines the type of toast notification, which determines its border color and icon.
/// </summary>
public enum ToastType
{
    /// <summary>
    /// Informational toast (cyan border). Auto-dismissed on next keypress.
    /// </summary>
    Info,

    /// <summary>
    /// Success/confirmation toast (green border). Auto-dismissed on next keypress.
    /// </summary>
    Success,

    /// <summary>
    /// Error toast (red border). Sticky — persists until user presses Esc.
    /// </summary>
    Error,

    /// <summary>
    /// Celebration toast (pink border). Sticky — persists until user presses Esc.
    /// </summary>
    Celebration
}
