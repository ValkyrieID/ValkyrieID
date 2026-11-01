using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Administration;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.AdminApi;

internal sealed record NamedRef(Guid Id, string Name);

internal sealed record UserSummary(
    Guid Id,
    string UserName,
    string? Email,
    bool EmailVerified,
    string? DisplayName,
    bool Enabled,
    bool LockedOut,
    bool TwoFactorEnabled,
    bool TwoFactorRequired,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt);

internal sealed record UserClaimDto(Guid Id, string Type, string Value);

internal sealed record UserDetail(
    Guid Id,
    string UserName,
    string? Email,
    bool EmailVerified,
    string? DisplayName,
    string? GivenName,
    string? FamilyName,
    bool Enabled,
    bool LockedOut,
    DateTimeOffset? LockoutEnd,
    int AccessFailedCount,
    bool TwoFactorEnabled,
    bool TwoFactorRequired,
    IReadOnlyList<NamedRef> Roles,
    IReadOnlyList<NamedRef> Groups,
    IReadOnlyList<UserClaimDto> Claims,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PasswordChangedAt);

internal sealed record CreateUserRequest(
    string UserName,
    string Password,
    string? Email = null,
    string? DisplayName = null,
    string? GivenName = null,
    string? FamilyName = null);

internal sealed record UpdateUserRequest(
    string UserName,
    string? Email,
    bool EmailVerified,
    string? DisplayName,
    string? GivenName,
    string? FamilyName);

internal sealed record SetPasswordRequest(string Password);

internal sealed record AddClaimRequest(string Type, string Value);

internal sealed record SessionDto(
    string Id,
    DateTimeOffset AuthenticatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> AuthenticationMethods,
    string? IpAddress,
    string? UserAgent,
    IReadOnlyList<string> Clients);

internal static class UserEndpoints
{
    private const string RevokedByAdmin = "revoked_by_admin";

    public static void Map(RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").WithTags("Users");
        users.MapGet("/", ListAsync);
        users.MapPost("/", CreateAsync);
        users.MapGet("/{id:guid}", GetAsync);
        users.MapPut("/{id:guid}", UpdateAsync);
        users.MapDelete("/{id:guid}", DeleteAsync);
        users.MapPost("/{id:guid}/password", SetPasswordAsync);
        users.MapPost("/{id:guid}/enable", (Guid id, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, user => admin.SetEnabledAsync(user, true, caller.Actor, ct)));
        users.MapPost("/{id:guid}/disable", (Guid id, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, user => admin.SetEnabledAsync(user, false, caller.Actor, ct)));
        users.MapPost("/{id:guid}/unlock", (Guid id, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, user => admin.UnlockAsync(user, caller.Actor, ct)));
        users.MapPost("/{id:guid}/reset-2fa", (Guid id, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, user => admin.ResetTwoFactorAsync(user, caller.Actor, ct)));
        users.MapPost("/{id:guid}/require-2fa", (Guid id, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, user => admin.SetTwoFactorRequiredAsync(user, true, caller.Actor, ct)));
        users.MapPost("/{id:guid}/unrequire-2fa", (Guid id, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, user => admin.SetTwoFactorRequiredAsync(user, false, caller.Actor, ct)));
        users.MapPost("/{id:guid}/send-verification", SendVerificationAsync);
        users.MapPut("/{id:guid}/roles/{roleId:guid}", (Guid id, Guid roleId, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, user => admin.SetRoleAsync(user, roleId, true, caller.Actor, ct)));
        users.MapDelete("/{id:guid}/roles/{roleId:guid}", (Guid id, Guid roleId, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, user => admin.SetRoleAsync(user, roleId, false, caller.Actor, ct)));
        users.MapPut("/{id:guid}/groups/{groupId:guid}", (Guid id, Guid groupId, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, user => admin.SetGroupAsync(user, groupId, true, caller.Actor, ct)));
        users.MapDelete("/{id:guid}/groups/{groupId:guid}", (Guid id, Guid groupId, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, user => admin.SetGroupAsync(user, groupId, false, caller.Actor, ct)));
        users.MapPost("/{id:guid}/claims", AddClaimAsync);
        users.MapDelete("/{id:guid}/claims/{claimId:guid}", (Guid id, Guid claimId, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
            ActAsync(id, admin, time, ct, user => admin.RemoveClaimAsync(user, claimId, caller.Actor, ct)));
        users.MapGet("/{id:guid}/sessions", ListSessionsAsync);
        users.MapDelete("/{id:guid}/sessions", RevokeSessionsAsync);
        users.MapDelete("/{id:guid}/sessions/{sessionId}", RevokeSessionAsync);
    }

    private static async Task<Ok<Page<UserSummary>>> ListAsync(
        ValkyrieDbContext db, TimeProvider time, int? limit, int? offset, string? q, CancellationToken ct)
    {
        var (take, skip, query) = AdminRequests.Paging(limit, offset, q);
        var users = db.Users.AsNoTracking();
        if (query is not null)
        {
            var normalized = UserNames.Normalize(query);
            users = users.Where(u => u.NormalizedUserName.Contains(normalized) || (u.NormalizedEmail != null && u.NormalizedEmail.Contains(normalized)));
        }

        var total = await users.CountAsync(ct);
        var page = await users.Include(u => u.Roles).OrderBy(u => u.NormalizedUserName).Skip(skip).Take(take).ToListAsync(ct);
        var now = time.GetUtcNow();
        return TypedResults.Ok(new Page<UserSummary>([.. page.Select(u => Summary(u, now))], total));
    }

    private static async Task<Created<UserDetail>> CreateAsync(
        CreateUserRequest request, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct)
    {
        var created = await admin.CreateAsync(
            new NewUser(request.UserName, request.Email, request.DisplayName, request.Password, GivenName: request.GivenName, FamilyName: request.FamilyName),
            caller.Actor,
            ct);
        var user = AdminRequests.Found(await admin.FindByIdAsync(created.Id, ct), "user");
        return TypedResults.Created($"{AdminApiAuthentication.PathPrefix}/users/{user.Id}", Detail(user, time.GetUtcNow()));
    }

    private static async Task<Ok<UserDetail>> GetAsync(Guid id, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
        TypedResults.Ok(Detail(await FindAsync(admin, id, ct), time.GetUtcNow()));

    private static Task<Ok<UserDetail>> UpdateAsync(
        Guid id, UpdateUserRequest request, AdminCaller caller, UserAdministration admin, TimeProvider time, CancellationToken ct) =>
        ActAsync(id, admin, time, ct, user => admin.UpdateAsync(
            user,
            new UserUpdate(request.UserName, request.Email, request.EmailVerified, request.DisplayName, request.GivenName, request.FamilyName),
            caller.Actor,
            ct));

    private static async Task<NoContent> DeleteAsync(Guid id, AdminCaller caller, UserAdministration admin, CancellationToken ct)
    {
        await admin.DeleteAsync(await FindAsync(admin, id, ct), caller.Actor, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Sets a new password; the account is unlocked and all its sessions and refresh tokens are revoked.</summary>
    private static async Task<NoContent> SetPasswordAsync(
        Guid id, SetPasswordRequest request, AdminCaller caller, UserAdministration admin, CancellationToken ct)
    {
        await admin.SetPasswordAsync(await FindAsync(admin, id, ct), request.Password, caller.Actor, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Queues a verification email. Repeated requests within the resend interval don't send another one.</summary>
    private static async Task<Accepted> SendVerificationAsync(
        Guid id, AdminCaller caller, UserAdministration admin, EmailAccountService email, CancellationToken ct)
    {
        var user = await FindAsync(admin, id, ct);
        if (!email.Enabled)
        {
            throw AdministrationException.Conflict("Email isn't configured, so no verification email can be sent.");
        }

        if (user.Email is null)
        {
            throw AdministrationException.Conflict("The user has no email address.");
        }

        if (user.EmailVerified)
        {
            throw AdministrationException.Conflict("The email address is already verified.");
        }

        await email.SendVerificationAsync(user, caller.Actor, ct);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Created<UserClaimDto>> AddClaimAsync(
        Guid id, AddClaimRequest request, AdminCaller caller, UserAdministration admin, CancellationToken ct)
    {
        var claim = await admin.AddClaimAsync(await FindAsync(admin, id, ct), request.Type, request.Value, caller.Actor, ct);
        return TypedResults.Created($"{AdminApiAuthentication.PathPrefix}/users/{id}/claims/{claim.Id}", new UserClaimDto(claim.Id, claim.Type, claim.Value));
    }

    private static async Task<Ok<IReadOnlyList<SessionDto>>> ListSessionsAsync(
        Guid id, UserAdministration admin, SessionService sessions, CancellationToken ct)
    {
        var user = await FindAsync(admin, id, ct);
        var active = await sessions.ListActiveAsync(user.Id, ct);
        return TypedResults.Ok<IReadOnlyList<SessionDto>>([.. active.Select(s => new SessionDto(
            s.Session.Id,
            s.Session.AuthenticatedAt,
            s.Session.LastActivityAt,
            s.Session.ExpiresAt,
            s.Session.AuthenticationMethods,
            s.Session.IpAddress,
            s.Session.UserAgent,
            s.Clients))]);
    }

    /// <summary>Force logout: revokes every session of the user, with back-channel logout and their refresh tokens.</summary>
    private static async Task<NoContent> RevokeSessionsAsync(
        Guid id, AdminCaller caller, UserAdministration admin, SessionService sessions, ValkyrieDbContext db, CancellationToken ct)
    {
        var user = await FindAsync(admin, id, ct);
        await sessions.RevokeForUserAsync(user.Id, caller.Actor, RevokedByAdmin, ct);
        await db.SaveChangesAsync(ct);
        sessions.WakeDeliveryWorker();
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RevokeSessionAsync(
        Guid id, string sessionId, AdminCaller caller, SessionService sessions, ValkyrieDbContext db, CancellationToken ct)
    {
        var session = await db.UserSessions.SingleOrDefaultAsync(s => s.Id == sessionId && s.UserId == id && s.RevokedAt == null, ct)
            ?? throw AdministrationException.NotFound("The session does not exist or has already ended.");
        await sessions.RevokeAsync(session, caller.Actor, RevokedByAdmin, ct);
        await db.SaveChangesAsync(ct);
        sessions.WakeDeliveryWorker();
        return TypedResults.NoContent();
    }

    /// <summary>Loads the user, applies a change, and returns the user as it is afterwards.</summary>
    private static async Task<Ok<UserDetail>> ActAsync(Guid id, UserAdministration admin, TimeProvider time, CancellationToken ct, Func<User, Task> action)
    {
        var user = await FindAsync(admin, id, ct);
        await action(user);
        return TypedResults.Ok(Detail(user, time.GetUtcNow()));
    }

    private static async Task<User> FindAsync(UserAdministration admin, Guid id, CancellationToken ct) =>
        AdminRequests.Found(await admin.FindByIdAsync(id, ct), "user");

    private static UserSummary Summary(User user, DateTimeOffset now) => new(
        user.Id,
        user.UserName,
        user.Email,
        user.EmailVerified,
        user.DisplayName,
        user.Enabled,
        user.IsLockedOut(now),
        user.TwoFactorEnabled,
        user.TwoFactorRequired,
        [.. user.Roles.Select(r => r.Name).Order(StringComparer.Ordinal)],
        user.CreatedAt);

    private static UserDetail Detail(User user, DateTimeOffset now) => new(
        user.Id,
        user.UserName,
        user.Email,
        user.EmailVerified,
        user.DisplayName,
        user.GivenName,
        user.FamilyName,
        user.Enabled,
        user.IsLockedOut(now),
        user.LockoutEnd,
        user.AccessFailedCount,
        user.TwoFactorEnabled,
        user.TwoFactorRequired,
        [.. user.Roles.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => new NamedRef(r.Id, r.Name))],
        [.. user.Groups.OrderBy(g => g.Name, StringComparer.Ordinal).Select(g => new NamedRef(g.Id, g.Name))],
        [.. user.Claims.OrderBy(c => c.Type, StringComparer.Ordinal).Select(c => new UserClaimDto(c.Id, c.Type, c.Value))],
        user.CreatedAt,
        user.UpdatedAt,
        user.PasswordChangedAt);
}
