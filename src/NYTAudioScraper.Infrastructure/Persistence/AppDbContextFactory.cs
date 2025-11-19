// <copyright file="AppDbContextFactory.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NYTAudioScraper.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for AppDbContext to support EF Core migrations
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        // Use SQLite for design-time operations (same as runtime)
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NYTAudioScraper",
            "nytaudioscraper.db");

        optionsBuilder.UseSqlite($"Data Source={dbPath}");

        return new AppDbContext(optionsBuilder.Options);
    }
}
