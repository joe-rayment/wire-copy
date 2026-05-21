// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireCopy.Domain.Entities.Podcast;

namespace WireCopy.Persistence.Configurations;

/// <summary>
/// EF Core configuration for the <see cref="PodcastJob"/> entity. The table
/// stores one row per podcast generation attempt; see the entity for the
/// lifecycle.
/// </summary>
public class PodcastJobConfiguration : IEntityTypeConfiguration<PodcastJob>
{
    public void Configure(EntityTypeBuilder<PodcastJob> builder)
    {
        builder.ToTable("PodcastJobs");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.Id)
            .ValueGeneratedNever();

        builder.Property(j => j.CollectionId)
            .IsRequired();

        builder.Property(j => j.CollectionTitle)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(j => j.Status)
            .IsRequired();

        builder.Property(j => j.Phase)
            .IsRequired();

        builder.Property(j => j.StartedAtUtc)
            .IsRequired();

        builder.Property(j => j.LastProgressAtUtc)
            .IsRequired();

        // Snapshot of the most recent PodcastProgress; not capped because
        // chapter and per-article details can run a few KB.
        builder.Property(j => j.LastProgressJson);

        builder.Property(j => j.TargetLocalPath)
            .HasMaxLength(2000);

        builder.Property(j => j.TargetFeedUrl)
            .HasMaxLength(2000);

        builder.Property(j => j.ErrorClass)
            .HasMaxLength(200);

        builder.Property(j => j.ErrorMessage)
            .HasMaxLength(4000);

        builder.Property(j => j.AcknowledgedAtUtc);

        // Cheap selector for the badge / startup-orphan scan.
        builder.HasIndex(j => j.Status);
    }
}
