// Educational and personal use only.

namespace TermReader.Infrastructure.Browser.UI.Renderers;

/// <summary>
/// Shared layout parameters for the link tree card view.
/// </summary>
internal record LinkTreeLayout(
    int Width,
    int StandardCardHeight,
    int GroupHeaderHeight,
    int CompactThreshold);
