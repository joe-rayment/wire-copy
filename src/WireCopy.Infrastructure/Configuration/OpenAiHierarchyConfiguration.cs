// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Configuration;

/// <summary>
/// Configuration for OpenAI-backed page-hierarchy analysis (link-list
/// classification used by the AI Curated scraping strategy).
///
/// The API key itself lives on <see cref="OpenAiTtsConfiguration.ApiKey"/> /
/// the <c>OpenAiApiKey</c> settings-store entry — the hierarchy analyzer
/// shares the single OpenAI credential the user already supplied for TTS,
/// so no separate key is read here.
/// </summary>
public class OpenAiHierarchyConfiguration
{
    public const string SectionName = "OpenAiHierarchy";

    /// <summary>
    /// Gets the OpenAI chat model to use for hierarchy / curated analysis.
    /// Default: <c>gpt-5-mini</c> — cheap, vision-capable, supports strict
    /// JSON Schema and the <c>minimal</c> reasoning effort tier.
    /// </summary>
    public string Model { get; init; } = "gpt-5-mini";

    /// <summary>
    /// Gets the reasoning-effort tier passed to the model. Default
    /// <c>minimal</c> keeps TTFT low for the classification task; bump to
    /// <c>low</c> / <c>medium</c> only if accuracy regresses on a target site.
    /// </summary>
    public string ReasoningEffort { get; init; } = "minimal";

    /// <summary>
    /// Gets the maximum completion tokens. 4096 mirrors the previous Anthropic
    /// budget — generous headroom for the largest curated-section payload we
    /// have observed (~30 stories × 4 fields).
    /// </summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>
    /// TTL (days) for cached AI Curated results. After this age the analyzer
    /// re-runs on the next visit so layouts adapt to site changes.
    /// </summary>
    public int AiCuratedCacheDays { get; init; } = 30;

    /// <summary>
    /// Per-month soft cap on tokens billed to AI article extraction. When the
    /// running total for the current calendar month (UTC) reaches this value,
    /// further AI extractor calls are skipped — the heuristic path runs alone.
    /// Counter is keyed <c>AiArticleTokens:YYYYMM</c> in the settings store
    /// and rotates automatically. Set to <c>0</c> to disable the cap.
    /// </summary>
    public int MonthlyTokenBudget { get; init; } = 200_000;

    /// <summary>
    /// workspace-5oe9.7: hard cap on the number of clarifying questions the AI
    /// setup wizard asks. Enforced both in the prompt and by a code clamp so a
    /// misbehaving model can never make the user answer 20 things. Each question
    /// is pre-filled with the model's best guess, so the common path is
    /// "Enter, Enter, Enter".
    /// </summary>
    public int MaxSetupQuestions { get; init; } = 4;
}
