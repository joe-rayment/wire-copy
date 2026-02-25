// Educational and personal use only.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TermReader.Domain.Entities.Collections;

namespace TermReader.Infrastructure.Persistence.Configurations;

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

        builder.HasIndex(i => new { i.CollectionId, i.SavedAt });
    }
}
