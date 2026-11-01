using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Email;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Users;

/// <summary>
/// Self-service changes from the account pages. Sensitive changes require the current password on every request,
/// so a stolen session cookie alone can't take over the account (PROJECT.md §6).
/// </summary>
internal sealed class AccountService(
    ValkyrieDbContext db,
    LoginService login,
    PasswordHasher hasher,
    PasswordValidator passwordValidator,
    TwoFactorService twoFactor,
    SessionService sessions,
    RefreshTokenStore refreshTokens,
    AccountEmails emails,
    AuditRecorder audit,
    TimeProvider timeProvider)
{
    public const string IncorrectPassword = "The password is incorrect.";
    public const string InvalidCode = "The code is invalid or has already been used.";
    public const int MaxDisplayNameLength = 200;
    public const int MaxNameLength = 100;

    private const int MaxPasswordLength = 1024;

    public async Task<User?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.Enabled, cancellationToken);

    /// <summary>
    /// Checks the current password. Wrong passwords count towards the lockout, and a locked-out account is refused
    /// even with the right password, so a stolen session can't be used to keep guessing.
    /// </summary>
    public async Task<bool> VerifyPasswordAsync(User user, string? password, AuditActor actor, CancellationToken cancellationToken)
    {
        password ??= string.Empty;
        var result = hasher.Verify(password.Length > MaxPasswordLength ? password[..MaxPasswordLength] : password, user.PasswordHash);
        if (user.IsLockedOut(timeProvider.GetUtcNow()))
        {
            audit.Add("user.login.failed", success: false, ipAddress: actor.IpAddress, subjectId: user.Id,
                details: new Dictionary<string, object?> { ["reason"] = "locked_out" });
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (result == PasswordVerificationResult.Failed)
        {
            await login.RecordFailureAsync(user, actor.IpAddress, "invalid_password", cancellationToken);
            return false;
        }

        return true;
    }

    public async Task<IReadOnlyList<string>> UpdateProfileAsync(
        User user, string? displayName, string? givenName, string? familyName, AuditActor actor, CancellationToken cancellationToken)
    {
        displayName = Clean(displayName);
        givenName = Clean(givenName);
        familyName = Clean(familyName);

        List<string> errors = [];
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

        if (errors.Count > 0)
        {
            return errors;
        }

        List<string> changed = [];
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
            return [];
        }

        user.DisplayName = displayName;
        user.GivenName = givenName;
        user.FamilyName = familyName;
        user.UpdatedAt = timeProvider.GetUtcNow();
        Audit("user.profile_updated", user, actor, new Dictionary<string, object?> { ["fields"] = string.Join(' ', changed) });
        await db.SaveChangesAsync(cancellationToken);
        return [];
    }

    /// <summary>
    /// Changes the password. The user's other sessions are revoked (with back-channel logout), and so are all their
    /// refresh tokens, including those of the current session; the caller re-issues the current session's cookie.
    /// </summary>
    public async Task<IReadOnlyList<string>> ChangePasswordAsync(
        User user, string? currentPassword, string? newPassword, string? confirmPassword, string? currentSessionId, AuditActor actor, CancellationToken cancellationToken)
    {
        if (!await VerifyPasswordAsync(user, currentPassword, actor, cancellationToken))
        {
            return [IncorrectPassword];
        }

        newPassword ??= string.Empty;
        if (newPassword != (confirmPassword ?? string.Empty))
        {
            return ["The new passwords don't match."];
        }

        var errors = await passwordValidator.ValidateAsync(newPassword, user.UserName, user.Email, cancellationToken);
        if (errors.Count > 0)
        {
            return errors;
        }

        if (hasher.Verify(newPassword, user.PasswordHash) != PasswordVerificationResult.Failed)
        {
            return ["The new password must be different from the current one."];
        }

        var now = timeProvider.GetUtcNow();
        user.PasswordHash = hasher.Hash(newPassword);
        user.PasswordChangedAt = now;
        user.UpdatedAt = now;
        user.SecurityStamp = User.NewSecurityStamp();
        await RevokeOthersAsync(user.Id, currentSessionId, actor, "password_changed", cancellationToken);
        var revokedTokens = await refreshTokens.RevokeForUserAsync(user.Id, cancellationToken);
        Audit("user.password_changed", user, actor, new Dictionary<string, object?> { ["refresh_tokens_revoked"] = revokedTokens });
        emails.PasswordChanged(user, PasswordChange.Changed);
        await db.SaveChangesAsync(cancellationToken);
        sessions.WakeDeliveryWorker();
        emails.Wake();
        return [];
    }

    /// <summary>
    /// Turns off 2FA with the password and a current TOTP or recovery code. Refused while 2FA is required. Like a
    /// password change, the user's other sessions are revoked; the caller re-issues the current session's cookie.
    /// </summary>
    public async Task<IReadOnlyList<string>> DisableTwoFactorAsync(
        User user, string? password, string? code, string? currentSessionId, AuditActor actor, CancellationToken cancellationToken)
    {
        if (!user.TwoFactorEnabled)
        {
            return [];
        }

        if (twoFactor.IsRequired(user))
        {
            return ["Two-factor authentication is required for your account and can't be turned off."];
        }

        if (!await VerifyPasswordAsync(user, password, actor, cancellationToken))
        {
            return [IncorrectPassword];
        }

        code = code?.Trim() ?? string.Empty;
        var valid = code.Length == Totp.Digits && code.All(char.IsAsciiDigit)
            ? await twoFactor.VerifyTotpAsync(user, code, cancellationToken)
            : await twoFactor.RedeemRecoveryCodeAsync(user, code, actor.IpAddress, cancellationToken);
        if (!valid)
        {
            await login.RecordFailureAsync(user, actor.IpAddress, "invalid_2fa_code", cancellationToken);
            return [InvalidCode];
        }

        user.TwoFactorEnabled = false;
        user.TotpSecret = null;
        user.TotpLastUsedStep = null;
        user.UpdatedAt = timeProvider.GetUtcNow();
        user.SecurityStamp = User.NewSecurityStamp();
        await db.RecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        await RevokeOthersAsync(user.Id, currentSessionId, actor, "2fa_disabled", cancellationToken);
        Audit("user.2fa.disabled", user, actor);
        await db.SaveChangesAsync(cancellationToken);
        sessions.WakeDeliveryWorker();
        return [];
    }

    /// <summary>Replaces all recovery codes with new ones, after checking the password. Returns <see langword="null"/> if it's wrong.</summary>
    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(User user, string? password, AuditActor actor, CancellationToken cancellationToken)
    {
        if (!user.TwoFactorEnabled || !await VerifyPasswordAsync(user, password, actor, cancellationToken))
        {
            return null;
        }

        var codes = await twoFactor.ReplaceRecoveryCodesAsync(user, cancellationToken);
        user.UpdatedAt = timeProvider.GetUtcNow();
        Audit("user.2fa.recovery_codes_regenerated", user, actor);
        await db.SaveChangesAsync(cancellationToken);
        return codes;
    }

    public async Task<IReadOnlyList<UserSession>> ListActiveSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var active = await db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ToListAsync(cancellationToken);
        return [.. active.Where(s => s.IsActive(now)).OrderByDescending(s => s.LastActivityAt)];
    }

    /// <summary>
    /// Revokes one of the user's own sessions. Returns <see langword="false"/> if there's no such active session
    /// for this user, without revealing whether the ID belongs to someone else.
    /// </summary>
    public async Task<bool> RevokeSessionAsync(Guid userId, string? sessionId, AuditActor actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length > 100)
        {
            return false;
        }

        var session = await db.UserSessions.SingleOrDefaultAsync(
            s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null, cancellationToken);
        if (session is null)
        {
            return false;
        }

        await sessions.RevokeAsync(session, actor, "user_revoked", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        sessions.WakeDeliveryWorker();
        return true;
    }

    public async Task<int> RevokeOtherSessionsAsync(Guid userId, string? currentSessionId, AuditActor actor, CancellationToken cancellationToken)
    {
        var count = await RevokeOthersAsync(userId, currentSessionId, actor, "user_revoked_others", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        sessions.WakeDeliveryWorker();
        return count;
    }

    private async Task<int> RevokeOthersAsync(Guid userId, string? currentSessionId, AuditActor actor, string reason, CancellationToken cancellationToken)
    {
        var others = await db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.Id != currentSessionId)
            .ToListAsync(cancellationToken);
        foreach (var session in others)
        {
            await sessions.RevokeAsync(session, actor, reason, cancellationToken);
        }

        return others.Count;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Audit(string eventType, User user, AuditActor actor, IReadOnlyDictionary<string, object?>? details = null) =>
        audit.Add(eventType, actor: actor.Name, ipAddress: actor.IpAddress, subjectId: user.Id, details: details);
}
