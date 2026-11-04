using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ValkyrieID.Core.Settings;

namespace ValkyrieID.Data.Configurations;

internal sealed class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        builder.ToTable("Settings");
        builder.HasKey(e => e.Key);
        builder.Property(e => e.Key).HasMaxLength(200);
        builder.Property(e => e.Value).HasMaxLength(2000);
        builder.Property(e => e.UpdatedBy).HasMaxLength(200);
    }
}

internal sealed class SettingsRevisionConfiguration : IEntityTypeConfiguration<SettingsRevision>
{
    public void Configure(EntityTypeBuilder<SettingsRevision> builder)
    {
        builder.ToTable("SettingsRevision");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.HasData(new SettingsRevision { Id = SettingsRevision.SingletonId, Revision = 0 });
    }
}
