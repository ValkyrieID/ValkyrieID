using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ValkyrieID.Core.Users;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Email;
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
        "reset-2fa" => Done(ResetTwoFactorAsync(args, cancellationToken)),
        "grant-admin" => Done(SetAdminAsync(args, true, cancellationToken)),
        "revoke-admin" => Done(SetAdminAsync(args, false, cancellationToken)),
        "reencrypt" => ReencryptAsync(args, cancellationToken),
        "send-test-email" => Done(SendTestEmailAsync(args, cancellationToken)),
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

    private async Task ResetTwoFactorAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var user = await _userCommands.FindAsync(args, cancellationToken);
        await _users.ResetTwoFactorAsync(user, UserCommands.Actor, cancellationToken);
        await output.WriteLineAsync($"2FA of '{user.UserName}' reset; its recovery codes are deleted and its sessions are signed out.");
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

        var summary = $"signing keys: {report.SigningKeys}, data protection keys: {report.DataProtectionKeys}, TOTP secrets: {report.TotpSecrets}, queued emails: {report.QueuedEmails}, settings: {report.Settings}";
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
            ["totp_secrets"] = report.TotpSecrets,
            ["queued_emails"] = report.QueuedEmails,
            ["settings"] = report.Settings,
        });
        await services.GetRequiredService<Data.ValkyrieDbContext>().SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"Re-encrypted with the current master key ({summary}). Previous keys can now be removed.");
        return 0;
    }

    /// <summary>Sends an email right away, without the queue, so SMTP problems are reported here.</summary>
    private async Task SendTestEmailAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var address = args.Argument(2, "address");
        args.EnsureConsumed(3);
        if (!services.GetRequiredService<IOptionsMonitor<SmtpOptions>>().CurrentValue.Enabled)
        {
            throw new CliException("SMTP isn't configured; set Smtp:Host and Smtp:FromAddress.");
        }

        if (!UserNames.IsValidEmail(address))
        {
            throw new CliException("The email address is invalid.");
        }

        var product = services.GetRequiredService<IOptionsMonitor<BrandingOptions>>().CurrentValue.ProductName;
        var audit = services.GetRequiredService<AuditRecorder>();
        try
        {
            await services.GetRequiredService<IEmailSender>().SendAsync(EmailTemplates.Test(product, address), cancellationToken);
        }
        catch (EmailDeliveryException ex)
        {
            audit.Add("email.test", success: false, actor: AuditRecorder.CliActor, details: new Dictionary<string, object?> { ["result"] = ex.Result });
            await services.GetRequiredService<Data.ValkyrieDbContext>().SaveChangesAsync(cancellationToken);
            throw new CliException($"Sending the test email failed ({ex.Result}): {ex.InnerException?.Message ?? ex.Message}");
        }

        audit.Add("email.test", actor: AuditRecorder.CliActor, details: new Dictionary<string, object?> { ["result"] = "sent" });
        await services.GetRequiredService<Data.ValkyrieDbContext>().SaveChangesAsync(cancellationToken);
        await output.WriteLineAsync($"Test email sent to {address}.");
    }

    private static async Task<int> Done(Task task)
    {
        await task;
        return 0;
    }
}
