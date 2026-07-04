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
    /// Legacy shared chat model (workspace-r8on split it into per-role models
    /// below). Kept so existing settings/appsettings still bind; no longer read
    /// by the analyzer or extractor — see <see cref="JudgeModel"/>,
    /// <see cref="VerifyModel"/>, <see cref="ArticleModel"/>.
    /// </summary>
    public string Model { get; init; } = "gpt-5-mini";

    /// <summary>
    /// workspace-r8on: the JUDGE model — the layout calls (propose / infer /
    /// refine / curated / hierarchy / classify), which now emit link INDICES only.
    /// Default <c>gpt-5-nano</c>: vision-capable, ~5× cheaper than gpt-5-mini, and
    /// its tiny integer/index output can't truncate. Overridable per-user via the
    /// <c>LayoutModel</c> settings key (the Setup switch). Points at
    /// <see cref="LayoutEndpoint"/> when that is set (e.g. a local Ollama).
    /// </summary>
    public string JudgeModel { get; init; } = "gpt-5-nano";

    /// <summary>
    /// workspace-r8on: model for the OPTIONAL vision lead-tiebreak
    /// (<c>VerifyLeadWithVisionAsync</c>). Default <c>gpt-5-nano</c> (vision).
    /// Shares <see cref="LayoutEndpoint"/> with the judge. Settings key
    /// <c>VerifyModel</c>.
    /// </summary>
    public string VerifyModel { get; init; } = "gpt-5-nano";

    /// <summary>
    /// workspace-r8on: model for ARTICLE extraction (HTML→JSON, strict schema +
    /// quality-floor self-test). Default <c>gpt-5-nano</c>. Stays on the OpenAI
    /// API — no local path. Settings key <c>ArticleModel</c>.
    /// </summary>
    public string ArticleModel { get; init; } = "gpt-5-nano";

    /// <summary>
    /// workspace-r8on: OPTIONAL OpenAI-compatible base URL for the judge/verify
    /// calls. Empty = the OpenAI API (default). Set to a local Ollama endpoint
    /// (e.g. <c>http://localhost:11434/v1</c>) running a small VLM to run the
    /// judge offline — the ONLY model that must exist locally, since selectors
    /// are derived in code. Opt-in, for capable machines only (a CPU/8 GB box
    /// can't run a VLM reliably). Settings key <c>LayoutEndpoint</c>.
    /// </summary>
    public string LayoutEndpoint { get; init; } = string.Empty;

    /// <summary>
    /// workspace-r8on: API key/token sent when <see cref="LayoutEndpoint"/> is set
    /// (Ollama ignores it but the OpenAI client requires a non-empty credential —
    /// defaults to <c>ollama</c>). Empty with no endpoint = use the shared OpenAI
    /// key. Settings key <c>LayoutApiKey</c>.
    /// </summary>
    public string LayoutApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Gets the reasoning-effort tier passed to the model. Default
    /// <c>minimal</c> keeps TTFT low for the classification task; bump to
    /// <c>low</c> / <c>medium</c> only if accuracy regresses on a target site.
    /// </summary>
    public string ReasoningEffort { get; init; } = "minimal";

    /// <summary>
    /// workspace-q77e: reasoning-effort tier for the ONE-TIME Ctrl+L site setup
    /// (the propose + infer-pattern round trips and free-text adjustments). The
    /// decisive infer-pattern call previously fell back to <see cref="ReasoningEffort"/>
    /// (<c>minimal</c>), which made the saved layout inconsistent run-to-run on
    /// messy aggregators (podcasts/sponsor rails sometimes kept, sometimes
    /// dropped). Setup happens once per site and revisits never call the model,
    /// so a higher default (<c>medium</c>) buys a reliably clean layout for a few
    /// cents, one time. Lower to <c>low</c> to trade some accuracy for latency.
    /// </summary>
    public string SetupReasoningEffort { get; init; } = "medium";

    /// <summary>
    /// Gets the maximum completion tokens. 4096 mirrors the previous Anthropic
    /// budget — generous headroom for the largest curated-section payload we
    /// have observed (~30 stories × 4 fields).
    /// </summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>
    /// workspace-i1lv: output-token cap for the ONE-TIME <c>g l</c> setup calls
    /// (propose / infer-pattern / refine). These run at <see cref="SetupReasoningEffort"/>
    /// (<c>medium</c>) on gpt-5-mini, where <c>max_completion_tokens</c> is shared
    /// by reasoning AND the emitted JSON. The old shared 4096 cap
    /// (<see cref="MaxTokens"/>) let medium reasoning starve the durable-pattern
    /// output to empty on the heavier infer/repair rounds — the "AI setup failed"
    /// bug. 12288 gives reasoning room AND a full sections config; setup runs once
    /// per site and revisits never call the model, so the extra ceiling is cheap.
    /// The analyzer escalates beyond this on a truncation retry.
    /// </summary>
    public int SetupMaxTokens { get; init; } = 12288;

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
