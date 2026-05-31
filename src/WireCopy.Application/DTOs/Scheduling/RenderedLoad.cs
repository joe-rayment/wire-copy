// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Scheduling;

/// <summary>workspace-frpl.6 — coarse result of a headless rendered page load.</summary>
public enum LoadOutcome
{
    /// <summary>Page rendered and HTML captured.</summary>
    Ok,

    /// <summary>A paywall / bot challenge needs a human — no usable content headlessly.</summary>
    Blocked,

    /// <summary>Navigation timed out or failed.</summary>
    LoadFailed,
}

/// <summary>workspace-frpl.6 — rendered HTML (or a failure classification) from a headless load.</summary>
public sealed record RenderedLoad
{
    public required LoadOutcome Outcome { get; init; }

    public string? Html { get; init; }

    public string FinalUrl { get; init; } = string.Empty;
}
