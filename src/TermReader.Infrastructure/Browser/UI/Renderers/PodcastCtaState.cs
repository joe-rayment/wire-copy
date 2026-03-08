// Educational and personal use only.

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Visual state of the podcast call-to-action button in collection views.
/// </summary>
internal enum PodcastCtaState
{
    /// <summary>
    /// Default state — collection has items and TTS is configured.
    /// </summary>
    Idle,

    /// <summary>
    /// Brief highlight flash when the user presses the podcast key.
    /// </summary>
    Pressed,

    /// <summary>
    /// Collection is empty — button is dimmed/inactive.
    /// </summary>
    Disabled,

    /// <summary>
    /// TTS API key is not configured — renders muted with "Setup required" subtitle.
    /// </summary>
    Unconfigured,
}
