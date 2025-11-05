using Microsoft.EntityFrameworkCore;
using NYTAudioScraper.Domain.Entities;
using NYTAudioScraper.Infrastructure.Persistence.Configurations;

namespace NYTAudioScraper.Infrastructure.Persistence;

/// <summary>
/// Application database context for NYT Audio Scraper
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Article> Articles => Set<Article>();
    public DbSet<ScrapingSession> ScrapingSessions => Set<ScrapingSession>();
    public DbSet<AudioChapter> AudioChapters => Set<AudioChapter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations
        modelBuilder.ApplyConfiguration(new ArticleConfiguration());
        modelBuilder.ApplyConfiguration(new ScrapingSessionConfiguration());
        modelBuilder.ApplyConfiguration(new AudioChapterConfiguration());
    }

    /// <summary>
    /// Ensures the database is created and migrations are applied
    /// </summary>
    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await Database.MigrateAsync(cancellationToken);
    }
}
