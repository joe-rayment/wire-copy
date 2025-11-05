using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NYTAudioScraper.Application.Interfaces;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Caching;

/// <summary>
/// Two-tier article cache: Memory (L1) + Database (L2)
/// </summary>
public class ArticleCache : IArticleCache
{
    private readonly IMemoryCache _memoryCache;
    private readonly IArticleRepository _articleRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ArticleCache> _logger;
    private readonly TimeSpan _defaultExpiration = TimeSpan.FromDays(7);

    // Cache statistics
    private int _hitCount;
    private int _missCount;

    public ArticleCache(
        IMemoryCache memoryCache,
        IArticleRepository articleRepository,
        IUnitOfWork unitOfWork,
        ILogger<ArticleCache> logger)
    {
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        _articleRepository = articleRepository ?? throw new ArgumentNullException(nameof(articleRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Article?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        // Try L1 cache (memory) first
        if (_memoryCache.TryGetValue<Article>(key, out var cachedArticle))
        {
            Interlocked.Increment(ref _hitCount);
            _logger.LogDebug("Article cache HIT (memory): {Key}", key);
            return cachedArticle;
        }

        // Try L2 cache (database)
        var article = await _articleRepository.GetByUrlAsync(key, cancellationToken);
        if (article != null)
        {
            // Promote to L1 cache
            _memoryCache.Set(key, article, _defaultExpiration);
            Interlocked.Increment(ref _hitCount);
            _logger.LogDebug("Article cache HIT (database): {Key}", key);
            return article;
        }

        Interlocked.Increment(ref _missCount);
        _logger.LogDebug("Article cache MISS: {Key}", key);
        return null;
    }

    public async Task SetAsync(string key, Article value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        var exp = expiration ?? _defaultExpiration;

        // Set in L1 cache (memory)
        _memoryCache.Set(key, value, exp);

        // Set in L2 cache (database)
        var existing = await _articleRepository.GetByUrlAsync(key, cancellationToken);
        if (existing == null)
        {
            await _articleRepository.AddAsync(value, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation("Article cached: {Key} (expires in {Expiration})", key, exp);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        // Remove from L1 cache
        _memoryCache.Remove(key);

        // Remove from L2 cache
        var article = await _articleRepository.GetByUrlAsync(key, cancellationToken);
        if (article != null)
        {
            await _articleRepository.DeleteAsync(article, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation("Article removed from cache: {Key}", key);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        // Check L1 cache first
        if (_memoryCache.TryGetValue<Article>(key, out _))
        {
            return true;
        }

        // Check L2 cache
        return await _articleRepository.ExistsByUrlAsync(key, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        // Note: MemoryCache doesn't have a Clear method, so we can only clear by removing individual items
        // or letting them expire naturally. For production, consider using IMemoryCache.Compact()
        _logger.LogWarning("Clear operation requested but MemoryCache doesn't support bulk clear");
        return Task.CompletedTask;
    }

    public async Task<Article?> GetByUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        return await GetAsync(url, cancellationToken);
    }

    public async Task CacheArticleAsync(Article article, CancellationToken cancellationToken = default)
    {
        await SetAsync(article.Url, article, cancellationToken: cancellationToken);
    }

    public Task<CacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var stats = new CacheStatistics
        {
            HitCount = _hitCount,
            MissCount = _missCount,
            // Note: Cannot get accurate count from IMemoryCache without tracking separately
            MemoryCacheCount = 0,
            DatabaseCacheCount = 0 // Would require separate query
        };

        return Task.FromResult(stats);
    }
}
