// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Typed (Step, Reason, Fix) tuple consumed by the Phase 4 result screen
/// (workspace-n49i). Produced by <see cref="PodcastFailureClassifier.Classify"/>.
/// All fields are user-facing copy — keep them short enough to fit on a
/// single line at terminal width 80.
///
/// <para>
/// <paramref name="RelevantSetupRow"/> (workspace-n0kb): when the failure
/// has an obvious fix in a specific Setup row, this surfaces the row so the
/// error screen can offer an <c>s</c> deep-link straight into that
/// credential editor. Null when the fix isn't tied to a credential row
/// (e.g. FFmpeg install, rate limit, network outage).
/// </para>
/// </summary>
internal sealed record PodcastFailureClassification(
    string Step,
    string Reason,
    string Fix,
    SettingsCommandHandler.SetupRow? RelevantSetupRow = null);
