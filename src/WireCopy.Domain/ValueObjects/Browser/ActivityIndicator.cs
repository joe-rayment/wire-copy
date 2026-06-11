// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Browser;

/// <summary>
/// One entry in the unified activity slot (workspace-wef6.5): a single
/// animated "is it working" indicator in the status line proving that real
/// work is running. Producers register by source; the highest-priority
/// (lowest value) entry wins the slot.
/// </summary>
public sealed record ActivityIndicator
{
    /// <summary>Producer identity ("load", "ai", "podcast"). One live entry per source.</summary>
    public required string Source { get; init; }

    /// <summary>Indicator copy ("Extracting content… (3s)", "✨ analyzing layout…").</summary>
    public required string Text { get; init; }

    /// <summary>Slot priority: foreground load (0) &gt; AI (1) &gt; podcast (2) &gt; prefetch (3, derived).</summary>
    public int Priority { get; init; }

    /// <summary>Optional completion percent appended to the copy.</summary>
    public int? Percent { get; init; }
}
