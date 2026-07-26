// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WireCopy.Domain.Entities.Bookmarks;
using WireCopy.Domain.Entities.Collections;
using WireCopy.Domain.Entities.Credentials;
using WireCopy.Domain.Entities.Podcast;

namespace WireCopy.Persistence;

/// <summary>
/// Application database context for WireCopy.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Collection> Collections => Set<Collection>();

    public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();

    public DbSet<Bookmark> Bookmarks => Set<Bookmark>();

    public DbSet<SiteCredential> SiteCredentials => Set<SiteCredential>();

    public DbSet<PodcastJob> PodcastJobs => Set<PodcastJob>();

    public DbSet<WireCopy.Domain.Entities.Scheduling.ScheduledRun> ScheduledRuns =>
        Set<WireCopy.Domain.Entities.Scheduling.ScheduledRun>();

    /// <summary>
    /// Ensures the database is created and all migrations are applied.
    /// Handles upgrade from legacy EnsureCreatedAsync-based databases.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
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

        // Handle upgrade from legacy EnsureCreatedAsync databases:
        // If tables exist but no migration history, stamp the initial migration as applied.
        await StampLegacyDatabaseIfNeeded(cancellationToken);

        await Database.MigrateAsync(cancellationToken);
    }

    private async Task StampLegacyDatabaseIfNeeded(CancellationToken cancellationToken)
    {
        // Does a Collections table exist? (indicator of a pre-existing database)
        //
        // Asked of sqlite_master rather than by SELECTing from Collections and catching
        // the failure (workspace-xpr5t). On a first run that SELECT is EXPECTED to throw,
        // and EF logs every failed command at Error with the SQL — so a clean install's
        // very first log lines were an [ERR] and a stack trace, which is what a broken
        // app looks like. sqlite_master always exists, so the question has an answer
        // instead of an exception. Behaviour is otherwise identical: 1 row means the
        // table is there.
        var existingTables = await Database
            .SqlQuery<string>($"SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name = 'Collections'")
            .ToListAsync(cancellationToken);

        if (existingTables.Count == 0)
        {
            return;
        }

        // Database has tables — check if migration history already tracks InitialCreate
        try
        {
            var applied = await Database.GetAppliedMigrationsAsync(cancellationToken);
            if (applied.Any())
            {
                return; // Already using migrations
            }
        }
        catch
        {
            // History table doesn't exist yet — will be created below
        }

        // Legacy database detected: ensure history table exists and stamp InitialCreate
        var historyRepository = this.GetService<IHistoryRepository>();

        // Use IF NOT EXISTS to handle partially-created history tables
        await Database.ExecuteSqlRawAsync(
            historyRepository.GetCreateScript().Replace("CREATE TABLE", "CREATE TABLE IF NOT EXISTS"),
            cancellationToken);

        var insertSql = historyRepository.GetInsertScript(
            new HistoryRow("20260307135947_InitialCreate", "9.0.0"));
        await Database.ExecuteSqlRawAsync(insertSql, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply entity configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
