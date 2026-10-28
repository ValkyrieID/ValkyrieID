using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Logout;
using ValkyrieID.Server.Tokens;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Authentication;

/// <summary>
/// Ends and revokes SSO sessions; shared by sign-out, user administration, the CLI, and (later) the Admin API.
/// Changes are added to the context and saved by the caller; after saving, call <see cref="WakeDeliveryWorker"/>.
/// </summary>
internal sealed class SessionService(
    ValkyrieDbContext db,
    RefreshTokenStore refreshTokens,
    BackChannelLogoutQueue backChannel,
    BackChannelLogoutSignal signal,
    AuditRecorder audit,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Ends a session, e.g. at sign-out, and queues back-channel logout notifications for its clients. Refresh tokens
    /// are kept: they are only issued with <c>offline_access</c>, and OIDC Back-Channel Logout 1.0 A2.7 says those
    /// "normally SHOULD NOT be revoked" (PROJECT.md §5).
    /// </summary>
    public async Task<IReadOnlyList<BackChannelLogoutDelivery>> EndAsync(UserSession session, bool claimDeliveries, CancellationToken cancellationToken)
    {
        if (session.RevokedAt is not null)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        session.RevokedAt = now;
        return await backChannel.AddAsync(session, now, claimDeliveries, cancellationToken);
    }

    /// <summary>
    /// Revokes a session on an explicit user or admin action (PROJECT.md §6): ends it with back-channel logout and
    /// revokes the refresh tokens issued from it, including those of a session that was already signed out.
    /// </summary>
    public async Task RevokeAsync(UserSession session, AuditActor actor, string reason, CancellationToken cancellationToken)
    {
        await EndAsync(session, claimDeliveries: false, cancellationToken);
        var revokedTokens = await refreshTokens.RevokeForSessionAsync(session.Id, cancellationToken);
        audit.Add(
            "session.revoked",
            actor: actor.Name,
            ipAddress: actor.IpAddress,
            subjectId: session.UserId,
            details: new Dictionary<string, object?>
            {
                ["sid"] = session.Id,
                ["reason"] = reason,
                ["refresh_tokens_revoked"] = revokedTokens,
            });
    }

    /// <summary>Revokes all of a user's sessions that haven't ended yet. Returns how many were revoked.</summary>
    public async Task<int> RevokeForUserAsync(Guid userId, AuditActor actor, string reason, CancellationToken cancellationToken)
    {
        var sessions = await db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            await RevokeAsync(session, actor, reason, cancellationToken);
        }

        return sessions.Count;
    }

    /// <summary>Delivers newly queued notifications without waiting for the worker's next poll.</summary>
    public void WakeDeliveryWorker() => signal.Notify();
}
