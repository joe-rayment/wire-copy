// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Configuration;

/// <summary>
/// Which side of the screen the headed browser docks to in the side-by-side
/// "concert" view (workspace-v7mb).
/// </summary>
public enum DockSide
{
    /// <summary>Dock to the right half; the terminal stays on the left (default).</summary>
    Right,

    /// <summary>Dock to the left half; the terminal stays on the right.</summary>
    Left,
}
