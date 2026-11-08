using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Protocol;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Administration;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Email;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Users;

internal sealed record NewUser(
    string UserName,
    string? Email,
    string? DisplayName,
    string Password,
    bool Admin = false,
    string? GivenName = null,
    string? FamilyName = null);

/// <summary>A user's profile as set by an administrator.</summary>
internal sealed record UserUpdate(
    string UserName,
    string? Email,
    bool EmailVerified,
    string? DisplayName,
    string? GivenName,
    string? FamilyName);

/// <summary>Who makes a change, for the audit log.</summary>
internal sealed record AuditActor(string Name, string? IpAddress = null);

/// <summary>User management shared by the setup wizard, the CLI, and the Admin API.</summary>
internal sealed class UserAdministration(
    ValkyrieDbContext db,
    PasswordHasher hasher,
    PasswordValidator passwordValidator,
    RefreshTokenStore refreshTokens,
    SessionService sessions,
    AccountEmails emails,
    AuditRecorder audit,
    TimeProvider timeProvider)
{
    public const int MaxDisplayNameLength = 200;
    public const int MaxNameLength = 100;
    public const int MaxClaimTypeLength = 100;
    public const int MaxClaimValueLength = 1000;
    public const int MaxClaimsPerUser = 100;

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
        catch (Exception ex) when (IsSetupConflict(ex))
        {
            // Another request completed setup first (serialization failure or unique index violation). Npgsql's
            // execution strategy wraps the serialization failure (40001) in an InvalidOperationException.
            db.ChangeTracker.Clear();
            return null;
        }
    }

    private static bool IsSetupConflict(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is DbUpdateException or DbException)
            {
                return true;
            }
        }

        return false;
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
        emails.PasswordChanged(user, PasswordChange.Admin);
        await db.SaveChangesAsync(cancellationToken);
        sessions.WakeDeliveryWorker();
        emails.Wake();
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

    /// <summary>
    /// Turns off 2FA and deletes the TOTP secret and recovery codes, e.g. after a lost device. The user's sessions
    /// are signed out; if 2FA is required, they enroll again at their next sign-in.
    /// </summary>
    public async Task ResetTwoFactorAsync(User user, AuditActor actor, CancellationToken cancellationToken)
    {
        user.TwoFactorEnabled = false;
        user.TotpSecret = null;
        user.TotpLastUsedStep = null;
        Touch(user, timeProvider.GetUtcNow());
        db.RecoveryCodes.RemoveRange(await db.RecoveryCodes.Where(c => c.UserId == user.Id).ToListAsync(cancellationToken));
        await RevokeSessionsAsync(user, actor, "2fa_reset", cancellationToken);
        AddAudit("user.2fa.reset", user, actor);
        await db.SaveChangesAsync(cancellationToken);
        sessions.WakeDeliveryWorker();
    }

    /// <summary>Requires 2FA for this user regardless of the global setting; it applies from their next sign-in.</summary>
    public async Task SetTwoFactorRequiredAsync(User user, bool required, AuditActor actor, CancellationToken cancellationToken)
    {
        if (user.TwoFactorRequired == required)
        {
            return;
        }

        user.TwoFactorRequired = required;
        user.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit(required ? "user.2fa.required" : "user.2fa.not_required", user, actor);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task SetAdminAsync(User user, bool admin, AuditActor actor, CancellationToken cancellationToken) =>
        SetRoleAsync(user, Role.AdminRoleId, admin, actor, cancellationToken);

    /// <summary>Loads a user with roles, groups, and claims.</summary>
    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.Include(u => u.Roles).Include(u => u.Groups).Include(u => u.Claims).AsSplitQuery()
            .SingleOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <summary>
    /// Updates the username, email address, and names. A changed email address is unverified, pending email links
    /// (e.g. password resets sent to the old address) stop working, and the old address is told about the change.
    /// </summary>
    public async Task UpdateAsync(User user, UserUpdate update, AuditActor actor, CancellationToken cancellationToken)
    {
        List<string> errors = [];
        var userName = update.UserName.Trim();
        var email = Clean(update.Email);
        var displayName = Clean(update.DisplayName);
        var givenName = Clean(update.GivenName);
        var familyName = Clean(update.FamilyName);

        var userNameChanged = userName != user.UserName;
        if (userNameChanged)
        {
            if (!UserNames.IsValidUserName(userName))
            {
                errors.Add($"The username must be 1-{UserNames.MaxUserNameLength} letters, digits, '.', '_' or '-'.");
            }
            else if (await db.Users.AnyAsync(u => u.NormalizedUserName == UserNames.Normalize(userName) && u.Id != user.Id, cancellationToken))
            {
                errors.Add("The username is already taken.");
            }
        }

        var emailChanged = email != user.Email;
        if (emailChanged && email is not null)
        {
            if (!UserNames.IsValidEmail(email))
            {
                errors.Add("The email address is invalid.");
            }
            else if (await db.Users.AnyAsync(u => u.NormalizedEmail == UserNames.Normalize(email) && u.Id != user.Id, cancellationToken))
            {
                errors.Add("The email address is already in use.");
            }
        }

        var addressChanged = (email is null ? null : UserNames.Normalize(email)) != user.NormalizedEmail;
        if (addressChanged && update.EmailVerified)
        {
            errors.Add("A new email address can't be marked as verified in the same change; update it first, then mark it verified.");
        }

        if (email is null && update.EmailVerified)
        {
            errors.Add("There is no email address to mark as verified.");
        }

        ValidateNames(displayName, givenName, familyName, errors);
        if (errors.Count > 0)
        {
            throw new AdministrationException(errors);
        }

        List<string> changed = [];
        if (userNameChanged)
        {
            changed.Add("user_name");
        }

        if (emailChanged)
        {
            changed.Add("email");
        }

        if (update.EmailVerified != user.EmailVerified)
        {
            changed.Add("email_verified");
        }

        if (displayName != user.DisplayName)
        {
            changed.Add("display_name");
        }

        if (givenName != user.GivenName)
        {
            changed.Add("given_name");
        }

        if (familyName != user.FamilyName)
        {
            changed.Add("family_name");
        }

        if (changed.Count == 0)
        {
            return;
        }

        var oldEmail = user.Email;
        user.UserName = userName;
        user.NormalizedUserName = UserNames.Normalize(userName);
        user.Email = email;
        user.NormalizedEmail = email is null ? null : UserNames.Normalize(email);
        user.EmailVerified = update.EmailVerified;
        user.DisplayName = displayName;
        user.GivenName = givenName;
        user.FamilyName = familyName;
        user.UpdatedAt = timeProvider.GetUtcNow();
        if (addressChanged)
        {
            await db.EmailTokens.Where(t => t.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
            if (oldEmail is not null && email is not null)
            {
                emails.EmailChanged(user, oldEmail);
            }
        }

        AddAudit("user.updated", user, actor, new Dictionary<string, object?> { ["fields"] = string.Join(' ', changed) });
        await db.SaveChangesAsync(cancellationToken);
        emails.Wake();
    }

    /// <summary>
    /// Deletes a user. Their sessions are revoked first, so clients are notified by back-channel logout; refresh
    /// tokens, sessions, and other data of the user are deleted with them.
    /// </summary>
    public async Task DeleteAsync(User user, AuditActor actor, CancellationToken cancellationToken)
    {
        if (user.Roles.Any(r => r.Id == Role.AdminRoleId))
        {
            await EnsureOtherAdminExistsAsync(user, "deleted", cancellationToken);
        }

        await RevokeSessionsAsync(user, actor, "user_deleted", cancellationToken);
        db.Users.Remove(user);
        AddAudit("user.deleted", user, actor, new Dictionary<string, object?> { ["user_name"] = user.UserName });
        await db.SaveChangesAsync(cancellationToken);
        sessions.WakeDeliveryWorker();
    }

    /// <summary>Adds or removes a role. Role changes take effect immediately: existing sessions must re-authenticate.</summary>
    public async Task SetRoleAsync(User user, Guid roleId, bool assigned, AuditActor actor, CancellationToken cancellationToken)
    {
        var hasRole = user.Roles.Any(r => r.Id == roleId);
        if (hasRole == assigned)
        {
            return;
        }

        Role role;
        if (assigned)
        {
            role = await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken)
                ?? throw AdministrationException.NotFound("The role does not exist.");
            user.Roles.Add(role);
        }
        else
        {
            role = user.Roles.Single(r => r.Id == roleId);
            if (roleId == Role.AdminRoleId)
            {
                await EnsureNotLastAdminAsync(user, cancellationToken);
            }

            user.Roles.Remove(role);
        }

        Touch(user, timeProvider.GetUtcNow());
        AddAudit(assigned ? "user.role_added" : "user.role_removed", user, actor, new Dictionary<string, object?> { ["role"] = role.Name });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetGroupAsync(User user, Guid groupId, bool member, AuditActor actor, CancellationToken cancellationToken)
    {
        var isMember = user.Groups.Any(g => g.Id == groupId);
        if (isMember == member)
        {
            return;
        }

        Group group;
        if (member)
        {
            group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken)
                ?? throw AdministrationException.NotFound("The group does not exist.");
            user.Groups.Add(group);
        }
        else
        {
            group = user.Groups.Single(g => g.Id == groupId);
            user.Groups.Remove(group);
        }

        user.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit(member ? "user.group_added" : "user.group_removed", user, actor, new Dictionary<string, object?> { ["group"] = group.Name });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Adds a custom claim, emitted with the <c>profile</c> scope. Protocol and standard claims are reserved, so a
    /// custom claim can never override them (e.g. <c>sub</c>, <c>aud</c>, or <c>roles</c>).
    /// </summary>
    public async Task<UserClaim> AddClaimAsync(User user, string type, string value, AuditActor actor, CancellationToken cancellationToken)
    {
        List<string> errors = [];
        type = type.Trim();
        if (type.Length is 0 or > MaxClaimTypeLength || !ProtocolSyntax.IsValidScopeToken(type))
        {
            errors.Add($"The claim type must be 1-{MaxClaimTypeLength} printable ASCII characters without spaces, '\"' or '\\'.");
        }
        else if (UserClaims.Reserved.Contains(type))
        {
            errors.Add($"'{type}' is a reserved claim and can't be set as a custom claim.");
        }

        if (value.Length is 0 or > MaxClaimValueLength || value.Any(char.IsControl))
        {
            errors.Add($"The claim value must be 1-{MaxClaimValueLength} characters without control characters.");
        }

        if (user.Claims.Count >= MaxClaimsPerUser)
        {
            errors.Add($"A user can have at most {MaxClaimsPerUser} custom claims.");
        }

        if (user.Claims.Any(c => c.Type == type && c.Value == value))
        {
            throw AdministrationException.Conflict("The user already has this claim.");
        }

        if (errors.Count > 0)
        {
            throw new AdministrationException(errors);
        }

        var claim = new UserClaim { UserId = user.Id, Type = type, Value = value };
        user.Claims.Add(claim);
        db.Add(claim);
        user.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("user.claim_added", user, actor, new Dictionary<string, object?> { ["claim_type"] = type });
        await db.SaveChangesAsync(cancellationToken);
        return claim;
    }

    public async Task RemoveClaimAsync(User user, Guid claimId, AuditActor actor, CancellationToken cancellationToken)
    {
        var claim = user.Claims.SingleOrDefault(c => c.Id == claimId)
            ?? throw AdministrationException.NotFound("The user has no such claim.");
        user.Claims.Remove(claim);
        db.Remove(claim);
        user.UpdatedAt = timeProvider.GetUtcNow();
        AddAudit("user.claim_removed", user, actor, new Dictionary<string, object?> { ["claim_type"] = claim.Type });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The last enabled user with the admin role can't be disabled or lose the role (PROJECT.md §8.4).</summary>
    private async Task EnsureNotLastAdminAsync(User user, CancellationToken cancellationToken)
    {
        if (!user.Enabled || user.Roles.All(r => r.Id != Role.AdminRoleId))
        {
            return;
        }

        await EnsureOtherAdminExistsAsync(user, "disabled or lose the admin role", cancellationToken);
    }

    private async Task EnsureOtherAdminExistsAsync(User user, string action, CancellationToken cancellationToken)
    {
        if (!user.Enabled)
        {
            return;
        }

        var otherAdmins = await db.Users.CountAsync(
            u => u.Id != user.Id && u.Enabled && u.Roles.Any(r => r.Id == Role.AdminRoleId),
            cancellationToken);
        if (otherAdmins == 0)
        {
            throw AdministrationException.Conflict($"This is the last enabled admin; it can't be {action}.");
        }
    }

    private static void ValidateNames(string? displayName, string? givenName, string? familyName, List<string> errors)
    {
        if (displayName?.Length > MaxDisplayNameLength)
        {
            errors.Add($"The display name must be at most {MaxDisplayNameLength} characters.");
        }

        if (givenName?.Length > MaxNameLength)
        {
            errors.Add($"The given name must be at most {MaxNameLength} characters.");
        }

        if (familyName?.Length > MaxNameLength)
        {
            errors.Add($"The family name must be at most {MaxNameLength} characters.");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<User> BuildAsync(NewUser request, CancellationToken cancellationToken)
    {
        List<string> errors = [];
        var userName = request.UserName.Trim();
        var email = Clean(request.Email);

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

        ValidateNames(Clean(request.DisplayName), Clean(request.GivenName), Clean(request.FamilyName), errors);

        errors.AddRange(await passwordValidator.ValidateAsync(request.Password, userName, email, cancellationToken));
        if (errors.Count > 0)
        {
            throw new AdministrationException(errors);
        }

        var now = timeProvider.GetUtcNow();
        var user = new User
        {
            UserName = userName,
            NormalizedUserName = UserNames.Normalize(userName),
            Email = email,
            NormalizedEmail = email is null ? null : UserNames.Normalize(email),
            DisplayName = Clean(request.DisplayName),
            GivenName = Clean(request.GivenName),
            FamilyName = Clean(request.FamilyName),
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
            throw new AdministrationException(errors);
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
