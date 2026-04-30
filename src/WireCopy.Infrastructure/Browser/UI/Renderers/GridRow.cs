// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Entities.Browser;

namespace WireCopy.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Row in the 2D grid, holding left and optional right nodes.
/// </summary>
internal record GridRow(LinkNode Left, LinkNode? Right, bool IsGroupHeader, int StartNodeIndex);
