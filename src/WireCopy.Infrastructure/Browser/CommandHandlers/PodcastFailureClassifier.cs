// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.DTOs.Podcast;
using WireCopy.Domain.ValueObjects.Podcast;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Maps raw <see cref="PodcastResult.ErrorMessage"/> strings and per-article
/// failure reasons to typed <c>(Step, Reason, Fix)</c> tuples for the result
/// screen (workspace-n49i Phase 4). The result screen renders Shape D
/// (total failure) as:
/// <code>
///   At step:     {Step}
///   Reason:      {Reason}
///   Fix:         {Fix}
/// </code>
/// which is far more actionable than the previous heuristic-bulleted
/// "What to try:" list.
///
/// <para>
/// Mirrors the pattern of <c>GcsCredentialVerifier.RemediationFor</c>:
/// pattern-match against the error string + first article-failure reason,
/// pick the most specific classification, fall back to a generic
/// "see logs" tuple if nothing matches.
/// </para>
/// </summary>
internal static class PodcastFailureClassifier
{
    /// <summary>
    /// Maps a typed <see cref="PodcastFailureDetail"/> (when present) to the
    /// (Step, Reason, Fix) tuple — bypasses the heuristic string fallback so
    /// the orchestrator's typed FailureClass surfaces directly on the result
    /// screen (workspace-3a2k Phase E). Falls back to the legacy heuristic
    /// pipeline when no typed detail is supplied.
    /// </summary>
    internal static PodcastFailureClassification Classify(
        PodcastFailureDetail? typedDetail,
        string? errorMessage,
        IReadOnlyList<ArticleFailure> failedArticles)
    {
        if (typedDetail is not null)
        {
            var reason = string.IsNullOrWhiteSpace(typedDetail.RawMessage)
                ? typedDetail.FailureClass.ToString()
                : typedDetail.RawMessage;

            // workspace-n0kb: any non-None publish failure points the user
            // at the GcsBucket row — that's where the bucket name, public
            // ACL, and (via the inline gate) the service-account key are
            // edited. NoAudioFiles is a generation-side missing local file
            // rather than a credential bug, so it gets no setup row.
            SettingsCommandHandler.SetupRow? relevantRow = typedDetail.FailureClass switch
            {
                FeedPublishFailureClass.BucketNotPublic
                    or FeedPublishFailureClass.FeedNotReachable
                    or FeedPublishFailureClass.FeedNotParseable
                    or FeedPublishFailureClass.Generic
                    => SettingsCommandHandler.SetupRow.GcsBucket,
                _ => null,
            };

            return new PodcastFailureClassification(
                Step: typedDetail.Step,
                Reason: reason,
                Fix: typedDetail.RemediationCopy,
                RelevantSetupRow: relevantRow);
        }

        return Classify(errorMessage, failedArticles);
    }

    /// <summary>
    /// Maps a raw failure into a typed classification. Always returns
    /// non-null fields; ambiguous failures get the <c>Unknown</c> generic
    /// tuple.
    /// </summary>
    internal static PodcastFailureClassification Classify(
        string? errorMessage,
        IReadOnlyList<ArticleFailure> failedArticles)
    {
        var error = errorMessage ?? string.Empty;
        var firstFailureReason = failedArticles.Count > 0 ? failedArticles[0].Reason : string.Empty;

        // ---- Local narration engine (Chatterbox) — keyed on the sentinel prefix
        // ChatterboxTtsService stamps on every failure (workspace-2xej.10). Must
        // come FIRST so a local failure never advises checking platform.openai.com. ----
        if (error.StartsWith("Local narration: ", StringComparison.Ordinal))
        {
            var detail = error["Local narration: ".Length..];
            return new PodcastFailureClassification(
                Step: "Local narration (Chatterbox)",
                Reason: string.IsNullOrWhiteSpace(detail) ? "The local voice engine failed" : detail,
                Fix: "Run the Test in Settings (c → Local engine) to see details; if it's the first run the model download may have been interrupted — just retry. uv installed? → curl -LsSf https://astral.sh/uv/install.sh | sh",
                RelevantSetupRow: SettingsCommandHandler.SetupRow.ChatterboxStatus);
        }

        // ---- Pre-flight failures (FFmpeg, missing API key) ----
        if (error.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("ffmpeg", StringComparison.Ordinal))
        {
            return new PodcastFailureClassification(
                Step: "Pre-flight (audio assembly)",
                Reason: "FFmpeg is not installed or not on PATH",
                Fix: "Install ffmpeg: brew install ffmpeg (macOS) or apt install ffmpeg (Linux)");
        }

        // Engine-neutral orchestrator preflight copy (workspace-2xej.10) — the
        // active engine is unknown here, so point at the engine picker.
        if (error.Contains("Narration isn't ready", StringComparison.OrdinalIgnoreCase))
        {
            return new PodcastFailureClassification(
                Step: "Pre-flight (narration)",
                Reason: "The narration engine isn't ready yet",
                Fix: "Open Settings (c) → Narration engine to finish setup, then press p again",
                RelevantSetupRow: SettingsCommandHandler.SetupRow.NarrationEngine);
        }

        if (error.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("not configured", StringComparison.OrdinalIgnoreCase))
        {
            return new PodcastFailureClassification(
                Step: "Pre-flight (credentials)",
                Reason: "OpenAI API key missing or not yet configured",
                Fix: "Open Setup (:config), paste your key, then press p again",
                RelevantSetupRow: SettingsCommandHandler.SetupRow.OpenAiKey);
        }

        // ---- GCS / publish failures (must come before generic 403 — a
        // GCS bucket 403 means "bucket isn't public", not "OpenAI auth"). ----
        if (error.Contains("GCS", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("bucket", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("storage.googleapis", StringComparison.OrdinalIgnoreCase))
        {
            return new PodcastFailureClassification(
                Step: "RSS publish (GCS upload)",
                Reason: "Couldn't upload or publish the feed to your GCS bucket",
                Fix: "Check the bucket name + service-account key in Setup, or run gsutil iam ch allUsers:objectViewer gs://your-bucket",
                RelevantSetupRow: SettingsCommandHandler.SetupRow.GcsBucket);
        }

        // ---- OpenAI API errors (429 / 401 / 403 / 5xx) ----
        if (error.Contains("429", StringComparison.Ordinal) ||
            error.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("rate_limit", StringComparison.OrdinalIgnoreCase))
        {
            return new PodcastFailureClassification(
                Step: "TTS synthesis",
                Reason: "OpenAI API returned 429 — rate limit or quota exceeded",
                Fix: "Wait ~60s and retry, or check usage at platform.openai.com/usage");
        }

        if (error.Contains("401", StringComparison.Ordinal) ||
            error.Contains("invalid_key", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return new PodcastFailureClassification(
                Step: "TTS synthesis (authentication)",
                Reason: "OpenAI rejected the API key (401)",
                Fix: "Verify the key at platform.openai.com/api-keys and re-paste in Setup",
                RelevantSetupRow: SettingsCommandHandler.SetupRow.OpenAiKey);
        }

        if (error.Contains("403", StringComparison.Ordinal) ||
            error.Contains("insufficient_credits", StringComparison.OrdinalIgnoreCase))
        {
            return new PodcastFailureClassification(
                Step: "TTS synthesis (authorization)",
                Reason: "OpenAI returned 403 — account lacks credits or permission",
                Fix: "Add credits at platform.openai.com/billing, then retry",
                RelevantSetupRow: SettingsCommandHandler.SetupRow.OpenAiKey);
        }

        if (error.Contains("500", StringComparison.Ordinal) ||
            error.Contains("502", StringComparison.Ordinal) ||
            error.Contains("503", StringComparison.Ordinal) ||
            error.Contains("504", StringComparison.Ordinal))
        {
            return new PodcastFailureClassification(
                Step: "TTS synthesis",
                Reason: "OpenAI API returned a 5xx error (transient server-side problem)",
                Fix: "Retry in a minute — usually clears on its own");
        }

        // ---- Cost / budget gating ----
        if (error.Contains("budget", StringComparison.OrdinalIgnoreCase) ||
            (error.Contains("cost", StringComparison.OrdinalIgnoreCase) && !error.Contains("Costly", StringComparison.OrdinalIgnoreCase)))
        {
            return new PodcastFailureClassification(
                Step: "Cost gate",
                Reason: "Estimated cost exceeded the configured budget",
                Fix: "Remove some articles, or raise PodcastCostGateThresholdUsd in Setup");
        }

        // ---- Network / DNS ----
        if (error.Contains("network", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("dns", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return new PodcastFailureClassification(
                Step: "Network",
                Reason: "Couldn't reach a remote endpoint (DNS, timeout, or network down)",
                Fix: "Check your internet connection and retry");
        }

        // ---- Content extraction (every article failed) ----
        if (error.Contains("No readable articles", StringComparison.OrdinalIgnoreCase) ||
            firstFailureReason.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
            firstFailureReason.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
            firstFailureReason.Contains("captcha", StringComparison.OrdinalIgnoreCase))
        {
            return new PodcastFailureClassification(
                Step: "Content extraction",
                Reason: "Couldn't extract readable content from the selected articles",
                Fix: "Open the articles in the browser first to populate the cache, then retry");
        }

        // ---- Generic fallback ----
        return new PodcastFailureClassification(
            Step: "Unknown",
            Reason: string.IsNullOrWhiteSpace(error) ? "Generation failed without a specific error message" : error,
            Fix: "Check the application logs at ~/.local/share/WireCopy/logs/ for details");
    }
}
