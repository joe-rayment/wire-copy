// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>Where the audio-engine program was looked for and what was found.</summary>
/// <param name="Program">The program to run; null when nothing was found.</param>
/// <param name="LookedIn">Every candidate examined, in order, home shortened to <c>~</c>.</param>
public sealed record AudioEngineDiscovery(AudioEngineProgram? Program, IReadOnlyList<string> LookedIn);
