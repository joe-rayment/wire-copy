using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Browser;

/// <summary>
/// Stub implementation of IScraperService
/// </summary>
public class ScraperService : IScraperService
{
    private readonly ILogger<ScraperService> _logger;

    public ScraperService(ILogger<ScraperService> logger)
    {
        _logger = logger;
    }

    public Task<bool> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("AuthenticateAsync called (stub implementation)");
        return Task.FromResult(true);
    }

    public Task<IEnumerable<Article>> ScrapeArticlesAsync(
        int maxArticles = 10,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("ScrapeArticlesAsync called with maxArticles={MaxArticles} (stub implementation)", maxArticles);
        return Task.FromResult(Enumerable.Empty<Article>());
    }
}
