// Educational and personal use only.

namespace TermReader.Domain.Enums.Browser;

/// <summary>
/// How a page was fetched: simple HTTP, Selenium browser, or from cache.
/// </summary>
public enum FetchMethod
{
    Http,
    Selenium,
    Cached
}
