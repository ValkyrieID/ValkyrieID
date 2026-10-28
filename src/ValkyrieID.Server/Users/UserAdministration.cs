using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Users;

/// <summary>A change that was rejected; <see cref="Errors"/> are safe to show to the person making it.</summary>
internal sealed class UserAdministrationException(IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    public UserAdministrationException(string error)
        : this([error])
    {
    }

    public IReadOnlyList<string> Errors { get; } = errors;
}

internal sealed record NewUser(string UserName, string? Email, string? DisplayName, string Password, bool Admin = false);

/// <summary>Who makes a change, for the audit log.</summary>
internal sealed record AuditActor(string Name, string? IpAddress = null);

/// <summary>User management shared by the setup wizard, the CLI, and (later) the Admin API.</summary>
internal sealed class UserAdministration(
    ValkyrieDbContext db,
    PasswordHasher hasher,
    PasswordValidator passwordValidator,
    RefreshTokenStore refreshTokens,
    SessionService sessions,
    AuditRecorder audit,
    TimeProvider timeProvider)
{
    public Task<User?> FindAsync(string identifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > UserNames.MaxEmailLength)
        {
            return Task.FromResult<User?>(null);
        }

        var normalized = UserNames.Normalize(identifier);
        var users = db.Users.Include(u => u.Roles);
        return UserNames.IsEmail(identifier)
            ? users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken)
            : users.SingleOrDefaultAsync(u => u.NormalizedUserName == normalized, cancellationToken);
    }

    public async Task<User> CreateAsync(NewUser request, AuditActor actor, CancellationToken cancellationToken)
    {
        var user = await BuildAsync(request, cancellationToken);
        db.Users.Add(user);
        AddAudit("user.created", user, actor, new Dictionary<string, object?> { ["admin"] = request.Admin });
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    /// <summary>
    /// Creates the first admin user. Returns <see langword="null"/> if any user already exists; a serializable
    /// transaction makes sure concurrent requests can't both succeed.
    /// </summary>
    public async Task<User?> CompleteSetupAsync(NewUser request, AuditActor actor, CancellationToken cancellationToken)
    {
        var user = await BuildAsync(request with { Admin = true }, cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (await db.Users.AnyAsync(cancellationToken))
            {
                return null;
            }

            db.Users.Add(user);
            AddAudit("setup.completed", user, actor);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return user;
        }
        catch (Exception ex) when (ex is DbUpdateException or DbException)
        {
            // Another request completed setup first (serialization failure or unique index violation).
            db.ChangeTracker.Clear();
            return null;
        }
    }

    public Task<bool> AnyUsersAsync(CancellationToken cancellationToken) => db.Users.AnyAsync(cancellationToken);

    public async Task SetPasswordAsync(User user, string password, AuditActor actor, CancellationToken cancellationToken)
    {
        await ValidatePasswordAsync(password, user.UserName, user.Email, cancellationToken);
        var now = timeProvider.GetUtcNow();
        user.PasswordHash = hasher.Hash(password);
        user.PasswordChangedAt = now;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        Touch(user, now);
        await RevokeSessionsAsync(user, actor, "password_reset", cancellationToken);
        AddAudit("user.password_reset", user, actor);
        await db.SaveChangesAsync(cancellationToken);
        sessions.WakeDeliveryWorker();
    }

    public async Task SetEnabledAsync(User user, bool enabled, AuditActor actor, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            await EnsureNotLastAdminAsync(user, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        user.Enabled = enabled;
        Touch(user, now);
        if (!enabled)
        {
            await RevokeSessionsAsync(user, actor, "user_disabled", cancellationToken);
        }

        AddAudit(enabled ? "user.enabled" : "user.disabled", user, actor);
        await db.SaveChangesAsync(cancellationToken);
        sessions.WakeDeliveryWorker();
    }

    public async Task UnlockAsync(User user, AuditActor actor, CancellationToken cancellationToken)
    {
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("user.unlocked", user, actor);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetAdminAsync(User user, bool admin, AuditActor actor, CancellationToken cancellationToken)
    {
        var hasRole = user.Roles.Any(r => r.Id == Role.AdminRoleId);
        if (hasRole == admin)
        {
            return;
        }

        if (admin)
        {
            user.Roles.Add(await db.Roles.SingleAsync(r => r.Id == Role.AdminRoleId, cancellationToken));
        }
        else
        {
            await EnsureNotLastAdminAsync(user, cancellationToken);
            user.Roles.RemoveAll(r => r.Id == Role.AdminRoleId);
        }

        // Role changes take effect immediately: existing sessions must re-authenticate.
        Touch(user, timeProvider.GetUtcNow());
        AddAudit(admin ? "user.role_added" : "user.role_removed", user, actor, new Dictionary<string, object?> { ["role"] = Role.Admin });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The last enabled user with the admin role can't be disabled or lose the role (PROJECT.md §8.4).</summary>
    private async Task EnsureNotLastAdminAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.Enabled || user.Roles.All(r => r.Id != Role.AdminRoleId))
        {
            return;
        }

        var otherAdmins = await db.Users.CountAsync(
            u => u.Id != user.Id && u.Enabled && u.Roles.Any(r => r.Id == Role.AdminRoleId),
            cancellationToken);
        if (otherAdmins == 0)
        {
            throw new UserAdministrationException("This is the last enabled admin; it can't be disabled or lose the admin role.");
        }
    }

    private async Task<User> BuildAsync(NewUser request, CancellationToken cancellationToken)
    {
        List<string> errors = [];
        var userName = request.UserName.Trim();
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();

        if (!UserNames.IsValidUserName(userName))
        {
            errors.Add($"The username must be 1-{UserNames.MaxUserNameLength} letters, digits, '.', '_' or '-'.");
        }
        else if (await db.Users.AnyAsync(u => u.NormalizedUserName == UserNames.Normalize(userName), cancellationToken))
        {
            errors.Add("The username is already taken.");
        }

        if (email is not null)
        {
            if (!UserNames.IsValidEmail(email))
            {
                errors.Add("The email address is invalid.");
            }
            else if (await db.Users.AnyAsync(u => u.NormalizedEmail == UserNames.Normalize(email), cancellationToken))
            {
                errors.Add("The email address is already in use.");
            }
        }

        if (request.DisplayName?.Length > 200)
        {
            errors.Add("The display name must be at most 200 characters.");
        }

        errors.AddRange(await passwordValidator.ValidateAsync(request.Password, userName, email, cancellationToken));
        if (errors.Count > 0)
        {
            throw new UserAdministrationException(errors);
        }

        var now = timeProvider.GetUtcNow();
        var user = new User
        {
            UserName = userName,
            NormalizedUserName = UserNames.Normalize(userName),
            Email = email,
            NormalizedEmail = email is null ? null : UserNames.Normalize(email),
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            PasswordHash = hasher.Hash(request.Password),
            PasswordChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (request.Admin)
        {
            user.Roles.Add(await db.Roles.SingleAsync(r => r.Id == Role.AdminRoleId, cancellationToken));
        }

        return user;
    }

    private async Task ValidatePasswordAsync(string password, string? userName, string? email, CancellationToken cancellationToken)
    {
        var errors = await passwordValidator.ValidateAsync(password, userName, email, cancellationToken);
        if (errors.Count > 0)
        {
            throw new UserAdministrationException(errors);
        }
    }

    /// <summary>Records a change and invalidates existing session cookies.</summary>
    private static void Touch(User user, DateTimeOffset now)
    {
        user.UpdatedAt = now;
        user.SecurityStamp = User.NewSecurityStamp();
    }

    /// <summary>
    /// Revokes the user's sessions, with back-channel logout, and all their refresh tokens (PROJECT.md: refresh
    /// tokens are revoked on password change and user disable).
    /// </summary>
    private async Task RevokeSessionsAsync(User user, AuditActor actor, string reason, CancellationToken cancellationToken)
    {
        await sessions.RevokeForUserAsync(user.Id, actor, reason, cancellationToken);
        await refreshTokens.RevokeForUserAsync(user.Id, cancellationToken);
    }

    private void AddAudit(string eventType, User user, AuditActor actor, IReadOnlyDictionary<string, object?>? details = null) =>
        audit.Add(eventType, actor: actor.Name, ipAddress: actor.IpAddress, subjectId: user.Id, details: details);
}
