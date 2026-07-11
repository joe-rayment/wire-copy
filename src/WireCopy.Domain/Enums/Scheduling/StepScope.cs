// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Scheduling;

/// <summary>
/// workspace-42q8.2 — what part of the source page a recipe step pulls from.
/// Before this enum every step pinned a named section, which made single-list
/// sites (saved DocumentOrder layouts with no sections) unschedulable.
/// </summary>
public enum StepScope
{
    /// <summary>A named section of the saved layout (the original frpl.2 model).</summary>
    PinnedSection,

    /// <summary>
    /// Every content link on the page (excludes still applied), in document
    /// order — the natural unit for single-list sites.
    /// </summary>
    WholePage,
}
