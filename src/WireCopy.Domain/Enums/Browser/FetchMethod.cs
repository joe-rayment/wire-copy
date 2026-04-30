// Licensed under the MIT License. See LICENSE in the repository root.

namespace WireCopy.Domain.Enums.Browser;

/// <summary>
/// How a page was fetched: simple HTTP, Playwright browser, or from cache.
/// </summary>
public enum FetchMethod
{
    Http,
    Browser,
    Cached
}
