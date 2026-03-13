// Educational and personal use only.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TermReader.Domain.Entities.Credentials;

namespace TermReader.Persistence.Configurations;

/// <summary>
/// EF Core configuration for the SiteCredential entity.
/// </summary>
public class SiteCredentialConfiguration : IEntityTypeConfiguration<SiteCredential>
{
    public void Configure(EntityTypeBuilder<SiteCredential> builder)
    {
        builder.ToTable("SiteCredentials");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        builder.Property(c => c.Domain)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(c => c.CredentialType)
            .IsRequired();

        builder.Property(c => c.EncryptedUsername)
            .IsRequired();

        builder.Property(c => c.EncryptedPassword)
            .IsRequired();

        builder.Property(c => c.UsernameSelector)
            .HasMaxLength(500);

        builder.Property(c => c.PasswordSelector)
            .HasMaxLength(500);

        builder.Property(c => c.SubmitSelector)
            .HasMaxLength(500);

        builder.Property(c => c.LoginUrl)
            .HasMaxLength(2000);

        builder.Property(c => c.LoginStepsJson)
            .HasMaxLength(4000);

        builder.Property(c => c.CreatedAt)
            .IsRequired();

        builder.Property(c => c.UpdatedAt)
            .IsRequired();

        builder.HasIndex(c => c.Domain)
            .IsUnique();
    }
}
