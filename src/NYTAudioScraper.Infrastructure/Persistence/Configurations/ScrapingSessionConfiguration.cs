using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for ScrapingSession entity
/// </summary>
public class ScrapingSessionConfiguration : IEntityTypeConfiguration<ScrapingSession>
{
    public void Configure(EntityTypeBuilder<ScrapingSession> builder)
    {
        builder.ToTable("ScrapingSessions");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(s => s.StartedAt)
            .IsRequired();

        builder.Property(s => s.CompletedAt);

        builder.Property(s => s.OutputFilePath)
            .HasMaxLength(1000);

        builder.Property(s => s.TotalCharactersProcessed)
            .IsRequired();

        builder.Property(s => s.EstimatedCost)
            .HasPrecision(18, 4)
            .IsRequired();

        builder.Property(s => s.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(s => s.ErrorMessage);

        // Configure relationship with Articles
        // Many-to-many: Sessions can have many Articles, Articles can be in many Sessions
        builder.HasMany(s => s.Articles)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "ScrapingSessionArticle",
                j => j.HasOne<Article>().WithMany().HasForeignKey("ArticleId"),
                j => j.HasOne<ScrapingSession>().WithMany().HasForeignKey("SessionId"));

        // Create index on StartedAt for time-based queries
        builder.HasIndex(s => s.StartedAt);

        // Create index on Status for filtering
        builder.HasIndex(s => s.Status);
    }
}
