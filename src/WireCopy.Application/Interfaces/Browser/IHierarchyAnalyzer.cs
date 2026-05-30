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
    /// and the user's answers, returns a durable <see cref="SiteHierarchyConfig"/>
    /// whose sections carry CSS/URL-pattern identifiers (never per-URL snapshot
    /// keys) so the layout generalizes across visits.
    /// </summary>
    Task<SiteHierarchyConfig> InferPatternFromAnswersAsync(
        byte[]? screenshot,
        List<LinkInfo> links,
        string pageUrl,
        SiteSetupProposal proposal,
        IReadOnlyList<SetupAnswer> answers,
        CancellationToken cancellationToken = default);
}
