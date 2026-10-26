using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ValkyrieID.Core.Clients;

namespace ValkyrieID.Data.Configurations;

internal sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    public void Configure(EntityTypeBuilder<Client> builder)
    {
        builder.ToTable("Clients");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.ClientId).HasMaxLength(200);
        builder.HasIndex(e => e.ClientId).IsUnique();
        builder.Property(e => e.DisplayName).HasMaxLength(200);
        builder.HasMany(e => e.Secrets).WithOne().HasForeignKey(e => e.ClientId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ClientSecretConfiguration : IEntityTypeConfiguration<ClientSecret>
{
    public void Configure(EntityTypeBuilder<ClientSecret> builder)
    {
        builder.ToTable("ClientSecrets");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Hash).HasMaxLength(32);
        builder.Property(e => e.Description).HasMaxLength(200);
    }
}
