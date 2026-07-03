// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Scheduling;

/// <summary>workspace-frpl.6 — coarse result of an unattended rendered page load (headful browser, no TUI).</summary>
public enum LoadOutcome
{
    /// <summary>Page rendered and HTML captured.</summary>
    Ok,

    /// <summary>A paywall / bot challenge needs a human — no usable content without one.</summary>
    Blocked,

    /// <summary>Navigation timed out or failed.</summary>
    LoadFailed,
}

/// <summary>workspace-frpl.6 — rendered HTML (or a failure classification) from an unattended load.</summary>
public sealed record RenderedLoad
{
    public required LoadOutcome Outcome { get; init; }

    public string? Html { get; init; }

    public string FinalUrl { get; init; } = string.Empty;
}
