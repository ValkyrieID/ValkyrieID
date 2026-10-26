using System.Security.Cryptography;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Security;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Cli;

/// <summary>Break-glass commands for admin recovery (PROJECT.md §8.4) and master key rotation.</summary>
internal sealed class AdminCommands(IServiceProvider services, TextReader input, TextWriter output)
{
    private readonly UserAdministration _users = services.GetRequiredService<UserAdministration>();
    private readonly UserCommands _userCommands = new(services, input, output);

    public Task<int> RunAsync(CliArguments args, CancellationToken cancellationToken) => args.Argument(1, "subcommand") switch
    {
        "reset-password" => Done(ResetPasswordAsync(args, cancellationToken)),
        "unlock" => Done(UnlockAsync(args, cancellationToken)),
        "grant-admin" => Done(SetAdminAsync(args, true, cancellationToken)),
        "revoke-admin" => Done(SetAdminAsync(args, false, cancellationToken)),
        "reencrypt" => ReencryptAsync(args, cancellationToken),
        var other => throw new CliException($"Unknown admin command '{other}'."),
    };

    private async Task ResetPasswordAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var user = await _userCommands.FindAsync(args, cancellationToken);
        var password = await PasswordReader.ReadNewPasswordAsync(input);
        await UserCommands.Run(() => _users.SetPasswordAsync(user, password, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync($"Password of '{user.UserName}' reset; the account is unlocked and its sessions are signed out.");
    }

    private async Task UnlockAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var user = await _userCommands.FindAsync(args, cancellationToken);
        await _users.UnlockAsync(user, UserCommands.Actor, cancellationToken);
        await output.WriteLineAsync($"User '{user.UserName}' unlocked.");
    }

    private async Task SetAdminAsync(CliArguments args, bool admin, CancellationToken cancellationToken)
    {
        var user = await _userCommands.FindAsync(args, cancellationToken);
        await UserCommands.Run(() => _users.SetAdminAsync(user, admin, UserCommands.Actor, cancellationToken));
        await output.WriteLineAsync(admin ? $"User '{user.UserName}' is an admin." : $"User '{user.UserName}' is no longer an admin.");
    }

    /// <summary>Re-encrypts secrets stored under a previous master key; <c>--check</c> only reports and exits 1 if any remain.</summary>
    private async Task<int> ReencryptAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var check = args.Flag("check");
        args.EnsureConsumed(2);

        ReencryptionReport report;
        try
        {
            report = await services.GetRequiredService<MasterKeyReencryptor>().RunAsync(apply: !check, cancellationToken);
        }
        catch (CryptographicException)
        {
            throw new CliException("Some data can't be decrypted. Configure every master key it was encrypted with in MasterKey:Previous.");
        }

        var summary = $"signing keys: {report.SigningKeys}, data protection keys: {report.DataProtectionKeys}";
        if (check)
        {
            await output.WriteLineAsync(report.Total == 0
                ? "All secrets use the current master key; previous keys can be removed."
                : $"Secrets still encrypted with a previous master key ({summary}). Run `admin reencrypt`.");
            return report.Total == 0 ? 0 : 1;
        }

        var audit = services.GetRequiredService<AuditRecorder>();
        audit.Add("masterkey.reencrypted", actor: AuditRecorder.CliActor, details: new Dictionary<string, object?>
        {
            ["signing_keys"] = report.SigningKeys,
            ["data_protection_keys"] = report.DataProtectionKeys,
        });
        await services.GetRequiredService<Data.ValkyrieDbContext>().SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"Re-encrypted with the current master key ({summary}). Previous keys can now be removed.");
        return 0;
    }

    private static async Task<int> Done(Task task)
    {
        await task;
        return 0;
    }
}
