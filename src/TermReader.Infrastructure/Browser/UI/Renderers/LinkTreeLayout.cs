// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Shared layout parameters for the link tree 2-column grid view.
/// </summary>
internal record LinkTreeLayout(
    int Width,
    int Columns,
    int CellHeight,
    int CellWidth,
    int VisibleRows,
    int HeaderLines,
    int StatusBarLines);
