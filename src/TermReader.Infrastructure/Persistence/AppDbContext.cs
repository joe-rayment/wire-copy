// Educational and personal use only.

using Microsoft.EntityFrameworkCore;
using TermReader.Domain.Entities.Bookmarks;
using TermReader.Domain.Entities.Collections;

namespace TermReader.Infrastructure.Persistence;

/// <summary>
/// Application database context for TermReader.
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

    /// <summary>
    /// Ensures the database is created and migrations are applied.
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

        // Ensure database is created (no migrations for now - fresh schema)
        await Database.EnsureCreatedAsync(cancellationToken);

        // EnsureCreatedAsync only creates a DB that doesn't exist — it won't add
        // new tables to an existing database. Create Bookmarks table if missing.
        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS Bookmarks (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                Url TEXT NOT NULL,
                SortOrder INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL
            )
            """,
            cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply entity configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
