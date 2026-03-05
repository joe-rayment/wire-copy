// Educational and personal use only.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TermReader.Domain.Entities.Bookmarks;

namespace TermReader.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for the Bookmark entity.
/// </summary>
public class BookmarkConfiguration : IEntityTypeConfiguration<Bookmark>
{
    public void Configure(EntityTypeBuilder<Bookmark> builder)
    {
        builder.ToTable("Bookmarks");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Id)
            .ValueGeneratedNever();

        builder.Property(b => b.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(b => b.Url)
            .IsRequired()
            .HasMaxLength(2000);

        builder.Property(b => b.SortOrder)
            .IsRequired();

        builder.Property(b => b.CreatedAt)
            .IsRequired();

        builder.HasIndex(b => b.SortOrder);
    }
}
