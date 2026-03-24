// Educational and personal use only.

namespace TermReader.Infrastructure.Browser.Models;

/// <summary>
/// Cookie data wrapper with metadata.
/// </summary>
public class CookieDataContainer
{
    /// <summary>
    /// Gets or sets list of cookies.
    /// </summary>
    public required List<CookieData> Cookies { get; set; }

    /// <summary>
    /// Gets or sets additional metadata.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}
