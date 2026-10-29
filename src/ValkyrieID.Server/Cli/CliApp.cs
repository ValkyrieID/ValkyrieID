namespace ValkyrieID.Server.Cli;

/// <summary>
/// Maintenance commands that run against the configured database without starting the web server,
/// e.g. <c>valkyrieid client create my-service --scope api.read</c>.
/// </summary>
internal static class CliApp
{
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal) { "client", "resource", "user", "session", "admin", "help" };

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    /// <param name="input">Source of passwords; defaults to the console.</param>
    public static async Task<int> RunAsync(
        IServiceProvider services,
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default,
        TextReader? input = null)
    {
        input ??= Console.In;
        try
        {
            var parsed = CliArguments.Parse(args);
            await using var scope = services.CreateAsyncScope();
            var command = parsed.Positional.Count > 0 ? parsed.Positional[0] : "help";
            switch (command)
            {
                case "client":
                    await new ClientCommands(scope.ServiceProvider, output).RunAsync(parsed, cancellationToken);
                    break;
                case "resource":
                    await new ResourceCommands(scope.ServiceProvider, output).RunAsync(parsed, cancellationToken);
                    break;
                case "user":
                    await new UserCommands(scope.ServiceProvider, input, output).RunAsync(parsed, cancellationToken);
                    break;
                case "session":
                    await new SessionCommands(scope.ServiceProvider, output).RunAsync(parsed, cancellationToken);
                    break;
                case "admin":
                    return await new AdminCommands(scope.ServiceProvider, input, output).RunAsync(parsed, cancellationToken);
                default:
                    await output.WriteLineAsync(Usage);
                    break;
            }

            return 0;
        }
        catch (CliException ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}");
            return 1;
        }
    }

    public const string Usage = """
        Usage: valkyrieid <command> [arguments]

        Users (<user> is a username or email address):
          user create <username> [--email <email>] [--name <display name>]
          user list
          user enable <user>
          user disable <user>

        Sessions (revoking also revokes the session's refresh tokens and notifies clients by back-channel logout):
          session list --user <user>
          session revoke <session-id>
          session revoke --user <user>
        Admin recovery:
          admin reset-password <user>
          admin unlock <user>
          admin grant-admin <user>
          admin revoke-admin <user>
          admin reencrypt [--check]

        Passwords are read from a hidden prompt, or as one line from standard input when it is redirected.

        Clients:
          client create <client-id> --scope <scope>... [--name <display name>]
                        [--grant authorization_code|client_credentials|refresh_token]... [--public] [--no-pkce]
                        [--redirect-uri <uri>]... [--post-logout-redirect-uri <uri>]... [--cors-origin <origin>]...
                        [--backchannel-logout-uri <uri>]
                        [--access-token-lifetime <seconds>] [--id-token-lifetime <seconds>]
                        [--refresh-token-lifetime <seconds>] [--refresh-token-absolute-lifetime <seconds>]
                        [--secret-expires-days <days>] [--allow-secret-post]
          client list
          client add-secret <client-id> [--description <text>] [--expires-days <days>]
          client remove-secret <client-id> <secret-id>
          client enable <client-id>
          client disable <client-id>
          client delete <client-id>
          client add-redirect-uri|remove-redirect-uri <client-id> <uri>
          client add-post-logout-redirect-uri|remove-post-logout-redirect-uri <client-id> <uri>
          client add-cors-origin|remove-cors-origin <client-id> <origin>
          client set-backchannel-logout-uri <client-id> <uri>
          client clear-backchannel-logout-uri <client-id>
          client enable-secret-post|disable-secret-post <client-id>

        API resources:
          resource create <name> --scope <scope>... [--name <display name>]
          resource list
          resource add-scope <name> <scope> [--description <text>]
          resource add-secret <name> [--description <text>] [--expires-days <days>]
          resource remove-secret <name> <secret-id>
          resource enable <name>
          resource disable <name>
          resource delete <name>
          resource enable-secret-post|disable-secret-post <name>

        Configuration is read from appsettings.json and environment variables, as for the server.
        """;
}
