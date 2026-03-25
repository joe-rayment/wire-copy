// Educational and personal use only.

namespace TermReader.Application.Interfaces;

/// <summary>
/// Refreshes the HTTP client's cookie container from persistent storage.
/// Called after browser-based login saves new cookies so the HTTP preloader
/// can use them for authenticated requests.
/// </summary>
public interface IHttpCookieRefresher
{
    /// <summary>
    /// Reloads cookies from persistent storage into the HTTP client's CookieContainer.
    /// </summary>
    Task RefreshAsync();
}
