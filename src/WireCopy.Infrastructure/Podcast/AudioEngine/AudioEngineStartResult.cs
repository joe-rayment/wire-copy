// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>Outcome of one start attempt.</summary>
/// <param name="Ready">True when audio-engine answered <c>/healthz</c>.</param>
/// <param name="Message">The user-facing failure (starts with "audio-engine"); null when ready.</param>
public sealed record AudioEngineStartResult(bool Ready, string? Message);
