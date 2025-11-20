// <copyright file="AppDbContext.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

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

    /// <summary>
    /// Ensures the database is created and migrations are applied
    /// </summary>
    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        // Create database directory if it doesn't exist
        var connectionString = Database.GetConnectionString();
        if (!string.IsNullOrEmpty(connectionString) && connectionString.Contains("Data Source="))
        {
            var dbPath = connectionString.Split("Data Source=")[1].Split(';')[0];
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        // Apply migrations
        await Database.MigrateAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations
        modelBuilder.ApplyConfiguration(new ArticleConfiguration());
        modelBuilder.ApplyConfiguration(new ScrapingSessionConfiguration());
        modelBuilder.ApplyConfiguration(new AudioChapterConfiguration());
    }
}
