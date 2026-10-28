using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Users;

namespace ValkyrieID.Data.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.UserName).HasMaxLength(UserNames.MaxUserNameLength);
        builder.Property(e => e.NormalizedUserName).HasMaxLength(UserNames.MaxUserNameLength);
        builder.HasIndex(e => e.NormalizedUserName).IsUnique();
        builder.Property(e => e.Email).HasMaxLength(UserNames.MaxEmailLength);
        builder.Property(e => e.NormalizedEmail).HasMaxLength(UserNames.MaxEmailLength);
        builder.HasIndex(e => e.NormalizedEmail).IsUnique();
        builder.Property(e => e.DisplayName).HasMaxLength(200);
        builder.Property(e => e.GivenName).HasMaxLength(100);
        builder.Property(e => e.FamilyName).HasMaxLength(100);
        builder.Property(e => e.PasswordHash).HasMaxLength(200);
        builder.Property(e => e.SecurityStamp).HasMaxLength(64);
        builder.HasMany(e => e.Roles).WithMany().UsingEntity(
            "UserRoles",
            r => r.HasOne(typeof(Role)).WithMany().HasForeignKey("RoleId").OnDelete(DeleteBehavior.Cascade),
            l => l.HasOne(typeof(User)).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade));
        builder.HasMany(e => e.Groups).WithMany().UsingEntity(
            "UserGroups",
            r => r.HasOne(typeof(Group)).WithMany().HasForeignKey("GroupId").OnDelete(DeleteBehavior.Cascade),
            l => l.HasOne(typeof(User)).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade));
        builder.HasMany(e => e.Claims).WithOne().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Name).HasMaxLength(100);
        builder.HasIndex(e => e.Name).IsUnique();
        builder.Property(e => e.Description).HasMaxLength(200);
        builder.HasData(new Role { Id = Role.AdminRoleId, Name = Role.Admin, Description = "Administrators of ValkyrieID." });
    }
}

internal sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> builder)
    {
        builder.ToTable("Groups");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Name).HasMaxLength(100);
        builder.HasIndex(e => e.Name).IsUnique();
        builder.Property(e => e.Description).HasMaxLength(200);
    }
}

internal sealed class UserClaimConfiguration : IEntityTypeConfiguration<UserClaim>
{
    public void Configure(EntityTypeBuilder<UserClaim> builder)
    {
        builder.ToTable("UserClaims");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Type).HasMaxLength(100);
        builder.Property(e => e.Value).HasMaxLength(1000);
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("UserSessions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(64).ValueGeneratedNever();
        builder.Property(e => e.IpAddress).HasMaxLength(45);
        builder.Property(e => e.UserAgent).HasMaxLength(500);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => e.UserId);
    }
}

internal sealed class UserSessionClientConfiguration : IEntityTypeConfiguration<UserSessionClient>
{
    public void Configure(EntityTypeBuilder<UserSessionClient> builder)
    {
        builder.ToTable("UserSessionClients");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.SessionId).HasMaxLength(64);
        builder.Property(e => e.ClientId).HasMaxLength(200);
        builder.HasOne<UserSession>().WithMany().HasForeignKey(e => e.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Client>().WithMany().HasForeignKey(e => e.ClientId).HasPrincipalKey(c => c.ClientId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => new { e.SessionId, e.ClientId });
    }
}

internal sealed class BackChannelLogoutDeliveryConfiguration : IEntityTypeConfiguration<BackChannelLogoutDelivery>
{
    public void Configure(EntityTypeBuilder<BackChannelLogoutDelivery> builder)
    {
        builder.ToTable("BackChannelLogoutDeliveries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.ClientId).HasMaxLength(200);
        builder.Property(e => e.SessionId).HasMaxLength(64);
        // A concurrency token, so an instance whose lease expired and was taken over can't overwrite the new owner.
        builder.Property(e => e.LeaseOwner).HasMaxLength(64).IsConcurrencyToken();
        builder.HasIndex(e => e.NextAttemptAt);
        builder.HasOne<Client>().WithMany().HasForeignKey(e => e.ClientId).HasPrincipalKey(c => c.ClientId).OnDelete(DeleteBehavior.Cascade);
    }
}
