// Educational and personal use only.

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Detects user idle state for controlling background operations like pre-loading.
/// </summary>
public interface IIdleDetector : IDisposable
{
    /// <summary>
    /// Records user activity, resetting the idle timer.
    /// </summary>
    void RecordActivity();

    /// <summary>
    /// Whether the user is currently idle.
    /// </summary>
    bool IsIdle { get; }

    /// <summary>
    /// Blocks until the user becomes idle.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WaitForIdleAsync(CancellationToken cancellationToken = default);
}
