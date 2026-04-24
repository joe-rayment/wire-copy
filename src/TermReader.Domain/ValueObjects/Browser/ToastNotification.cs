// Educational and personal use only.

using TermReader.Domain.Enums.Browser;

namespace TermReader.Domain.ValueObjects.Browser;

/// <summary>
/// Represents a toast notification displayed as an overlay in the top-right corner.
/// Only one toast is visible at a time; new toasts replace the current one.
/// </summary>
public record ToastNotification
{
    /// <summary>
    /// The type of toast, which determines border color and icon.
    /// </summary>
    public required ToastType Type { get; init; }

    /// <summary>
    /// Primary message text (e.g., "Cache warmed", "Bookmark added").
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Optional detail text displayed after the message (e.g., "24/24 articles", "arstechnica").
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Whether this toast persists across redraws until dismissed with Esc.
    /// Error and Celebration toasts are sticky by default.
    /// </summary>
    public bool IsSticky => Type is ToastType.Error or ToastType.Celebration;

    /// <summary>
    /// Whether this toast has been rendered at least once.
    /// Used to implement auto-dismiss: non-sticky toasts are cleared after one render pass.
    /// </summary>
    public bool HasBeenRendered { get; init; }
}
