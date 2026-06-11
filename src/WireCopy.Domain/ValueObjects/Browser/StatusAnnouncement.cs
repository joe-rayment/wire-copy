// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Browser;

/// <summary>
/// One key/action pair advertised by a <see cref="StatusAnnouncement"/>
/// (workspace-wef6.4) — rendered as an accent key plus a dim ":action".
/// </summary>
public readonly record struct StatusKeyHint(string Key, string Action);

/// <summary>
/// A transient status announcement (workspace-wef6.4): a state change telling
/// the user what just happened WITH the keys that control it, e.g.
/// "▶ Speed reading 350 WPM — &lt;:slower &gt;:faster f:stop". Rendered into
/// the status line's Transient channel for its TTL, then disappears (or
/// collapses to ambient state where one exists).
/// </summary>
public sealed record StatusAnnouncement
{
    /// <summary>Leading glyph from the app's glyph language (⏸ ▶ ✓ ⚡ ⇉), or null.</summary>
    public string? Glyph { get; init; }

    /// <summary>The announcement copy ("Speed reading 350 WPM").</summary>
    public required string Text { get; init; }

    /// <summary>Keys that control the announced state, appended after an em dash.</summary>
    public IReadOnlyList<StatusKeyHint> Keys { get; init; } = Array.Empty<StatusKeyHint>();

    /// <summary>
    /// Compact fallback used when the line is squeezed (e.g. "▶350").
    /// Null derives a fallback from glyph + text.
    /// </summary>
    public string? ShortText { get; init; }
}
