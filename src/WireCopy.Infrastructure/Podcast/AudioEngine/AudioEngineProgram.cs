// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>The audio-engine program to run.</summary>
/// <param name="Executable">Absolute path of the program.</param>
/// <param name="Arguments">Its arguments (empty for a published build).</param>
/// <param name="WorkingDirectory">The folder it runs in.</param>
/// <param name="Display">The path shown to the user, home shortened to <c>~</c>.</param>
public sealed record AudioEngineProgram(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, string Display);
