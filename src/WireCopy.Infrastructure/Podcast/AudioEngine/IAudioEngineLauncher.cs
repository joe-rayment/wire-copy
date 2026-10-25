// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast.AudioEngine;

/// <summary>
/// Starts a local audio-engine when nothing answers at its URL (workspace-08e4u). Finds the program
/// (Settings → Audio engine program, <c>AUDIO_ENGINE_HOME</c>, then <c>~/audio-engine</c>), spawns it
/// detached with its output in <c>logs/audio-engine.log</c>, and waits for <c>/healthz</c>.
/// </summary>
public interface IAudioEngineLauncher
{
    /// <summary>Finds the audio-engine program the current settings and environment point at.</summary>
    /// <returns>The program to run (null when none was found) and every place that was examined.</returns>
    AudioEngineDiscovery Discover();

    /// <summary>
    /// Starts one automatic attempt for <paramref name="url"/> without waiting. At most one automatic
    /// attempt runs per URL per session until <see cref="ResetAttempts"/>.
    /// </summary>
    /// <param name="url">The audio-engine base address nothing answered at.</param>
    /// <returns>True when a new attempt began; false when one already ran or is running.</returns>
    bool BeginStart(string url);

    /// <summary>Gets a value indicating whether an attempt for <paramref name="url"/> is still in flight.</summary>
    /// <param name="url">The audio-engine base address.</param>
    /// <returns>True while an attempt is running.</returns>
    bool IsStarting(string url);

    /// <summary>
    /// A user-initiated start: joins the attempt already running for <paramref name="url"/>, otherwise
    /// begins a new one, and waits for its outcome.
    /// </summary>
    /// <param name="url">The audio-engine base address.</param>
    /// <param name="ct">Stops waiting; the attempt itself keeps running.</param>
    /// <returns>Whether audio-engine became ready, with a user-facing message on failure.</returns>
    Task<AudioEngineStartResult> StartAndWaitAsync(string url, CancellationToken ct);

    /// <summary>Forgets finished attempts so the next automatic start tries again (after a settings change).</summary>
    void ResetAttempts();
}
