// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// Synchronous <see cref="IProgress{T}"/> adapter (workspace-74zy follow-up).
///
/// <para>
/// <see cref="System.Progress{T}"/> posts <c>Report</c> callbacks through the
/// captured <see cref="System.Threading.SynchronizationContext"/> (or the
/// thread-pool when there is none) — that's async and offers NO ordering
/// guarantee between two consecutive <c>Report</c> calls. Inside the podcast
/// pipeline we layer one <see cref="IProgress{T}"/> sink onto another (the
/// orchestrator wraps the caller's progress into TTS/Assembly/Publish
/// adapters), and ordering is load-bearing — consumers need to observe the
/// final <c>chunk = N/N</c> event AFTER the in-flight events, not before.
/// </para>
///
/// <para>
/// This adapter just invokes the action on the calling thread, mirroring the
/// semantics callers expect from a layered forwarder. Use this in place of
/// <c>new Progress&lt;T&gt;(action)</c> wherever the orchestrator wires its
/// pipeline together.
/// </para>
/// </summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public SyncProgress(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public void Report(T value) => _handler(value);
}
