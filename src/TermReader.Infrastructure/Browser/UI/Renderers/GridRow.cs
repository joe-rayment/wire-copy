// Educational and personal use only.

using TermReader.Domain.Entities.Browser;

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Row in the 2D grid, holding left and optional right nodes.
/// </summary>
internal record GridRow(LinkNode Left, LinkNode? Right, bool IsGroupHeader, int StartNodeIndex);
