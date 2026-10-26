using System.Security.Cryptography;

namespace ValkyrieID.Core.Users;

public sealed class User
{
    /// <summary>The immutable subject identifier (<c>sub</c>). Never the username or email.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string UserName { get; set; }

    /// <summary>Lookup key for <see cref="UserName"/>; see <see cref="UserNames.Normalize"/>.</summary>
    public required string NormalizedUserName { get; set; }

    public string? Email { get; set; }

    public string? NormalizedEmail { get; set; }

    public bool EmailVerified { get; set; }

    public string? DisplayName { get; set; }

    public string? GivenName { get; set; }

    public string? FamilyName { get; set; }

    /// <summary>Argon2id hash in PHC string format; <see langword="null"/> if the user can't log in with a password.</summary>
    public string? PasswordHash { get; set; }

    public DateTimeOffset? PasswordChangedAt { get; set; }

    /// <summary>Random value that changes when credentials or account state change; invalidates existing session cookies.</summary>
    public string SecurityStamp { get; set; } = NewSecurityStamp();

    public bool Enabled { get; set; } = true;

    /// <summary>Consecutive failed logins since the last success or lockout.</summary>
    public int AccessFailedCount { get; set; }

    public DateTimeOffset? LockoutEnd { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<Role> Roles { get; init; } = [];

    public List<Group> Groups { get; init; } = [];

    public List<UserClaim> Claims { get; init; } = [];

    public bool IsLockedOut(DateTimeOffset now) => LockoutEnd is { } end && now < end;

    public static string NewSecurityStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}

public sealed class Role
{
    /// <summary>The built-in role that grants access to administration.</summary>
    public const string Admin = "admin";

    public static readonly Guid AdminRoleId = new("0199e0a0-0000-7000-8000-000000000001");

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public string? Description { get; set; }
}

public sealed class Group
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public string? Description { get; set; }
}

/// <summary>A custom key/value claim emitted for the user.</summary>
public sealed class UserClaim
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid UserId { get; init; }

    public required string Type { get; set; }

    public required string Value { get; set; }
}
