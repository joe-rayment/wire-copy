// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Application.DTOs.Scheduling;

/// <summary>workspace-frpl.3 — outcome of resolving a recipe step's section against today's links.</summary>
public enum ResolutionStatus
{
    /// <summary>The section matched ≥1 article; <see cref="SectionResolution.Items"/> is populated.</summary>
    Resolved,

    /// <summary>The section exists in the config but matched 0 articles today (site may have drifted).</summary>
    ZeroMatch,

    /// <summary>The referenced section is not in the saved config (deleted/renamed).</summary>
    SectionNotFound,
}

/// <summary>Which criterion carried the match — Selector is the durable marquee path.</summary>
public enum SectionMatchTier
{
    Selector,
    UrlPattern,
    HeadingName,
    HeadingAlias,
}

/// <summary>
/// workspace-frpl.3 — the resolved articles for one recipe step. A non-Resolved
/// status ALWAYS carries a human <see cref="Diagnostic"/> and empty
/// <see cref="Items"/> — never an empty list dressed up as success.
/// </summary>
public sealed record SectionResolution
{
    public required ResolutionStatus Status { get; init; }

    public IReadOnlyList<(string Url, string Title)> Items { get; init; } = Array.Empty<(string, string)>();

    /// <summary>How many links matched the section (before TakeMode trimming).</summary>
    public int MatchCount { get; init; }

    public SectionMatchTier? Tier { get; init; }

    public string? Diagnostic { get; init; }
}
