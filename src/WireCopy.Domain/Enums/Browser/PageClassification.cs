// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Browser;

/// <summary>
/// Classification of a web page based on its structure and content.
/// Drives downstream decisions: content extraction, cache TTL, view mode, fallback strategy.
/// </summary>
public enum PageClassification
{
    /// <summary>
    /// Page type has not been determined. Treated as Article for safety (conservative).
    /// </summary>
    Unknown,

    /// <summary>
    /// Page with primary article/story content (single piece of long-form text).
    /// </summary>
    Article,

    /// <summary>
    /// Page that is primarily a list of links to other content (section front, homepage, index).
    /// </summary>
    LinkList,
}
