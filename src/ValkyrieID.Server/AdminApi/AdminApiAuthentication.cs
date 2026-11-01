using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Administration;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Protocol;
using ValkyrieID.Server.Tokens;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.AdminApi;

/// <summary>The administrator making an Admin API request; bound as a handler parameter.</summary>
internal sealed record AdminCaller(Guid UserId, string UserName, string? IpAddress)
{
    /// <summary>Audit actor: the admin's immutable <c>sub</c>, so entries stay attributable after a rename.</summary>
    public AuditActor Actor => new($"admin:{UserId}", IpAddress);

    public static ValueTask<AdminCaller?> BindAsync(HttpContext context, ParameterInfo parameter) =>
        ValueTask.FromResult(context.Features.Get<AdminCaller>());
}

/// <summary>
/// Authenticates every request under <see cref="PathPrefix"/> before routing to a handler, so nothing (not even
/// request validation or a 404) is revealed to unauthenticated callers. A request is allowed only when:
/// <list type="number">
/// <item>it carries an access token in the <c>Authorization: Bearer</c> header (RFC 6750 §2.1), never in the
/// query string (§2.3; RFC 9700 §4.3.2) or a cookie, so cross-site requests can't use an ambient credential;</item>
/// <item>the token is a valid access token from this issuer (signature, <c>typ: at+jwt</c>, lifetime; RFC 9068 §4)
/// whose audience includes the Admin API resource;</item>
/// <item>it was granted the admin scope;</item>
/// <item>its <c>sub</c> is a user who, right now, exists, is enabled, isn't locked out, and has the admin role.
/// Checking the database on every request means losing the role or being disabled takes effect at once, even
/// though access tokens can't be revoked (PROJECT.md §5).</item>
/// </list>
/// </summary>
internal sealed class AdminApiAuthentication(RequestDelegate next)
{
    public const string PathPrefix = "/api/admin";

    public async Task InvokeAsync(
        HttpContext context, AccessTokenValidator validator, ValkyrieDbContext db, AuditRecorder audit, TimeProvider timeProvider)
    {
        ProtocolHeaders.NoStore(context.Response);
        var request = context.Request;
        var cancellationToken = context.RequestAborted;

        if (request.Query.ContainsKey("access_token"))
        {
            await Challenge(context, StatusCodes.Status400BadRequest, "invalid_request", "Send the access token in the Authorization header, not in the URL.");
            return;
        }

        if (request.Headers.Authorization.Count == 0)
        {
            // RFC 6750 §3.1: without authentication information, no error code is included.
            await Challenge(context, StatusCodes.Status401Unauthorized, null, "An access token is required.");
            return;
        }

        if (request.Headers.Authorization.Count > 1
            || !AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(header.Parameter))
        {
            await Challenge(context, StatusCodes.Status400BadRequest, "invalid_request", "The Authorization header is malformed.");
            return;
        }

        // RFC 9068 §4: the resource server validates that the audience contains its own identifier.
        if (await validator.ValidateAsync(header.Parameter, cancellationToken) is not { } jwt
            || !jwt.Audiences.Contains(BuiltIn.AdminResource, StringComparer.Ordinal))
        {
            await InvalidToken(context);
            return;
        }

        var scopes = jwt.TryGetPayloadValue<string>("scope", out var scope) ? scope.Split(' ') : [];
        if (!scopes.Contains(BuiltIn.AdminScope, StringComparer.Ordinal))
        {
            await Challenge(context, StatusCodes.Status403Forbidden, "insufficient_scope", "The access token was not issued with the admin scope.", BuiltIn.AdminScope);
            return;
        }

        // Tokens without a user (client_credentials: sub is the client id) are never accepted (PROJECT.md §9).
        var user = Guid.TryParse(jwt.Subject, out var subject)
            ? await db.Users.AsNoTracking()
                .Where(u => u.Id == subject)
                .Select(u => new { u.Id, u.UserName, u.Enabled, u.LockoutEnd, IsAdmin = u.Roles.Any(r => r.Id == Role.AdminRoleId) })
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        if (user is not { Enabled: true })
        {
            await InvalidToken(context);
            return;
        }

        var ipAddress = context.Connection.RemoteIpAddress?.ToString();
        var lockedOut = user.LockoutEnd is { } end && timeProvider.GetUtcNow() < end;
        if (!user.IsAdmin || lockedOut)
        {
            audit.Add("admin_api.access_denied", success: false, actor: user.Id.ToString(), ipAddress: ipAddress, subjectId: user.Id,
                details: new Dictionary<string, object?> { ["reason"] = lockedOut ? "locked_out" : "not_admin" });
            await db.SaveChangesAsync(cancellationToken);
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "The Admin API requires the admin role.").ExecuteAsync(context);
            return;
        }

        context.Features.Set(new AdminCaller(user.Id, user.UserName, ipAddress));
        await next(context);
    }

    private static Task InvalidToken(HttpContext context) =>
        Challenge(context, StatusCodes.Status401Unauthorized, "invalid_token", "The access token is invalid or expired.");

    /// <summary>RFC 6750 §3: a <c>WWW-Authenticate: Bearer</c> challenge, with an RFC 9457 problem body.</summary>
    private static Task Challenge(HttpContext context, int statusCode, string? error, string description, string? scope = null)
    {
        var challenge = "Bearer realm=\"ValkyrieID\"";
        if (error is not null)
        {
            challenge += $", error=\"{error}\", error_description=\"{description}\"";
        }

        if (scope is not null)
        {
            challenge += $", scope=\"{scope}\"";
        }

        context.Response.Headers.WWWAuthenticate = challenge;
        return Results.Problem(statusCode: statusCode, detail: description).ExecuteAsync(context);
    }
}
