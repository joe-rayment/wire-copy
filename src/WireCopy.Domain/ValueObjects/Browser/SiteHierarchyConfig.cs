// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Browser;

public enum LayoutKind
{
    AiHierarchical,
    DocumentOrder,
    RssFeed,
    AiCurated,
}

public record SiteHierarchyConfig
{
    /// <summary>
    /// workspace-9k27.1/.2: importance score at/above which a content link counts
    /// as a "genuine story" for the exclusion guards (parse-time AND build-time).
    /// </summary>
    public const int HighImportanceScoreThreshold = 80;

    /// <summary>
    /// workspace-9k27.1/.2: an exclude rule that would hide more than this share
    /// of the page's high-importance links is wrong by construction and is
    /// skipped — at parse time (analyzer) and again on every revisit (builder),
    /// so a save-time-surgical selector that broadens on a redesign can't nuke
    /// the page.
    /// </summary>
    public const double MaxExcludeHighImportanceFraction = 0.25;

    /// <summary>
    /// workspace-9k27.1: minimum share of the page's content links the saved
    /// sections must still cover on a revisit. Below this the builder falls back
    /// to document order and flags the tree stale (never a silently empty page).
    /// Matches the wizard's setup-time MinCoverageFraction.
    /// </summary>
    public const double StaleCoverageFraction = 0.10;

    /// <summary>workspace-t1ok.3: cap on the <see cref="UserLabels"/> ledger.</summary>
    public const int MaxUserLabels = 200;

    /// <summary>workspace-t1ok.3: cap on the <see cref="UserInstructions"/> log.</summary>
    public const int MaxUserInstructions = 20;

    public required string Domain { get; init; }
    public required string UrlPattern { get; init; }
    public required List<HierarchySection> Sections { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string ModelVersion { get; init; }
    public LayoutKind Kind { get; init; } = LayoutKind.AiHierarchical;
    public string? RssFeedUrl { get; init; }
    public string? StructuralSignature { get; init; }
    public int Version { get; init; } = 1;
    public string? Strategy { get; init; }
    public AiCuratedResult? AiResult { get; init; }

    /// <summary>
    /// Durable exclusion rules (workspace-5oe9.1): content links whose
    /// <see cref="LinkInfo.ParentSelector"/> contains any of these fragments are
    /// dropped from the tree entirely. Unlike <see cref="AiCuratedResult.ExcludedLinkKeys"/>
    /// — which stores per-visit absolute URLs that go stale the moment the page
    /// changes — these selectors generalize across visits, so an AI-configured
    /// site keeps hiding ads/promos as the underlying articles rotate.
    /// </summary>
    public List<string> ExcludeSelectors { get; init; } = new();

    /// <summary>
    /// Durable exclusion rules (workspace-5oe9.1): content links whose
    /// <see cref="LinkInfo.Url"/> contains any of these substrings are dropped
    /// from the tree entirely (e.g. "/sponsored/", "/newsletter"). Generalizes
    /// across visits — see <see cref="ExcludeSelectors"/>.
    /// </summary>
    public List<string> ExcludeUrlPatterns { get; init; } = new();

    /// <summary>
    /// workspace-rpop.4: durable exclusion by SECTION HEADING — content links whose
    /// <see cref="LinkInfo.SectionTitle"/> equals one of these (e.g. "Sponsor Posts")
    /// are dropped. This is the DURABLE way to hide an ad/rail class on a messy
    /// aggregator: the heading text is stable across visits even when the block's
    /// markup uses a per-day random id, so an ad the user removes stays removed.
    /// </summary>
    public List<string> ExcludeSectionTitles { get; init; } = new();

    /// <summary>
    /// workspace-t1ok.3: content links whose <see cref="LinkInfo.ParentSelector"/>
    /// contains any of these fragments are routed OUT of the story sections and
    /// under the collapsed "More" chrome menu (kept reachable, out of the article
    /// flow). Same Contains semantics as <see cref="ExcludeSelectors"/>.
    /// </summary>
    public List<string> MoreSelectors { get; init; } = new();

    /// <summary>
    /// workspace-t1ok.3: content links whose <see cref="LinkInfo.Url"/> contains
    /// any of these substrings are routed under the collapsed "More" chrome menu.
    /// </summary>
    public List<string> MoreUrlPatterns { get; init; } = new();

    /// <summary>
    /// workspace-t1ok.3: the user's hand-labeled links — durable ground truth
    /// that every later AI round must honor (never regenerate away a user
    /// correction). Latest-wins per normalized URL, capped at
    /// <see cref="MaxUserLabels"/>. Labeled article ranks also order matched
    /// links within a section while those URLs are still on the page.
    /// </summary>
    public List<UserLinkLabel> UserLabels { get; init; } = new();

    /// <summary>
    /// workspace-t1ok.3: plain-English refinement instructions that have been
    /// APPLIED to this config, oldest first, capped at
    /// <see cref="MaxUserInstructions"/> — carried into later refine prompts so
    /// a new tweak can't silently undo an earlier one.
    /// </summary>
    public List<string> UserInstructions { get; init; } = new();

    /// <summary>
    /// workspace-5oe9.5/.6: set when this config cannot be trusted to curate
    /// durably on a revisit — either a legacy per-URL snapshot (Version&lt;3,
    /// empty Sections, AiResult present) or a fresh analysis whose derived
    /// selectors failed the self-test. The UI surfaces it as a "re-run AI
    /// setup" nudge; a passive revisit never silently rots into document order
    /// without telling the user.
    /// </summary>
    public bool NeedsReanalyze { get; init; }

    /// <summary>
    /// workspace-9k27.2: how many exclude rules the over-exclusion guard dropped
    /// while parsing this config (each would have hidden too many real stories).
    /// Transient — the wizard preview surfaces it so a user whose steering was
    /// vetoed learns WHY the change didn't take, instead of silence. Not
    /// persisted.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int DroppedExcludeRuleCount { get; init; }
}

public record HierarchySection
{
    public required string Name { get; init; }
    public required int SortOrder { get; init; }
    public List<string> ParentSelectors { get; init; } = new();
    public List<string> UrlPatterns { get; init; } = new();
    public bool StartCollapsed { get; init; }

    /// <summary>
    /// workspace-9wm6: optional cap on how many matched links this section keeps
    /// (in document order). Null = unlimited. Used to pin a "lead" section to the
    /// single top story when its selector is too broad; the greedy tree builder
    /// then re-offers the overflow to later sections, so no story is lost.
    /// </summary>
    public int? MaxLinks { get; init; }
}
