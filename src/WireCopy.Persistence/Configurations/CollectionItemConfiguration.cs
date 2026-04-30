// Licensed under the MIT License. See LICENSE in the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WireCopy.Domain.Entities.Collections;

namespace WireCopy.Persistence.Configurations;

/// <summary>
/// EF Core configuration for the CollectionItem entity.
/// </summary>
public class CollectionItemConfiguration : IEntityTypeConfiguration<CollectionItem>
{
    public void Configure(EntityTypeBuilder<CollectionItem> builder)
    {
        builder.ToTable("CollectionItems");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id)
            .ValueGeneratedNever();

        builder.Property(i => i.Url)
            .IsRequired()
            .HasMaxLength(2000);

        builder.Property(i => i.Title)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(i => i.SavedAt)
            .IsRequired();

        builder.Property(i => i.IsRead)
            .IsRequired();

        builder.Property(i => i.SortOrder)
            .IsRequired()
            .HasDefaultValue(0);

        builder.HasIndex(i => new { i.CollectionId, i.SortOrder });
    }
}
