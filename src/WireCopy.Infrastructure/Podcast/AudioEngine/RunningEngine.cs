// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>A running audio-engine process WireCopy could identify (workspace-wxll0.2).</summary>
/// <param name="ProcessId">The operating-system process id.</param>
/// <param name="StartedUtc">When the process started.</param>
/// <param name="Program">The program its executable belongs to, as discovery describes it.</param>
internal sealed record RunningEngine(int ProcessId, DateTime StartedUtc, AudioEngineProgram Program);
