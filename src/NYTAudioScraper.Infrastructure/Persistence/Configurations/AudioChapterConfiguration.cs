// <copyright file="AudioChapterConfiguration.cs" company="NYT Audio Scraper">
// Educational and personal use only.
// </copyright>

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NYTAudioScraper.Domain.Entities;

namespace NYTAudioScraper.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for AudioChapter entity
/// </summary>
public class AudioChapterConfiguration : IEntityTypeConfiguration<AudioChapter>
{
    public void Configure(EntityTypeBuilder<AudioChapter> builder)
    {
        builder.ToTable("AudioChapters");

        // Composite key: ArticleId + StartTimeMs (chapters are unique per article and start time)
        builder.HasKey(c => new { c.ArticleId, c.StartTimeMs });

        builder.Property(c => c.Title)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(c => c.ArticleId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(c => c.StartTimeMs)
            .IsRequired();

        builder.Property(c => c.DurationMs)
            .IsRequired();

        builder.Property(c => c.AudioFilePath)
            .HasMaxLength(1000);

        // EndTimeMs is a computed property, not stored in database
        builder.Ignore(c => c.EndTimeMs);

        // Create index on ArticleId for faster lookups
        builder.HasIndex(c => c.ArticleId);
    }
}
