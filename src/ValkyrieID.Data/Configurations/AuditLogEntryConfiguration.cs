using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ValkyrieID.Core.Auditing;

namespace ValkyrieID.Data.Configurations;

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("AuditLog");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.EventType).HasMaxLength(100);
        builder.Property(e => e.Actor).HasMaxLength(200);
        builder.Property(e => e.ClientId).HasMaxLength(200);
        builder.Property(e => e.IpAddress).HasMaxLength(45);
        builder.HasIndex(e => e.Timestamp);
        builder.HasIndex(e => e.SubjectId);
    }
}
