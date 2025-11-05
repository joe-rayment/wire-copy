using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for Article entity
/// </summary>
public class ArticleConfiguration : IEntityTypeConfiguration<Article>
{
    public void Configure(EntityTypeBuilder<Article> builder)
    {
        builder.ToTable("Articles");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(a => a.Title)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(a => a.Url)
            .IsRequired()
            .HasMaxLength(1000);

        builder.Property(a => a.Author)
            .HasMaxLength(200);

        builder.Property(a => a.Section)
            .HasMaxLength(100);

        builder.Property(a => a.Content)
            .IsRequired();

        builder.Property(a => a.PublishedDate)
            .IsRequired();

        builder.Property(a => a.ScrapedDate)
            .IsRequired();

        // Create index on URL for faster lookups
        builder.HasIndex(a => a.Url);

        // Create index on PublishedDate for time-based queries
        builder.HasIndex(a => a.PublishedDate);

        // EstimatedWordCount is a computed property, not stored in database
        builder.Ignore(a => a.EstimatedWordCount);
    }
}
