// Educational and personal use only.

namespace TermReader.Application.Interfaces.Browser;

/// <summary>
/// Application-layer interface for browser session lifecycle management.
/// Provides warmup and disposal without exposing Selenium types.
/// </summary>
public interface IBrowserSessionControl : IDisposable
{
    /// <summary>
    /// Eagerly initializes the browser driver in the background so the first
    /// browser-based page load does not incur the cold-start penalty.
    /// </summary>
    Task WarmUpAsync();
}
