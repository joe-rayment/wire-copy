// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
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

        await UseWriteAheadLogAsync(cancellationToken);
    }

    /// <summary>
    /// Puts the database file in write-ahead-log mode (workspace-q06ia.3). The setting lives
    /// in the file, so this is a no-op on every start after the first.
    /// </summary>
    /// <remarks>
    /// EF Core only turns the write-ahead log on when IT creates the file, and here the
    /// legacy-stamp query above always opens the connection first — so every database this
    /// app ever made is in rollback-journal mode. There a save commits when its statement is
    /// reset, needs every reader out of the file at that instant, and is not retried: with
    /// the run view re-reading the run row on the same progress report that makes the job
    /// journal write, saves failed with "database is locked" about once every ten seconds of
    /// narration. In write-ahead-log mode readers never block a commit.
    /// </remarks>
    private async Task UseWriteAheadLogAsync(CancellationToken cancellationToken)
    {
        var logger = this.GetService<ILoggerFactory>().CreateLogger<AppDbContext>();
        try
        {
            await Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var command = Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA journal_mode = 'wal';";
                var mode = (await command.ExecuteScalarAsync(cancellationToken))?.ToString();

                // "memory" is an in-memory database: there is no file to journal.
                if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(mode, "memory", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "The database stayed in '{Mode:l}' journal mode instead of the write-ahead log; saves can fail with 'database is locked' while a routine runs",
                        mode ?? "unknown");
                }
            }
            finally
            {
                await Database.CloseConnectionAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not switch the database to the write-ahead log; saves can fail with 'database is locked' while a routine runs");
        }
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
