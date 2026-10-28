using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Logout;

/// <summary>Queues back-channel logout notifications for the clients of an ended session.</summary>
internal sealed class BackChannelLogoutQueue(
    ValkyrieDbContext db,
    BackChannelLogoutDispatcher dispatcher,
    IOptions<BackChannelLogoutOptions> options)
{
    /// <summary>
    /// Adds a delivery for every client that received a code from <paramref name="session"/> and registered a
    /// back-channel logout URI (OIDC Back-Channel Logout 1.0 A2.3). The caller saves them. With
    /// <paramref name="claim"/>, this instance holds them so it can deliver them right away.
    /// </summary>
    public async Task<IReadOnlyList<BackChannelLogoutDelivery>> AddAsync(
        UserSession session, DateTimeOffset now, bool claim, CancellationToken cancellationToken)
    {
        var clientIds = await db.UserSessionClients
            .Where(sc => sc.SessionId == session.Id)
            .Join(db.Clients.Where(c => c.BackChannelLogoutUri != null), sc => sc.ClientId, c => c.ClientId, (sc, c) => c.ClientId)
            .Distinct()
            .ToListAsync(cancellationToken);

        List<BackChannelLogoutDelivery> deliveries = [];
        foreach (var clientId in clientIds.Order(StringComparer.Ordinal))
        {
            var delivery = new BackChannelLogoutDelivery
            {
                ClientId = clientId,
                UserId = session.UserId,
                SessionId = session.Id,
                CreatedAt = now,
                NextAttemptAt = now,
                LeaseOwner = claim ? dispatcher.InstanceId : null,
                LeaseExpiresAt = claim ? now + options.Value.LeaseDuration : null,
            };
            db.BackChannelLogoutDeliveries.Add(delivery);
            deliveries.Add(delivery);
        }

        return deliveries;
    }
}
