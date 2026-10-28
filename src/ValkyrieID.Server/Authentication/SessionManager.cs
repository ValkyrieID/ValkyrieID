using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Logout;

namespace ValkyrieID.Server.Authentication;

internal static class SessionClaims
{
    public const string Scheme = "ValkyrieID.Session";
    public const string CookieName = "__Host-valkyrie.session";

    public const string Subject = "sub";
    public const string SessionId = "sid";
    public const string Name = "name";
    public const string AuthTime = "auth_time";
    public const string AuthenticationMethod = "amr";
    public const string SecurityStamp = "valkyrie:stamp";
}

/// <summary>Creates and ends SSO sessions: a <see cref="UserSession"/> row plus the session cookie that references it.</summary>
internal sealed class SessionManager(
    ValkyrieDbContext db,
    SessionService sessions,
    BackChannelLogoutDispatcher backChannel,
    AuditRecorder audit,
    TimeProvider timeProvider,
    IOptions<SsoSessionOptions> options)
{
    public async Task SignInAsync(HttpContext context, User user, IReadOnlyList<string> methods, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // A new login (e.g. forced by prompt=login) replaces the current session instead of leaving it active. The
        // replaced session has ended, so its clients are notified.
        var replaced = false;
        if (context.User.FindFirstValue(SessionClaims.SessionId) is { } previousId)
        {
            var previous = await db.UserSessions.SingleOrDefaultAsync(s => s.Id == previousId, cancellationToken);
            if (previous is { RevokedAt: null })
            {
                replaced = (await sessions.EndAsync(previous, claimDeliveries: false, cancellationToken)).Count > 0;
            }
        }

        var session = new UserSession
        {
            UserId = user.Id,
            AuthenticatedAt = now,
            AuthenticationMethods = [.. methods],
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(context.Request.Headers.UserAgent.ToString(), 500),
            LastActivityAt = now,
            ExpiresAt = now + options.Value.Lifetime,
        };
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        if (replaced)
        {
            sessions.WakeDeliveryWorker();
        }

        List<Claim> claims =
        [
            new(SessionClaims.Subject, user.Id.ToString()),
            new(SessionClaims.SessionId, session.Id),
            new(SessionClaims.Name, user.UserName),
            new(SessionClaims.AuthTime, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new(SessionClaims.SecurityStamp, user.SecurityStamp),
            .. methods.Select(m => new Claim(SessionClaims.AuthenticationMethod, m)),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SessionClaims.Scheme, SessionClaims.Name, null));

        // An absolute lifetime: the cookie isn't renewed, and it's dropped when the browser closes.
        await context.SignInAsync(SessionClaims.Scheme, principal, new AuthenticationProperties
        {
            IssuedUtc = now,
            ExpiresUtc = session.ExpiresAt,
            IsPersistent = false,
            AllowRefresh = false,
        });
    }

    /// <summary>
    /// Ends the current session and removes the cookie. The session's clients are notified by back-channel logout
    /// first: OIDC RP-Initiated Logout 1.0 A3 performs the post-logout redirect "after the OP has finished notifying
    /// the RPs". This waits at most <see cref="BackChannelLogoutOptions.RequestTimeout"/>; failures are retried later.
    /// </summary>
    public async Task SignOutAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var sessionId = context.User.FindFirstValue(SessionClaims.SessionId);
        var subject = context.User.FindFirstValue(SessionClaims.Subject);
        if (sessionId is not null)
        {
            var session = await db.UserSessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
            if (session is { RevokedAt: null })
            {
                var deliveries = await sessions.EndAsync(session, claimDeliveries: true, cancellationToken);
                audit.Add("user.logout", actor: subject, ipAddress: context.Connection.RemoteIpAddress?.ToString(), subjectId: session.UserId,
                    details: new Dictionary<string, object?> { ["sid"] = session.Id });
                await db.SaveChangesAsync(cancellationToken);
                await backChannel.DeliverClaimedAsync([.. deliveries.Select(d => d.Id)]);
            }
        }

        await context.SignOutAsync(SessionClaims.Scheme);
    }

    private static string? Truncate(string value, int length) =>
        value.Length == 0 ? null : value.Length <= length ? value : value[..length];
}

/// <summary>
/// Validates the session cookie on every request: the session must exist and be active, and the user must be
/// enabled with an unchanged security stamp. Otherwise the cookie is rejected.
/// </summary>
internal sealed class SessionCookieEvents(ValkyrieDbContext db, TimeProvider timeProvider) : CookieAuthenticationEvents
{
    private static readonly TimeSpan ActivityUpdateInterval = TimeSpan.FromMinutes(1);

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var sessionId = principal?.FindFirstValue(SessionClaims.SessionId);
        var stamp = principal?.FindFirstValue(SessionClaims.SecurityStamp);
        var cancellationToken = context.HttpContext.RequestAborted;
        var now = timeProvider.GetUtcNow();

        var session = sessionId is null
            ? null
            : await db.UserSessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        var user = session is null
            ? null
            : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.UserId, cancellationToken);

        if (session is null || user is null || !session.IsActive(now) || !user.Enabled
            || principal?.FindFirstValue(SessionClaims.Subject) != user.Id.ToString()
            || stamp is null || !FixedTimeEquals(stamp, user.SecurityStamp))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(SessionClaims.Scheme);
            return;
        }

        if (now - session.LastActivityAt >= ActivityUpdateInterval)
        {
            session.LastActivityAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
