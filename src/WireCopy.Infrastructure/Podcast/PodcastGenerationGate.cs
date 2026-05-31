// Licensed under the MIT License. See LICENSE in the repository root.

using WireCopy.Application.Interfaces.Podcast;

namespace WireCopy.Infrastructure.Podcast;

/// <summary>
/// workspace-frpl.1 (B0) — <see cref="IPodcastGenerationGate"/> backed by a
/// <see cref="SemaphoreSlim"/>(1,1). Registered as a singleton so the manual
/// modal and the scheduler share the one instance. The returned lease is
/// idempotent — disposing twice releases the semaphore exactly once.
/// </summary>
internal sealed class PodcastGenerationGate : IPodcastGenerationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool IsHeld => _semaphore.CurrentCount == 0;

    public bool TryAcquire(out IDisposable? lease)
    {
        if (_semaphore.Wait(0))
        {
            lease = new Lease(_semaphore);
            return true;
        }

        lease = null;
        return false;
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Lease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
