// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Application.Interfaces;

/// <summary>
/// Detects when a page load on a paywalled domain shows a logged-in user, and
/// automatically imports cookies from the foreground browser session into the
/// HTTP cookie container. Mirrors the manual <c>:cookies import</c> command but
/// fires opportunistically without user intervention.
///
/// <para>
/// The refresher is conservative: it only fires when a page is on a configured
/// paywalled domain, the markup looks logged-in (account link present, no
/// paywall gate, substantial word count), and it has not run for the same
/// domain within a cooldown window. False positives only result in importing
/// anonymous cookies, which is harmless.
/// </para>
/// </summary>
public interface IAutoCookieRefresher
{
    /// <summary>
    /// Inspects a freshly loaded page and, if all gates pass, imports cookies
    /// from the foreground browser session into <c>cookies.json</c> and
    /// refreshes the HTTP cookie container. Always returns quickly; failures
    /// are swallowed and logged.
    /// </summary>
    /// <param name="url">Final URL of the page after redirects.</param>
    /// <param name="html">Raw HTML of the loaded page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if an import was attempted (regardless of outcome), false if any gate caused a skip.</returns>
    Task<bool> MaybeRefreshAsync(string url, string? html, CancellationToken cancellationToken = default);
}
