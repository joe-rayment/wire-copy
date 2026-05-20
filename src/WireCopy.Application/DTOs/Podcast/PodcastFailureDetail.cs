// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Domain.ValueObjects.Podcast;

namespace WireCopy.Application.DTOs.Podcast;

/// <summary>
/// Typed-failure payload attached to a <see cref="PodcastResult"/> when the
/// pipeline ends in <see cref="PodcastResultClassification.TotalFailure"/>
/// (workspace-3a2k Phase E). Lets the result screen render targeted
/// remediation without having to pattern-match on
/// <see cref="PodcastResult.ErrorMessage"/>.
/// </summary>
/// <param name="Step">Pipeline step at which the failure occurred (e.g. "Publishing", "TTS synthesis").</param>
/// <param name="FailureClass">Typed publisher failure class — drives the remediation copy.</param>
/// <param name="RawMessage">Underlying diagnostic / exception message — surfaced for log-correlation but not the primary user message.</param>
/// <param name="RemediationCopy">Single short paragraph telling the user how to fix this — rendered as the "Fix:" line on the result screen.</param>
public sealed record PodcastFailureDetail(
    string Step,
    FeedPublishFailureClass FailureClass,
    string RawMessage,
    string RemediationCopy);
