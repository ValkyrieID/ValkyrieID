using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Cli;

internal sealed class UserCommands(IServiceProvider services, TextReader input, TextWriter output)
{
    internal static readonly AuditActor Actor = new(AuditRecorder.CliActor);

    private readonly ValkyrieDbContext _db = services.GetRequiredService<ValkyrieDbContext>();
    private readonly UserAdministration _users = services.GetRequiredService<UserAdministration>();

    public Task RunAsync(CliArguments args, CancellationToken cancellationToken) => args.Argument(1, "subcommand") switch
    {
        "create" => CreateAsync(args, cancellationToken),
        "list" => ListAsync(args, cancellationToken),
        "enable" => SetEnabledAsync(args, true, cancellationToken),
        "disable" => SetEnabledAsync(args, false, cancellationToken),
        var other => throw new CliException($"Unknown user command '{other}'."),
    };

    private async Task CreateAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var userName = args.Argument(2, "username");
        var email = args.Optional("email");
        var name = args.Optional("name");
        args.EnsureConsumed(3);

        var password = await PasswordReader.ReadNewPasswordAsync(input);
        var user = await Run(() => _users.CreateAsync(new NewUser(userName, email, name, password), Actor, cancellationToken));
        await output.WriteLineAsync($"Created user '{user.UserName}' (sub {user.Id}).");
    }

    private async Task ListAsync(CliArguments args, CancellationToken cancellationToken)
    {
        args.EnsureConsumed(2);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var users = await _db.Users.AsNoTracking().Include(u => u.Roles).OrderBy(u => u.NormalizedUserName).ToListAsync(cancellationToken);
        foreach (var user in users)
        {
            var state = !user.Enabled ? "disabled" : user.IsLockedOut(now) ? "locked" : "enabled";
            var roles = user.Roles.Count == 0 ? "-" : string.Join(' ', user.Roles.Select(r => r.Name).Order(StringComparer.Ordinal));
            await output.WriteLineAsync($"{user.UserName}  {state}  {user.Email ?? "-"}  roles: {roles}  sub: {user.Id}");
        }
    }

    private async Task SetEnabledAsync(CliArguments args, bool enabled, CancellationToken cancellationToken)
    {
        var user = await FindAsync(args, cancellationToken);
        await Run(() => _users.SetEnabledAsync(user, enabled, Actor, cancellationToken));
        await output.WriteLineAsync($"User '{user.UserName}' {(enabled ? "enabled" : "disabled")}.");
    }

    /// <summary>Reads <c>&lt;user&gt;</c> (username or email) at position 2 and loads the user.</summary>
    internal async Task<User> FindAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var identifier = args.Argument(2, "user");
        args.EnsureConsumed(3);
        return await _users.FindAsync(identifier, cancellationToken)
            ?? throw new CliException($"User '{identifier}' does not exist.");
    }

    internal static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (UserAdministrationException ex)
        {
            throw new CliException(ex.Message);
        }
    }

    internal static Task Run(Func<Task> action) => Run(async () =>
    {
        await action();
        return true;
    });
}
