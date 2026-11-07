// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Scheduling;

namespace WireCopy.Infrastructure.Scheduling;

/// <summary>One section's selection inside a routine pick session.</summary>
public sealed class SectionPick
{
    public StepScope Scope { get; set; } = StepScope.PinnedSection;

    public TakeMode TakeMode { get; set; } = TakeMode.WholeSection;

    public int? TakeCount { get; set; }

    /// <summary>1-based positions; meaningful only when <see cref="TakeMode"/> is Slots.</summary>
    public SortedSet<int> Positions { get; } = new();
}
