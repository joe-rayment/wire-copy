// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Shared layout parameters for the launcher grid.
/// </summary>
internal record LauncherLayout(
    int Width,
    int Columns,
    int CellHeight,
    int VisibleRows,
    int CellWidth,
    int HeaderLines,
    int FooterLines);
