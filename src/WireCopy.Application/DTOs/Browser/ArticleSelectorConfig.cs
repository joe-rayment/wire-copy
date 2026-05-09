// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.Enums.Browser;

namespace WireCopy.Application.DTOs.Browser;

/// <summary>
/// Per-domain saved article-extraction layout. One file per domain at
/// <c>{LocalAppData}/WireCopy/layouts/{domain}.json</c> holds one root
/// <see cref="ArticleSelectorConfig"/>; the config holds an ordered list of
/// page-type entries, and the extractor picks the highest-priority entry whose
/// <see cref="PageTypeMatcher"/> matches the live page.
///
/// <para>
/// All selector arrays are ordered: the extractor tries them top-to-bottom and
/// uses the first one that produces non-empty output. Multiple selectors per
/// field absorb minor markup churn without an AI re-run.
/// </para>
/// </summary>
public sealed record ArticleSelectorConfig
{
    /// <summary>
    /// Schema version. Bumped on breaking shape changes; on read mismatch the
    /// store treats the file as a cache miss and lets the AI regenerate.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Lower-cased domain (e.g. <c>nytimes.com</c>).
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// Ordered list of page-type entries. Highest <c>Priority</c> wins; ties
    /// fall back to declaration order.
    /// </summary>
    public List<PageTypeEntry> PageTypes { get; init; } = new();

    /// <summary>
    /// When this top-level config was last written.
    /// </summary>
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// One page-type entry inside an <see cref="ArticleSelectorConfig"/>: a matcher
/// that decides whether the entry applies, the selector set to use when it
/// does, the quality thresholds we expect the result to meet, and provenance
/// metadata for diagnostics / regeneration tracking.
/// </summary>
public sealed record PageTypeEntry
{
    /// <summary>
    /// Human-readable name (e.g. <c>article</c>, <c>live-blog</c>, <c>recipe</c>).
    /// Must be unique within the parent config.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Page-type classification this entry produces when matched.
    /// </summary>
    public PageType PageType { get; init; } = PageType.Article;

    /// <summary>
    /// Higher wins when multiple entries match the same URL. Generic articles
    /// should be low (e.g. 10), specific shapes high (live-blog 100, recipe 100).
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Conditions deciding whether this entry applies to the page.
    /// </summary>
    public PageTypeMatcher Matcher { get; init; } = new();

    /// <summary>
    /// Selectors to apply when the matcher hits.
    /// </summary>
    public ArticleSelectors Selectors { get; init; } = new();

    /// <summary>
    /// Thresholds the extracted body must meet for the entry to be considered
    /// successful (failures escalate to the heuristic / AI fallback).
    /// </summary>
    public ArticleQualityThresholds Quality { get; init; } = new();

    /// <summary>
    /// Diagnostic metadata about how / when this entry was produced.
    /// </summary>
    public ProvenanceInfo Provenance { get; init; } = new();
}

/// <summary>
/// Conditions evaluated against the loaded page to decide whether a
/// <see cref="PageTypeEntry"/> should be selected. All populated fields must
/// match (logical AND); empty / null fields are ignored.
/// </summary>
public sealed record PageTypeMatcher
{
    /// <summary>
    /// Optional regex evaluated against the URL (full or path). Matches via
    /// <c>Regex.IsMatch</c> with <c>IgnoreCase</c>.
    /// </summary>
    public string? UrlPattern { get; init; }

    /// <summary>
    /// Required <c>&lt;meta&gt;</c> tags. Each entry is <c>name/property -&gt; expected substring</c>.
    /// The page must contain a meta tag whose <c>name</c> or <c>property</c>
    /// matches the key, with a <c>content</c> attribute containing the value.
    /// </summary>
    public Dictionary<string, string> MetaTags { get; init; } = new();

    /// <summary>
    /// Required value in any <c>application/ld+json</c> block's <c>@type</c>
    /// field (e.g. <c>NewsArticle</c>, <c>LiveBlogPosting</c>).
    /// </summary>
    public string? LdJsonType { get; init; }

    /// <summary>
    /// Required substring in the <c>&lt;body&gt;</c>'s <c>class</c> attribute
    /// (e.g. <c>page-recipe</c>).
    /// </summary>
    public string? BodyClassContains { get; init; }
}

/// <summary>
/// Selector arrays for the article fields. Each is tried top-to-bottom; the
/// extractor uses the first selector that produces non-empty output for that
/// field. CSS / XPath are both accepted (parser-dependent).
/// </summary>
public sealed record ArticleSelectors
{
    public List<string> Headline { get; init; } = new();

    public List<string> Byline { get; init; } = new();

    public List<string> PublishDate { get; init; } = new();

    public List<string> Body { get; init; } = new();

    /// <summary>
    /// Selectors whose matched nodes should be removed from the body before
    /// paragraph extraction (ads, related-coverage modules, comment widgets).
    /// </summary>
    public List<string> ExcludeRegions { get; init; } = new();
}

/// <summary>
/// Minimum quality bar an entry's extraction result must clear to be kept.
/// </summary>
public sealed record ArticleQualityThresholds
{
    /// <summary>
    /// Minimum word count across body paragraphs.
    /// </summary>
    public int MinWords { get; init; } = 100;

    /// <summary>
    /// Minimum number of body paragraphs.
    /// </summary>
    public int MinParagraphs { get; init; } = 3;
}

/// <summary>
/// Diagnostic metadata about how / when the entry was generated.
/// </summary>
public sealed record ProvenanceInfo
{
    /// <summary>
    /// Model that produced the entry (e.g. <c>gpt-5-mini</c>) or <c>"manual"</c>
    /// when hand-edited.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// When the entry was produced.
    /// </summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// URL the entry was generated against — useful for diffing and re-running.
    /// </summary>
    public string? SampleUrl { get; init; }

    /// <summary>
    /// Number of consecutive failed extractions since last success. Used as a
    /// stale-entry signal — after enough failures the AI re-runs.
    /// </summary>
    public int ConsecutiveFailures { get; init; }
}
