// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Application.Interfaces.Scheduling;

/// <summary>
/// workspace-frpl.6 — headless "load a homepage → extracted links + its saved
/// layout config" for the scheduler. Reuses the existing rendered-load path and
/// the durable config store; does NOT touch NavigationService or the foreground
/// page-access queue.
/// </summary>
public interface IHeadlessSectionLoader
{
    Task<HeadlessSectionLoad> LoadLinksAndConfigAsync(string sourceUrl, CancellationToken cancellationToken = default);
}

/// <summary>The links + matched config a scheduled step needs, plus the load classification.</summary>
public sealed record HeadlessSectionLoad
{
    public required LoadOutcome Outcome { get; init; }

    public IReadOnlyList<LinkInfo> Links { get; init; } = Array.Empty<LinkInfo>();

    public SiteHierarchyConfig? Config { get; init; }
}
