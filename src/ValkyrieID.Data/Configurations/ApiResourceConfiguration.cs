using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ValkyrieID.Core.Resources;

namespace ValkyrieID.Data.Configurations;

internal sealed class ApiResourceConfiguration : IEntityTypeConfiguration<ApiResource>
{
    public void Configure(EntityTypeBuilder<ApiResource> builder)
    {
        builder.ToTable("ApiResources");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Name).HasMaxLength(200);
        builder.HasIndex(e => e.Name).IsUnique();
        builder.Property(e => e.DisplayName).HasMaxLength(200);
        builder.HasMany(e => e.Scopes).WithOne().HasForeignKey(e => e.ApiResourceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Secrets).WithOne().HasForeignKey(e => e.ApiResourceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ApiScopeConfiguration : IEntityTypeConfiguration<ApiScope>
{
    public void Configure(EntityTypeBuilder<ApiScope> builder)
    {
        builder.ToTable("ApiScopes");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Name).HasMaxLength(200);
        builder.HasIndex(e => e.Name).IsUnique();
        builder.Property(e => e.Description).HasMaxLength(500);
    }
}

internal sealed class ApiResourceSecretConfiguration : IEntityTypeConfiguration<ApiResourceSecret>
{
    public void Configure(EntityTypeBuilder<ApiResourceSecret> builder)
    {
        builder.ToTable("ApiResourceSecrets");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Hash).HasMaxLength(32);
        builder.Property(e => e.Description).HasMaxLength(200);
    }
}
