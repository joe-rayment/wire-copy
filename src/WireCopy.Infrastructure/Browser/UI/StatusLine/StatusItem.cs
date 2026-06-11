// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.StatusLine;

/// <summary>
/// One independent piece of status-line content (workspace-wef6 B1). Declares
/// its channel, its width-degradation variants (longest first), an optional
/// expiry for transients, and a priority for stable ordering within a channel.
/// </summary>
internal sealed record StatusItem
{
    /// <summary>Channel controlling drop/degrade order. See <see cref="StatusChannel"/>.</summary>
    public required StatusChannel Channel { get; init; }

    /// <summary>
    /// Text variants ordered longest → shortest. The composer steps down this
    /// list one variant at a time when the line is over budget, before any
    /// item is dropped. Must contain at least one variant.
    /// </summary>
    public required IReadOnlyList<StatusSegment[]> Variants { get; init; }

    /// <summary>
    /// Absolute expiry for Transient items; null means sticky/persistent.
    /// The composer filters expired items using its injected clock.
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>Stable ordering within a channel; lower renders first (more important).</summary>
    public int Priority { get; init; }

    /// <summary>
    /// Convenience: a single-variant item with one uniformly styled run.
    /// </summary>
    public static StatusItem Text(StatusChannel channel, StatusStyle style, string text, int priority = 0)
        => new()
        {
            Channel = channel,
            Variants = new[] { new[] { new StatusSegment(text, style) } },
            Priority = priority,
        };

    /// <summary>
    /// Convenience: builds the standard "Key:action" hint pair — accent key,
    /// dim ":action" — matching the existing hint grammar.
    /// </summary>
    public static StatusSegment[] KeyHint(string key, string action)
        =>
        [
            new StatusSegment(key, StatusStyle.Accent),
            new StatusSegment($":{action}", StatusStyle.Dim),
        ];
}
