// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Scheduling;

/// <summary>
/// workspace-frpl.2 — how many articles a recipe step pulls from its resolved
/// section.
/// </summary>
public enum TakeMode
{
    /// <summary>All articles in the section (capped downstream).</summary>
    WholeSection,

    /// <summary>The first N in document order.</summary>
    TopN,

    /// <summary>Exactly the single top story (the lead of the section).</summary>
    SingleTopStory,
}
