using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ValkyrieID.Core.Coordination;

namespace ValkyrieID.Data.Configurations;

internal sealed class LeaseConfiguration : IEntityTypeConfiguration<Lease>
{
    public void Configure(EntityTypeBuilder<Lease> builder)
    {
        builder.ToTable("Leases");
        builder.HasKey(e => e.Name);
        builder.Property(e => e.Name).HasMaxLength(100);
        builder.Property(e => e.Owner).HasMaxLength(100);
        builder.HasData(new Lease { Name = Lease.SigningKeys });
    }
}
