// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.StatusLine;

/// <summary>
/// The five prioritized channels of the composed status line (workspace-wef6).
/// Lower values are more important: an Alert is never dropped at any width,
/// while Hints only ever render into space nothing else wanted.
/// </summary>
internal enum StatusChannel
{
    /// <summary>Sticky until resolved (HITL / blocked domain). Never dropped.</summary>
    Alert = 0,

    /// <summary>TTL'd action feedback ("▶ Speed reading 350 WPM — &lt;:slower &gt;:faster"). Survives re-renders until expiry.</summary>
    Transient = 1,

    /// <summary>Animated while real work runs (prefetch n/total, AI analysis, podcast %).</summary>
    Activity = 2,

    /// <summary>Quiet state badges shown only while non-default (selection count, /search, ▶WPM, ⇉docked).</summary>
    Ambient = 3,

    /// <summary>Adaptive shortcut hints — fill remaining space, lowest priority.</summary>
    Hint = 4,
}

/// <summary>
/// Semantic style roles for status segments. The composer works purely on
/// plain text + roles; the painter maps roles to the active theme palette,
/// so composition stays byte-measurable and theme-independent.
/// </summary>
internal enum StatusStyle
{
    Primary,
    Secondary,
    Dim,
    Accent,
    Warning,
    Prompt,
    Success,
}
