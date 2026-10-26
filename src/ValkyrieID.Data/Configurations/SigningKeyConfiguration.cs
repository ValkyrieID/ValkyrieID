using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ValkyrieID.Core.Tokens;

namespace ValkyrieID.Data.Configurations;

internal sealed class SigningKeyConfiguration : IEntityTypeConfiguration<SigningKey>
{
    public void Configure(EntityTypeBuilder<SigningKey> builder)
    {
        builder.ToTable("SigningKeys");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(64).ValueGeneratedNever();
        builder.Property(e => e.Algorithm).HasMaxLength(16);
        builder.HasIndex(e => e.ActivatesAt);
    }
}
