using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Data;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Authentication;

/// <summary>
/// Decides whether a signed-in user has to see the email verification page before being sent back to an
/// application (<see cref="EmailOptions.VerificationAtSignIn"/>). It only applies while SMTP is configured, since
/// the address can't be verified otherwise.
/// </summary>
internal sealed class EmailVerificationGate(
    ValkyrieDbContext db, IOptionsMonitor<EmailOptions> email, IOptionsMonitor<SmtpOptions> smtp)
{
    public const string PagePath = "/account/verify-required";

    /// <summary>Remembers, for one session, that the user was already offered verification (prompt mode).</summary>
    public const string PromptedCookieName = "__Host-valkyrie.verify-prompted";

    /// <summary>What the user must see before continuing: <see cref="EmailVerificationAtSignIn.Off"/> means nothing.</summary>
    public async Task<EmailVerificationAtSignIn> CheckAsync(HttpContext context, Guid userId, string sessionId, CancellationToken cancellationToken)
    {
        var mode = Mode;
        if (mode == EmailVerificationAtSignIn.Off)
        {
            return mode;
        }

        if (mode == EmailVerificationAtSignIn.Prompt && context.Request.Cookies[PromptedCookieName] == sessionId)
        {
            return EmailVerificationAtSignIn.Off;
        }

        var verified = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Email != null && u.EmailVerified)
            .SingleOrDefaultAsync(cancellationToken);
        return verified ? EmailVerificationAtSignIn.Off : mode;
    }

    public EmailVerificationAtSignIn Mode =>
        smtp.CurrentValue.Enabled ? email.CurrentValue.VerificationAtSignIn : EmailVerificationAtSignIn.Off;

    /// <summary>Records that the prompt was shown for this session, so the user can continue without verifying.</summary>
    public static void MarkPrompted(HttpContext context, string sessionId) =>
        context.Response.Cookies.Append(PromptedCookieName, sessionId, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
        });
}
