// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Browser.CommandHandlers;

/// <summary>
/// Typed (Step, Reason, Fix) tuple consumed by the Phase 4 result screen
/// (workspace-n49i). Produced by <see cref="PodcastFailureClassifier.Classify"/>.
/// All fields are user-facing copy — keep them short enough to fit on a
/// single line at terminal width 80.
/// </summary>
internal sealed record PodcastFailureClassification(string Step, string Reason, string Fix);
