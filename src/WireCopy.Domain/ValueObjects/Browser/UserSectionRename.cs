// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Browser;

/// <summary>
/// workspace-nbvb.4: one user-chosen section name — the durable record of a
/// rename. Persisted on <see cref="SiteHierarchyConfig.UserSectionNames"/> and
/// re-applied after every model round and label derivation (both build fresh
/// section lists), matched to a section by identifier overlap, so a rename
/// survives refines the same way labels do.
/// </summary>
public record UserSectionRename
{
    /// <summary>
    /// The renamed section's identifiers (parent selectors + URL patterns) at
    /// rename time. A later section owns this rename when it shares ANY of them.
    /// </summary>
    public List<string> Identifiers { get; init; } = new();

    public required string Name { get; init; }

    public DateTime RenamedAt { get; init; }
}
