// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.ValueObjects.Browser;

namespace WireCopy.Application.Interfaces.Browser;

public interface IHierarchyAnalyzer
{
    bool IsConfigured { get; }

    Task<SiteHierarchyConfig> AnalyzePageHierarchyAsync(
        byte[] screenshot,
        List<LinkInfo> links,
        string pageUrl,
        string? promptSuffix = null,
        CancellationToken cancellationToken = default);

    Task<AiCuratedResult> AnalyzeCuratedAsync(
        byte[]? screenshot,
        List<LinkInfo> links,
        string pageUrl,
        string? userGuidance = null,
        string? reasoningEffortOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// workspace-5oe9.7 — round 1 of the question-driven AI setup contract.
    /// Returns a proposed pattern (top story / tiers / exclude) PLUS a bounded
    /// set of clarifying questions (capped at the configured MaxSetupQuestions),
    /// each pre-filled with the model's best guess so the user usually just
    /// confirms.
    /// </summary>
    Task<SiteSetupProposal> ProposeSetupQuestionsAsync(
        byte[]? screenshot,
        List<LinkInfo> links,
        string pageUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// workspace-5oe9.7 — round 2 of the AI setup contract. Given the proposal
    /// and the user's answers, returns a durable config whose sections carry
    /// CSS/URL-pattern identifiers (never per-URL snapshot keys) so the layout
    /// generalizes across visits. workspace-romy.8: wrapped in
    /// <see cref="InferredPattern"/> carrying the model's confidence and at
    /// most one ignorable targeted confirm question for the preview card.
    /// </summary>
    Task<InferredPattern> InferPatternFromAnswersAsync(
        byte[]? screenshot,
        List<LinkInfo> links,
        string pageUrl,
        SiteSetupProposal proposal,
        IReadOnlyList<SetupAnswer> answers,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// workspace-q77e — incremental refinement. Given the layout the user is
    /// CURRENTLY looking at (and already implicitly accepted) plus one plain-English
    /// change they typed, returns an edited layout that applies ONLY that change and
    /// keeps everything else identical. Unlike re-running
    /// <see cref="InferPatternFromAnswersAsync"/> from the original proposal, this
    /// never re-derives from scratch, so a small tweak cannot throw away the parts of
    /// an almost-perfect layout that were already working.
    /// </summary>
    Task<InferredPattern> RefineLayoutAsync(
        byte[]? screenshot,
        List<LinkInfo> links,
        string pageUrl,
        SiteHierarchyConfig currentConfig,
        string instruction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// workspace-frpl.10 (B9b) — a NEW single-label classify (distinct from the
    /// interactive-wizard surface above): given the section headings present on a
    /// page today and a durable intent (the recipe step's section name + aliases),
    /// returns the ONE candidate heading that best matches, with a 0..1 confidence,
    /// or <see cref="SectionClassification.None"/> when nothing maps. Used only as
    /// the budgeted, confidence-gated, self-tested last-resort recovery tier for a
    /// scheduled run whose durable section drifted beyond deterministic re-derivation.
    /// </summary>
    Task<SectionClassification> ClassifySectionAsync(
        IReadOnlyList<string> candidateLabels,
        string intent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// workspace-45ji.3: OPTIONAL vision tiebreak for the LEAD only. Given a
    /// screenshot and a small set of near-equally-prominent candidate lead links,
    /// returns the INDEX (into <paramref name="candidates"/>) of the one that is
    /// visually dominant on the page, or -1 to keep the current lead. Invoked ONLY
    /// when the deterministic gate cannot decide the lead (an importance
    /// tie-cluster) and a screenshot exists — never the default path. Tiny output.
    /// </summary>
    Task<int> VerifyLeadWithVisionAsync(
        byte[] screenshot,
        IReadOnlyList<LinkInfo> candidates,
        string pageUrl,
        CancellationToken cancellationToken = default);
}
