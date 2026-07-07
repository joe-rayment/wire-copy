// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.ValueObjects.Browser;

/// <summary>
/// workspace-t1ok.3: the four categories a user can hand-assign to a link in
/// the layout wizard's label mode.
/// </summary>
public enum LinkLabelKind
{
    /// <summary>A story — the order of labeling defines the story order.</summary>
    Article,

    /// <summary>An ad/promo — excluded, and its slot pattern generalized away.</summary>
    Ad,

    /// <summary>Site chrome worth keeping — routed under the collapsed "More" menu.</summary>
    Menu,

    /// <summary>Site chrome to hide entirely.</summary>
    Ignore,
}

/// <summary>
/// workspace-t1ok.3: one hand-labeled link — the durable record of a user
/// correction. Persisted on <see cref="SiteHierarchyConfig.UserLabels"/> so
/// every later AI round (refine, re-infer, label fallback) treats it as ground
/// truth instead of starting from a blank slate: labeled articles keep their
/// relative order, labeled ads/ignores stay hidden, labeled menu links stay in
/// the "More" menu.
/// </summary>
public record UserLinkLabel
{
    public required string Url { get; init; }

    /// <summary>Display text at label time — a prompt/debugging aid, never an identifier.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The link's <see cref="LinkInfo.ParentSelector"/> at label time.</summary>
    public string? ParentSelector { get; init; }

    public required LinkLabelKind Kind { get; init; }

    /// <summary>Article order, 1 = top story. Null for non-article labels.</summary>
    public int? Rank { get; init; }

    public DateTime LabeledAt { get; init; }
}
