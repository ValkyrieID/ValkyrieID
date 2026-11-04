using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Email;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.Users;

/// <summary>
/// The email flows (PROJECT.md §6): password reset, email verification, and email change. They are only available
/// when SMTP is configured. Request responses don't depend on whether the account exists, and emails are queued
/// instead of sent during the request, so neither the response nor its timing reveals it.
/// </summary>
internal sealed class EmailAccountService(
    ValkyrieDbContext db,
    UserAdministration users,
    AccountService account,
    EmailTokenService tokens,
    AccountEmails emails,
    PasswordHasher hasher,
    PasswordValidator passwordValidator,
    SessionService sessions,
    RefreshTokenStore refreshTokens,
    AuditRecorder audit,
    TimeProvider timeProvider,
    IOptionsMonitor<EmailOptions> options)
{
    public const string InvalidLink = "This link is invalid or has expired. Request a new one.";
    public const string EmailInUse = "This email address is already used by another account.";

    public bool Enabled => emails.Enabled;

    /// <summary>Sends a reset link if the account exists, is enabled, and has an email address. The caller always shows the same response.</summary>
    public async Task RequestPasswordResetAsync(string? identifier, string? ipAddress, CancellationToken cancellationToken)
    {
        identifier = identifier?.Trim() ?? string.Empty;
        if (!Enabled || identifier.Length == 0)
        {
            return;
        }

        var user = await users.FindAsync(identifier, cancellationToken);
        var reason = user switch
        {
            null => "unknown_user",
            { Enabled: false } => "disabled",
            { Email: null } => "no_email",
            _ when await tokens.IssuedRecentlyAsync(user.Id, EmailTokenPurpose.ResetPassword, options.CurrentValue.ResendInterval, cancellationToken) => "throttled",
            _ => null,
        };

        if (reason is null)
        {
            var lifetime = options.CurrentValue.PasswordResetLifetime;
            var token = await tokens.IssueAsync(user!, EmailTokenPurpose.ResetPassword, user!.Email, lifetime, cancellationToken);
            emails.PasswordReset(user, token, lifetime);
        }

        audit.Add("user.password_reset.requested", success: reason is null, ipAddress: ipAddress, subjectId: user?.Id,
            details: reason is null ? null : new Dictionary<string, object?> { ["reason"] = reason });
        await db.SaveChangesAsync(cancellationToken);
        emails.Wake();
    }

    /// <summary>Checks a reset link without using it, so the page can say early that it's invalid.</summary>
    public async Task<bool> IsValidAsync(string? token, EmailTokenPurpose purpose, CancellationToken cancellationToken) =>
        Enabled && await tokens.FindAsync(token, purpose, cancellationToken) is not null;

    /// <summary>
    /// Sets a new password with a reset link. All the user's sessions are revoked (with back-channel logout) and
    /// all refresh tokens, and an active lockout is cleared: the user has proven control of the mailbox. 2FA still
    /// applies at the next sign-in.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResetPasswordAsync(
        string? token, string? newPassword, string? confirmPassword, string? ipAddress, CancellationToken cancellationToken)
    {
        if (!Enabled || await tokens.FindAsync(token, EmailTokenPurpose.ResetPassword, cancellationToken) is not ({ } row, { } user))
        {
            await AuditInvalidAsync("user.password_reset.failed", ipAddress, cancellationToken);
            return [InvalidLink];
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

        if (!await tokens.ConsumeAsync(row, cancellationToken))
        {
            return [InvalidLink];
        }

        var now = timeProvider.GetUtcNow();
        user.PasswordHash = hasher.Hash(newPassword);
        user.PasswordChangedAt = now;
        user.UpdatedAt = now;
        user.SecurityStamp = User.NewSecurityStamp();
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        var actor = new AuditActor(user.Id.ToString(), ipAddress);
        await sessions.RevokeForUserAsync(user.Id, actor, "password_reset", cancellationToken);
        var revokedTokens = await refreshTokens.RevokeForUserAsync(user.Id, cancellationToken);
        await tokens.DeleteAsync(user.Id, EmailTokenPurpose.ResetPassword, cancellationToken);
        audit.Add("user.password_reset.completed", actor: actor.Name, ipAddress: ipAddress, subjectId: user.Id,
            details: new Dictionary<string, object?> { ["refresh_tokens_revoked"] = revokedTokens });
        emails.PasswordChanged(user, PasswordChange.Reset);
        await db.SaveChangesAsync(cancellationToken);
        sessions.WakeDeliveryWorker();
        emails.Wake();
        return [];
    }

    /// <summary>Sends a verification link to the user's unverified address.</summary>
    public async Task SendVerificationAsync(User user, AuditActor actor, CancellationToken cancellationToken)
    {
        if (!Enabled || user.Email is null || user.EmailVerified
            || await tokens.IssuedRecentlyAsync(user.Id, EmailTokenPurpose.VerifyEmail, options.CurrentValue.ResendInterval, cancellationToken))
        {
            return;
        }

        var lifetime = options.CurrentValue.VerificationLifetime;
        var token = await tokens.IssueAsync(user, EmailTokenPurpose.VerifyEmail, user.Email, lifetime, cancellationToken);
        emails.VerifyEmail(user, token, lifetime);
        audit.Add("user.email_verification.requested", actor: actor.Name, ipAddress: actor.IpAddress, subjectId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
        emails.Wake();
    }

    /// <summary>Marks the address as verified, if the link was sent to the user's current address.</summary>
    public async Task<bool> VerifyEmailAsync(string? token, string? ipAddress, CancellationToken cancellationToken)
    {
        if (!Enabled
            || await tokens.FindAsync(token, EmailTokenPurpose.VerifyEmail, cancellationToken) is not ({ } row, { } user)
            || row.Email is null || user.NormalizedEmail != UserNames.Normalize(row.Email)
            || !await tokens.ConsumeAsync(row, cancellationToken))
        {
            await AuditInvalidAsync("user.email_verification.failed", ipAddress, cancellationToken);
            return false;
        }

        user.EmailVerified = true;
        user.UpdatedAt = timeProvider.GetUtcNow();
        await tokens.DeleteAsync(user.Id, EmailTokenPurpose.VerifyEmail, cancellationToken);
        audit.Add("user.email_verified", actor: user.Id.ToString(), ipAddress: ipAddress, subjectId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Starts an email change, after checking the password. The address only changes once the link sent to the new
    /// address is opened. If another account uses the address, that address gets a notice instead; the response is
    /// the same, so it doesn't reveal which addresses are registered.
    /// </summary>
    public async Task<IReadOnlyList<string>> RequestEmailChangeAsync(
        User user, string? newEmail, string? password, AuditActor actor, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return ["Email changes are not available."];
        }

        if (!await account.VerifyPasswordAsync(user, password, actor, cancellationToken))
        {
            return [AccountService.IncorrectPassword];
        }

        newEmail = newEmail?.Trim() ?? string.Empty;
        if (!UserNames.IsValidEmail(newEmail))
        {
            return ["The email address is invalid."];
        }

        var normalized = UserNames.Normalize(newEmail);
        if (normalized == user.NormalizedEmail)
        {
            return ["This is already your email address."];
        }

        if (await tokens.IssuedRecentlyAsync(user.Id, EmailTokenPurpose.ChangeEmail, options.CurrentValue.ResendInterval, cancellationToken))
        {
            return [];
        }

        // A token is issued either way, so both cases do the same work. One for an address in use can never be
        // confirmed: confirming checks the address again.
        var lifetime = options.CurrentValue.VerificationLifetime;
        var token = await tokens.IssueAsync(user, EmailTokenPurpose.ChangeEmail, newEmail, lifetime, cancellationToken);
        var inUse = await db.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.Id != user.Id, cancellationToken);
        if (inUse)
        {
            emails.EmailInUse(user.Id, newEmail);
        }
        else
        {
            emails.ConfirmEmailChange(user, newEmail, token, lifetime);
        }

        audit.Add("user.email_change.requested", actor: actor.Name, ipAddress: actor.IpAddress, subjectId: user.Id,
            details: new Dictionary<string, object?> { ["address_in_use"] = inUse });
        await db.SaveChangesAsync(cancellationToken);
        emails.Wake();
        return [];
    }

    /// <summary>
    /// Completes an email change. The new address is verified by the link itself. All other pending links are
    /// voided, since password reset links sent to the old address must stop working, and the old address is told.
    /// </summary>
    public async Task<IReadOnlyList<string>> ConfirmEmailChangeAsync(string? token, string? ipAddress, CancellationToken cancellationToken)
    {
        if (!Enabled || await tokens.FindAsync(token, EmailTokenPurpose.ChangeEmail, cancellationToken) is not ({ } row, { } user) || row.Email is null)
        {
            await AuditInvalidAsync("user.email_change.failed", ipAddress, cancellationToken);
            return [InvalidLink];
        }

        var newEmail = row.Email;
        var normalized = UserNames.Normalize(newEmail);
        if (!await tokens.ConsumeAsync(row, cancellationToken))
        {
            return [InvalidLink];
        }

        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.Id != user.Id, cancellationToken))
        {
            return [EmailInUse];
        }

        var oldEmail = user.Email;
        user.Email = newEmail;
        user.NormalizedEmail = normalized;
        user.EmailVerified = true;
        user.UpdatedAt = timeProvider.GetUtcNow();
        await db.EmailTokens.Where(t => t.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        audit.Add("user.email_changed", actor: user.Id.ToString(), ipAddress: ipAddress, subjectId: user.Id);
        if (oldEmail is not null)
        {
            emails.EmailChanged(user, oldEmail);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another account took the address in the meantime (unique index).
            db.ChangeTracker.Clear();
            return [EmailInUse];
        }

        emails.Wake();
        return [];
    }

    private async Task AuditInvalidAsync(string eventType, string? ipAddress, CancellationToken cancellationToken)
    {
        audit.Add(eventType, success: false, ipAddress: ipAddress, details: new Dictionary<string, object?> { ["reason"] = "invalid_token" });
        await db.SaveChangesAsync(cancellationToken);
    }
}
