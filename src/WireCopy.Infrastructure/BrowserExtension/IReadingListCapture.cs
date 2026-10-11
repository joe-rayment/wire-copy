// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Infrastructure.BrowserExtension;

/// <summary>
/// What the native-messaging host needs from <see cref="ReadingListCapture"/>; a seam so
/// the host's message loop can be tested without a database or HTML extraction.
/// </summary>
internal interface IReadingListCapture
{
    /// <summary>Parses the page, caches its article, and saves (or moves to top) the Reading List row.</summary>
    /// <param name="url">The page's address; must be an absolute http or https URL.</param>
    /// <param name="pageTitle">The browser tab's title.</param>
    /// <param name="html">The page HTML the browser already has.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was saved.</returns>
    /// <exception cref="ArgumentException">The URL is not an absolute http or https address.</exception>
    Task<ReadingListCaptureResult> SaveAsync(string url, string? pageTitle, string? html, CancellationToken cancellationToken);

    /// <summary>How many items the Reading List shows.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current item count.</returns>
    Task<int> ReadingListCountAsync(CancellationToken cancellationToken);
}
