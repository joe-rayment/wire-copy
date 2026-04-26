// Licensed under the MIT License. See LICENSE in the repository root.

namespace TermReader.Domain.Enums.Browser;

/// <summary>
/// How a page was fetched: simple HTTP, Playwright browser, or from cache.
/// </summary>
public enum FetchMethod
{
    Http,
    Browser,
    Cached
}
