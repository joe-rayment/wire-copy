// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Application.DTOs.Browser;

/// <summary>
/// Tracks the progress of a page load for display on the loading screen.
/// Updated by the background load task and read by the main loop's timer tick.
/// </summary>
public sealed record LoadingStatus
{
    /// <summary>
    /// Current stage of the load (e.g., "Connecting...", "Downloading...", "Extracting links...").
    /// </summary>
    public string Stage { get; init; } = "Loading...";

    /// <summary>
    /// Elapsed time since load started, in milliseconds.
    /// </summary>
    public long ElapsedMs { get; init; }

    /// <summary>
    /// URL being loaded.
    /// </summary>
    public string Url { get; init; } = string.Empty;
}
