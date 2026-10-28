using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Tokens;
using ValkyrieID.Core.Users;

namespace ValkyrieID.Data.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Hash).HasMaxLength(32);
        builder.HasIndex(e => e.Hash).IsUnique();
        builder.Property(e => e.ClientId).HasMaxLength(200);
        builder.Property(e => e.SessionId).HasMaxLength(64);
        builder.HasIndex(e => e.FamilyId);
        builder.HasIndex(e => e.UserId);
        builder.HasIndex(e => e.ClientId);
        builder.HasIndex(e => e.SessionId);
        builder.HasIndex(e => e.FamilyExpiresAt);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Client>().WithMany().HasForeignKey(e => e.ClientId).HasPrincipalKey(c => c.ClientId).OnDelete(DeleteBehavior.Cascade);
    }
}
