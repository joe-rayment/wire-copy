// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Browser;

/// <summary>
/// Article-extraction page-type discriminator. Used as the key for per-site
/// <c>ArticleSelectorConfig</c> entries — each domain's saved layout file holds
/// one entry per page type, and we pick the highest-priority entry whose matcher
/// matches the live page.
///
/// <para>
/// This is a richer superset of <see cref="PageClassification"/>: classification
/// asks "is this a list of links or an article?" while <see cref="PageType"/>
/// asks "what flavour of article?" so the selector set can be tailored to the
/// page's actual DOM (live blogs, recipes, opinion columns, and standard news
/// articles all use different markup on most publishers).
/// </para>
/// </summary>
public enum PageType
{
    /// <summary>
    /// Page type unknown / not classified yet.
    /// </summary>
    Unknown,

    /// <summary>
    /// Standard article / news story (single piece of long-form text).
    /// </summary>
    Article,

    /// <summary>
    /// Live blog — sequence of timestamped posts on a single topic.
    /// </summary>
    LiveBlog,

    /// <summary>
    /// Recipe page (ingredients, steps).
    /// </summary>
    Recipe,

    /// <summary>
    /// Opinion / editorial column.
    /// </summary>
    Opinion,

    /// <summary>
    /// Section-front / link list. Article extraction does not normally apply
    /// here — listed for completeness so saved configs can record that a URL
    /// shape was investigated and found to be a section front.
    /// </summary>
    SectionFront,
}
