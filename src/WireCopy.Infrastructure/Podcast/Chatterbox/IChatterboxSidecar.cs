// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast.Chatterbox;

/// <summary>
/// The single owner of the Chatterbox worker child process (protocol v1,
/// JSON lines over stdio — see tools/chatterbox-worker/chatterbox_worker.py).
/// stdout is protocol only; stderr is logs/progress (uv env build, downloads).
/// </summary>
internal interface IChatterboxSidecar : IAsyncDisposable
{
    /// <summary>Gets a value indicating whether the worker process is up and past its ready banner.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Spawns the worker (uv) if not running and waits for the ready banner + a health reply.
    /// <paramref name="setupProgress"/> receives human-readable lines (uv env build stderr,
    /// load progress) for the UI.
    /// </summary>
    Task StartAsync(IProgress<string>? setupProgress, CancellationToken ct);

    /// <summary>
    /// Sends one speak request; the worker auto-loads the model on first speak
    /// (progress lines surface via <paramref name="progress"/>).
    /// </summary>
    Task<ChatterboxSpeakResult> SpeakAsync(ChatterboxSpeakRequest request, IProgress<string>? progress, CancellationToken ct);

    /// <summary>Gracefully shuts the worker down (falls back to kill). Never throws.</summary>
    Task StopAsync();
}
