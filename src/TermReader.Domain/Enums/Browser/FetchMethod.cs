// Educational and personal use only.

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
