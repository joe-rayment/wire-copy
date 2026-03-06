// Educational and personal use only.

using Microsoft.Extensions.Logging;
using TermReader.Application.DTOs.Browser;
using TermReader.Application.Interfaces.Browser;
using TermReader.Domain.Enums.Browser;

namespace TermReader.Infrastructure.Browser.Cache;

/// <summary>
/// Decorator around IPageLoader that checks the page cache before fetching from the network.
/// On cache miss, delegates to the inner loader and stores the result.
/// </summary>
public class CachingPageLoader : IPageLoader
{
    private readonly IPageLoader _inner;
    private readonly IPageCache _cache;
    private readonly ILogger<CachingPageLoader> _logger;

    public CachingPageLoader(
        IPageLoader inner,
        IPageCache cache,
        ILogger<CachingPageLoader> logger)
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PageLoadResult> LoadAsync(
        PageLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.ForceRefresh)
        {
            var cached = _cache.TryGet(request.Url);
            if (cached != null)
            {
                _logger.LogInformation("Serving from cache: {Url}", request.Url);
                return cached with { FetchMethod = FetchMethod.Cached };
            }
        }
        else
        {
            _logger.LogInformation("Force refresh requested, bypassing cache: {Url}", request.Url);
        }

        var result = await _inner.LoadAsync(request, cancellationToken);

        if (result.Success)
        {
            _cache.Put(request.Url, result);
        }

        return result;
    }

    public Task<string> GetPageSourceAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        return _inner.GetPageSourceAsync(url, cancellationToken);
    }
}
