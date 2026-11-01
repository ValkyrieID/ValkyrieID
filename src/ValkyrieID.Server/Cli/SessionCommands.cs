using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Cli;

internal sealed class SessionCommands(IServiceProvider services, TextWriter output)
{
    private readonly ValkyrieDbContext _db = services.GetRequiredService<ValkyrieDbContext>();
    private readonly SessionService _sessions = services.GetRequiredService<SessionService>();

    public Task RunAsync(CliArguments args, CancellationToken cancellationToken) => args.Argument(1, "subcommand") switch
    {
        "list" => ListAsync(args, cancellationToken),
        "revoke" => RevokeAsync(args, cancellationToken),
        var other => throw new CliException($"Unknown session command '{other}'."),
    };

    private async Task ListAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var identifier = args.Optional("user") ?? throw new CliException("Option --user is required.");
        args.EnsureConsumed(2);

        var user = await FindUserAsync(identifier, cancellationToken);
        var sessions = await _sessions.ListActiveAsync(user.Id, cancellationToken);
        foreach (var (session, sessionClients) in sessions)
        {
            await output.WriteLineAsync(
                $"{session.Id}  signed in: {session.AuthenticatedAt:u}  last activity: {session.LastActivityAt:u}  expires: {session.ExpiresAt:u}  ip: {session.IpAddress ?? "-"}");
            await output.WriteLineAsync($"  clients: {(sessionClients.Count == 0 ? "-" : string.Join(' ', sessionClients))}");
            await output.WriteLineAsync($"  user agent: {session.UserAgent ?? "-"}");
        }

        if (sessions.Count == 0)
        {
            await output.WriteLineAsync($"User '{user.UserName}' has no active sessions.");
        }
    }

    private async Task RevokeAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var identifier = args.Optional("user");
        var sessionId = identifier is null ? args.Argument(2, "session-id") : null;
        args.EnsureConsumed(identifier is null ? 3 : 2);

        if (identifier is not null)
        {
            var user = await FindUserAsync(identifier, cancellationToken);
            var count = await _sessions.RevokeForUserAsync(user.Id, UserCommands.Actor, "revoked_by_admin", cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            _sessions.WakeDeliveryWorker();
            await output.WriteLineAsync($"Revoked {count} session(s) of user '{user.UserName}'.");
            return;
        }

        var session = await _db.UserSessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new CliException($"Session '{sessionId}' does not exist.");
        await _sessions.RevokeAsync(session, UserCommands.Actor, "revoked_by_admin", cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        _sessions.WakeDeliveryWorker();
        await output.WriteLineAsync($"Revoked session '{session.Id}'.");
    }

    private async Task<User> FindUserAsync(string identifier, CancellationToken cancellationToken) =>
        await services.GetRequiredService<UserAdministration>().FindAsync(identifier, cancellationToken)
        ?? throw new CliException($"User '{identifier}' does not exist.");
}
