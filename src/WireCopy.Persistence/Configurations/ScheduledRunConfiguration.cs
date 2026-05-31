// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireCopy.Domain.Entities.Scheduling;

namespace WireCopy.Persistence.Configurations;

/// <summary>workspace-frpl.12 (B10) — EF Core configuration for <see cref="ScheduledRun"/>.</summary>
public class ScheduledRunConfiguration : IEntityTypeConfiguration<ScheduledRun>
{
    public void Configure(EntityTypeBuilder<ScheduledRun> builder)
    {
        builder.ToTable("ScheduledRuns");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.RecipeId).IsRequired();
        builder.Property(r => r.RecipeName).IsRequired().HasMaxLength(500);
        builder.Property(r => r.OccurrenceKey).IsRequired().HasMaxLength(64);
        builder.Property(r => r.Status).IsRequired();
        builder.Property(r => r.StartedAtUtc).IsRequired();
        builder.Property(r => r.FinishedAtUtc);
        builder.Property(r => r.ItemCount).IsRequired();
        builder.Property(r => r.TargetLocalPath).HasMaxLength(2000);
        builder.Property(r => r.TargetFeedUrl).HasMaxLength(2000);
        builder.Property(r => r.ErrorClass).HasMaxLength(200);
        builder.Property(r => r.ErrorMessage).HasMaxLength(4000);
        builder.Property(r => r.StepOutcomesJson);
        builder.Property(r => r.AcknowledgedAtUtc);

        // Dedup read (recipe + occurrence) and the startup-orphan / badge scans.
        builder.HasIndex(r => new { r.RecipeId, r.OccurrenceKey });
        builder.HasIndex(r => r.Status);
    }
}
