// Licensed under the MIT License. See LICENSE in the repository root.

using System.Text.Json;
using WireCopy.Application.DTOs.Scheduling;
using WireCopy.Domain.Entities.Scheduling;
using WireCopy.Domain.Enums.Scheduling;
using WireCopy.Infrastructure.Scheduling;

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// workspace-9undz: pure mapping from a finished <see cref="ScheduledRun"/> to the plain-language
/// "what happened / at which step / how to fix" the run-details screen shows. No I/O.
/// </summary>
internal static class RoutineRunDiagnosis
{
    private static readonly RunFix None = new(RunFixKind.None, string.Empty);
    private static readonly RunFix Again = new(RunFixKind.RunAgain, "Run again");

    /// <summary>Known failure messages and the fixed whole phrase shown for each; first hit wins.</summary>
    private static readonly (string Match, string Phrase)[] KnownShorts =
    {
        ("couldn't find it", "can't find audio-engine to start it"),
        ("wants an API key", "audio-engine wants an API key"),
        ("rejected the API key", "audio-engine rejected the API key"),
        ("Finish setting it up in audio-engine", "audio-engine's narration engine is not ready"),
        ("isn't set up", "audio-engine isn't set up"),
        ("isn't running at", "audio-engine isn't running"),
    };

    internal static IReadOnlyList<RunStepOutcome> Steps(ScheduledRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(run.StepOutcomesJson))
        {
            return Array.Empty<RunStepOutcome>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<RunStepOutcome>>(run.StepOutcomesJson) ?? new List<RunStepOutcome>();
        }
        catch (JsonException)
        {
            return Array.Empty<RunStepOutcome>();
        }
    }

    internal static RunDiagnosis Diagnose(ScheduledRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var attention = ScheduledRunBadge.NeedsAttention(run);
        if (run.Status == ScheduledRunStatus.Completed)
        {
            return Make(string.IsNullOrWhiteSpace(run.TargetFeedUrl) ? "✓ Done — saved on this computer" : "✓ Published", string.Empty, null, null, None, attention);
        }

        if (run.Status == ScheduledRunStatus.Skipped && run.ErrorClass == "NothingToCollect")
        {
            return Make("Skipped — nothing to collect yet", string.Empty, null, null, None, attention);
        }

        if (run.ErrorClass is "NarrationNotReady" or "GenerationFailed")
        {
            var c = PodcastFailureClassifier.Classify(run.ErrorMessage, Array.Empty<WireCopy.Application.DTOs.Podcast.ArticleFailure>());
            var fix = c.RelevantSetupRow is { } row ? new RunFix(RunFixKind.SetupRow, "Fix it: " + RowName(row), row) : Again;
            return Make("✗ Failed", c.Reason, c.Step, TrimRetry(c.Fix), fix, attention);
        }

        if (run.ErrorClass == "NoContentResolved" || run.Status == ScheduledRunStatus.PartialSuccess)
        {
            var steps = Steps(run);
            var bad = run.Status == ScheduledRunStatus.PartialSuccess
                ? steps.FirstOrDefault(s => s.Status != "Resolved")
                : steps.Where(s => s.ItemsContributed == 0).OrderByDescending(s => s.Required).FirstOrDefault();
            if (bad is not null)
            {
                var head = run.Status == ScheduledRunStatus.PartialSuccess ? "⚠ Published, with a problem" : "✗ Failed";
                var host = Host(bad.SourceUrl);
                var section = bad.SectionName;
                switch (bad.Status)
                {
                    case "Blocked":
                        return Make(head, $"{host} asked for a sign-in or showed a paywall, so the routine couldn't read it.", $"Loading {host}", "Open it here and sign in — routine runs reuse that sign-in.", new RunFix(RunFixKind.OpenSite, $"Open {host} to sign in", null, bad.SourceUrl), attention);
                    case "LoadFailed":
                        return Make(head, bad.Diagnostic ?? $"{host} didn't load.", $"Loading {host}", "Usually temporary — run it again.", Again, attention);
                    case "Recovered":
                        return Make(head, $"{section} on {host} was found a different way than when you set it up (the site changed).", null, null, new RunFix(RunFixKind.OpenSite, $"Check sections on {host}", null, bad.SourceUrl), attention);
                    default:
                        return Make(head, bad.Diagnostic ?? $"{section} matched no stories on {host}.", $"Finding {section} on {host}", "Re-pick the section so the routine can find it again.", new RunFix(RunFixKind.OpenSite, $"Re-pick sections on {host}", null, bad.SourceUrl), attention);
                }
            }
        }

        if (run.Status == ScheduledRunStatus.Interrupted)
        {
            return Make("✗ Interrupted", run.ErrorMessage ?? "The run was interrupted.", null, null, Again, attention);
        }

        return Make("✗ Failed", run.ErrorMessage ?? run.ErrorClass ?? "Unknown error", null, "The log below has the detail.", Again, attention);
    }

    internal static string RowName(SettingsCommandHandler.SetupRow row) => row switch
    {
        SettingsCommandHandler.SetupRow.AudioEngine => "audio-engine address",
        SettingsCommandHandler.SetupRow.AudioEngineKey => "audio-engine API key",
        SettingsCommandHandler.SetupRow.AudioEngineProgram => "find audio-engine and start it",
        SettingsCommandHandler.SetupRow.OpenAiKey => "OpenAI key",
        SettingsCommandHandler.SetupRow.GcsBucket => "podcast bucket",
        SettingsCommandHandler.SetupRow.GcsKey => "storage key",
        _ => row.ToString(),
    };

    private static RunDiagnosis Make(string headline, string what, string? step, string? how, RunFix fix, bool attention)
    {
        var oneLine = what.Replace('\r', ' ').Replace('\n', ' ');
        var cut = oneLine.IndexOf(". ", StringComparison.Ordinal);
        var firstSentence = cut >= 0 ? oneLine[..cut] : oneLine.TrimEnd('.');
        var shortText = ShortPhrase(oneLine, firstSentence);
        return new RunDiagnosis(headline, shortText, what, step, how, fix, attention);
    }

    /// <summary>
    /// Builds the one-line summary: a fixed phrase for known failures, else the
    /// first sentence, cut at a word boundary (never mid-word) when over 60 characters.
    /// </summary>
    private static string ShortPhrase(string message, string firstSentence)
    {
        foreach (var (match, phrase) in KnownShorts)
        {
            if (message.Contains(match, StringComparison.OrdinalIgnoreCase))
            {
                return phrase;
            }
        }

        if (firstSentence.Length <= 60)
        {
            return firstSentence;
        }

        var space = firstSentence.LastIndexOf(' ', 59);
        var head = space > 0 ? firstSentence[..space] : firstSentence[..59];
        return head.TrimEnd(',', ':', ';', '(', '—', '-', ' ') + "…";
    }

    private static string TrimRetry(string fix)
    {
        var tail = new[] { ", then press p again", ", then retry", " then press p again", " then retry" }
            .FirstOrDefault(t => fix.EndsWith(t, StringComparison.Ordinal));
        if (tail is not null)
        {
            return fix[..^tail.Length];
        }

        return fix;
    }

    private static string Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : (url ?? "the site");
}
