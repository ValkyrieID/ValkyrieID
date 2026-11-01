using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Authentication;

/// <summary>What a user enrolling in 2FA sees: the secret as a QR code and as text, plus the state to confirm it with.</summary>
internal sealed record TotpEnrollment(string State, string QrCodeDataUri, string Secret);

/// <summary>TOTP enrollment and verification, and recovery codes (PROJECT.md §6).</summary>
internal sealed class TwoFactorService(
    ValkyrieDbContext db,
    SecretProtector protector,
    IDataProtectionProvider dataProtection,
    AuditRecorder audit,
    TimeProvider timeProvider,
    IOptions<TwoFactorOptions> options,
    IOptions<BrandingOptions> branding)
{
    public const string TotpSecretPurpose = "ValkyrieID.TotpSecret";

    /// <summary>How long a QR code stays valid before it has to be confirmed.</summary>
    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(15);

    private IDataProtector EnrollmentProtector => dataProtection.CreateProtector("ValkyrieID.TotpEnrollment");

    public bool IsRequired(User user) => options.Value.Required || user.TwoFactorRequired;

    /// <summary>
    /// Starts enrollment with a new secret. The secret isn't stored until a code confirms it; until then it
    /// travels in <see cref="TotpEnrollment.State"/>, encrypted and bound to the user and an expiry, so reloading
    /// or abandoning the page leaves nothing behind.
    /// </summary>
    public TotpEnrollment BeginEnrollment(User user)
    {
        var secret = Totp.GenerateSecret();
        var expires = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(expires, (timeProvider.GetUtcNow() + EnrollmentLifetime).ToUnixTimeSeconds());
        byte[] payload = [.. user.Id.ToByteArray(), .. expires, .. secret];
        var state = Base64Url.EncodeToString(EnrollmentProtector.Protect(payload));
        CryptographicOperations.ZeroMemory(payload);
        return View(user, state, secret);
    }

    /// <summary>The enrollment for a previously issued state, or <see langword="null"/> if it's invalid or expired.</summary>
    public TotpEnrollment? ContinueEnrollment(User user, string? state) =>
        ReadState(user, state) is { } secret ? View(user, state!, secret) : null;

    /// <summary>
    /// Turns on 2FA if <paramref name="code"/> is valid for the secret in <paramref name="state"/>. Returns the
    /// new recovery codes, or <see langword="null"/> if the state or code is invalid.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ConfirmEnrollmentAsync(
        User user, string? state, string? code, AuditActor actor, CancellationToken cancellationToken)
    {
        if (user.TwoFactorEnabled || ReadState(user, state) is not { } secret)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (Totp.Verify(secret, code ?? string.Empty, now, lastUsedStep: null) is not { } step)
        {
            return null;
        }

        user.TotpSecret = protector.Protect(secret, TotpSecretPurpose);
        user.TotpLastUsedStep = step;
        user.TwoFactorEnabled = true;
        user.UpdatedAt = now;
        CryptographicOperations.ZeroMemory(secret);
        var codes = await ReplaceRecoveryCodesAsync(user, now, cancellationToken);
        audit.Add("user.2fa.enabled", actor: actor.Name, ipAddress: actor.IpAddress, subjectId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
        return codes;
    }

    /// <summary>Checks a TOTP code and marks its time step as used, so the same code can't be used again.</summary>
    public async Task<bool> VerifyTotpAsync(User user, string? code, CancellationToken cancellationToken)
    {
        if (!user.TwoFactorEnabled || user.TotpSecret is null)
        {
            return false;
        }

        var secret = protector.Unprotect(user.TotpSecret, TotpSecretPurpose);
        long? step;
        try
        {
            step = Totp.Verify(secret, code ?? string.Empty, timeProvider.GetUtcNow(), user.TotpLastUsedStep);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        if (step is null)
        {
            return false;
        }

        // Conditional update: of two concurrent requests with the same code, only one succeeds (RFC 6238 §5.2).
        var updated = await db.Users
            .Where(u => u.Id == user.Id && (u.TotpLastUsedStep == null || u.TotpLastUsedStep < step))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TotpLastUsedStep, step), cancellationToken);
        return updated == 1;
    }

    /// <summary>Uses up a recovery code; each one works once.</summary>
    public async Task<bool> RedeemRecoveryCodeAsync(User user, string? code, string? ipAddress, CancellationToken cancellationToken)
    {
        var hash = RecoveryCodes.Hash(code ?? string.Empty);
        var unused = await db.RecoveryCodes.AsNoTracking()
            .Where(c => c.UserId == user.Id && c.UsedAt == null)
            .Select(c => new { c.Id, c.CodeHash })
            .ToListAsync(cancellationToken);

        Guid? match = null;
        foreach (var candidate in unused)
        {
            if (hash is not null && CryptographicOperations.FixedTimeEquals(hash, candidate.CodeHash))
            {
                match = candidate.Id;
            }
        }

        if (!user.TwoFactorEnabled || match is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var updated = await db.RecoveryCodes
            .Where(c => c.Id == match && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), cancellationToken);
        if (updated != 1)
        {
            return false;
        }

        audit.Add("user.2fa.recovery_code_used", actor: user.Id.ToString(), ipAddress: ipAddress, subjectId: user.Id,
            details: new Dictionary<string, object?> { ["remaining"] = unused.Count - 1 });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<int> CountUnusedRecoveryCodesAsync(User user, CancellationToken cancellationToken) =>
        db.RecoveryCodes.CountAsync(c => c.UserId == user.Id && c.UsedAt == null, cancellationToken);

    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(User user, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.RecoveryCodes.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
        var codes = Enumerable.Range(0, RecoveryCodes.Count).Select(_ => RecoveryCodes.Generate()).ToList();
        db.RecoveryCodes.AddRange(codes.Select(c => new RecoveryCode { UserId = user.Id, CodeHash = RecoveryCodes.Hash(c) ?? throw new InvalidOperationException("Generated an invalid recovery code."), CreatedAt = now }));
        return codes;
    }

    private byte[]? ReadState(User user, string? state)
    {
        if (string.IsNullOrEmpty(state) || state.Length > 1024)
        {
            return null;
        }

        byte[] payload;
        try
        {
            payload = EnrollmentProtector.Unprotect(Base64Url.DecodeFromChars(state));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }

        if (payload.Length != 16 + 8 + Totp.SecretSizeBytes
            || new Guid(payload.AsSpan(0, 16)) != user.Id
            || BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(16, 8)) <= timeProvider.GetUtcNow().ToUnixTimeSeconds())
        {
            return null;
        }

        return payload[24..];
    }

    private TotpEnrollment View(User user, string state, byte[] secret)
    {
        var uri = Totp.KeyUri(secret, branding.Value.ProductName, user.UserName);
        using var data = QRCodeGenerator.GenerateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(5);
        var text = Base32.Encode(secret);
        var grouped = string.Join(' ', Enumerable.Range(0, (text.Length + 3) / 4).Select(i => text.Substring(i * 4, Math.Min(4, text.Length - (i * 4)))));
        return new TotpEnrollment(state, "data:image/png;base64," + Convert.ToBase64String(png), grouped);
    }
}

internal enum PendingStep
{
    /// <summary>The user has 2FA and must enter a code.</summary>
    Verify,

    /// <summary>2FA is required but not set up; the user must enroll first.</summary>
    Enroll,
}

/// <summary>
/// The state between a verified password and the second factor: a short-lived encrypted cookie, separate from
/// the session cookie and granting no access by itself. The session is only created once all factors pass.
/// </summary>
internal sealed class PendingLogin(ValkyrieDbContext db, TimeProvider timeProvider)
{
    public const string Scheme = "ValkyrieID.PendingLogin";
    public const string CookieName = "__Host-valkyrie.pending";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string StepClaim = "step";

    public Task StartAsync(HttpContext context, User user, PendingStep step)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(SessionClaims.Subject, user.Id.ToString()),
                new Claim(SessionClaims.SecurityStamp, user.SecurityStamp),
                new Claim(StepClaim, step.ToString()),
            ],
            Scheme);
        var now = timeProvider.GetUtcNow();
        return context.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IssuedUtc = now,
            ExpiresUtc = now + Lifetime,
            IsPersistent = false,
            AllowRefresh = false,
        });
    }

    /// <summary>
    /// The user waiting for <paramref name="step"/>, or <see langword="null"/> if there's no valid pending login,
    /// it's for another step, or the account was disabled, locked out, or changed since the password was checked.
    /// </summary>
    public async Task<User?> GetAsync(HttpContext context, PendingStep step, CancellationToken cancellationToken)
    {
        var result = await context.AuthenticateAsync(Scheme);
        var principal = result.Principal;
        if (!result.Succeeded || principal is null
            || principal.FindFirstValue(StepClaim) != step.ToString()
            || !Guid.TryParse(principal.FindFirstValue(SessionClaims.Subject), out var userId)
            || principal.FindFirstValue(SessionClaims.SecurityStamp) is not { } stamp
            || result.Properties?.ExpiresUtc is not { } expires || expires <= timeProvider.GetUtcNow())
        {
            return null;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is { Enabled: true } && !user.IsLockedOut(timeProvider.GetUtcNow())
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(stamp), System.Text.Encoding.UTF8.GetBytes(user.SecurityStamp))
            ? user
            : null;
    }

    public Task EndAsync(HttpContext context) => context.SignOutAsync(Scheme);
}
