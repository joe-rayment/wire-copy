// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Browser;

/// <summary>
/// workspace-romy.2 — per-link visual geometry measured on the live page just
/// before the HTML snapshot is taken (document coordinates, CSS pixels).
/// Static-HTML extraction cannot see prominence: a wide main-column headline
/// and a right-rail promo item can have near-identical markup. Geometry is the
/// signal that separates them, both for the heuristic importance score and for
/// the AI layout analyzer's prompt.
/// </summary>
/// <param name="X">Left edge in document coordinates.</param>
/// <param name="Y">Top edge in document coordinates.</param>
/// <param name="Width">Rendered width in CSS pixels.</param>
/// <param name="Height">Rendered height in CSS pixels.</param>
/// <param name="FontSize">Computed font-size in CSS pixels (rounded).</param>
/// <param name="FontWeight">Computed numeric font-weight (400 normal, 700 bold).</param>
/// <param name="AboveFold">True when the link's top edge was inside the first viewport.</param>
public sealed record LinkGeometry(
    int X,
    int Y,
    int Width,
    int Height,
    int FontSize,
    int FontWeight,
    bool AboveFold)
{
    /// <summary>Treats 600+ as visually bold (semi-bold and up).</summary>
    public bool IsBold => FontWeight >= 600;

    /// <summary>
    /// Parses the <c>data-wc-geom</c> attribute stamped by the page loader:
    /// "x,y,w,h,fontSize,fontWeight,aboveFold(0|1)". Returns null on any
    /// malformed input — geometry is always optional.
    /// </summary>
    public static LinkGeometry? Parse(string? attribute)
    {
        if (string.IsNullOrWhiteSpace(attribute))
        {
            return null;
        }

        var parts = attribute.Split(',');
        if (parts.Length != 7)
        {
            return null;
        }

        var values = new int[7];
        for (var i = 0; i < 7; i++)
        {
            if (!int.TryParse(parts[i], out values[i]))
            {
                return null;
            }
        }

        if (values[2] <= 0 || values[3] <= 0)
        {
            return null;
        }

        return new LinkGeometry(
            X: values[0],
            Y: values[1],
            Width: values[2],
            Height: values[3],
            FontSize: values[4],
            FontWeight: values[5],
            AboveFold: values[6] == 1);
    }
}
