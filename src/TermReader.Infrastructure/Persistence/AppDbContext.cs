// Educational and personal use only.

using Microsoft.EntityFrameworkCore;
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
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply entity configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
