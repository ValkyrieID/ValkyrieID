using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Settings;
using ValkyrieID.Data;
using ValkyrieID.Server.Administration;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Settings;

/// <summary>A changed setting. Secrets are shown as <c>(secret)</c>, never their value.</summary>
internal sealed record SettingChange(string Key, string? OldValue, string? NewValue);

/// <summary>Changes runtime settings for the Admin API and the CLI, with validation and auditing.</summary>
internal sealed class SettingsAdministration(
    ValkyrieDbContext db,
    RuntimeSettingsStore store,
    SecretProtector protector,
    AuditRecorder audit,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Sets the given settings (a <see langword="null"/> value resets one to its default) and returns what changed.
    /// Settings set by configuration can't be changed (<see cref="AdministrationError.Conflict"/>). The resulting
    /// configuration is validated as a whole before anything is saved.
    /// </summary>
    public async Task<IReadOnlyList<SettingChange>> UpdateAsync(
        IReadOnlyDictionary<RuntimeSetting, string?> changes, AuditActor actor, CancellationToken cancellationToken)
    {
        var locked = changes.Keys.Where(store.IsLocked).Select(s => s.Key).ToList();
        if (locked.Count > 0)
        {
            throw AdministrationException.Conflict($"Set by configuration, so it can't be changed here: {string.Join(", ", locked)}.");
        }

        var (_, current) = await store.ReadAsync(db, cancellationToken);
        var candidate = new Dictionary<string, string?>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var (setting, value) in changes)
        {
            if (value is null)
            {
                candidate.Remove(setting.Key);
            }
            else
            {
                candidate[setting.Key] = value;
            }
        }

        List<string> errors = [.. store.Validate(candidate)];
        errors.AddRange(CrossChecks(changes, store.Merge(candidate)));
        if (errors.Count > 0)
        {
            throw new AdministrationException(errors);
        }

        var changed = changes
            .Where(c => !string.Equals(current.GetValueOrDefault(c.Key.Key), c.Value, StringComparison.Ordinal))
            .ToList();
        if (changed.Count == 0)
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        var keys = changed.Select(c => c.Key.Key).ToList();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var rows = await db.Settings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);
            foreach (var (setting, value) in changed)
            {
                var row = rows.GetValueOrDefault(setting.Key);
                if (value is null)
                {
                    if (row is not null)
                    {
                        db.Settings.Remove(row);
                    }

                    continue;
                }

                if (row is null)
                {
                    row = new Setting { Key = setting.Key, UpdatedAt = now };
                    db.Settings.Add(row);
                }

                if (setting.Kind == SettingKind.Secret)
                {
                    row.Value = null;
                    row.EncryptedValue = protector.Protect(value, RuntimeSettings.SecretPurpose(setting));
                }
                else
                {
                    row.Value = value;
                    row.EncryptedValue = null;
                }

                row.UpdatedAt = now;
                row.UpdatedBy = actor.Name;
            }

            var result = changed.Select(c => new SettingChange(c.Key.Key, c.Key.Display(current.GetValueOrDefault(c.Key.Key)), c.Key.Display(c.Value))).ToList();
            audit.Add("settings.updated", actor: actor.Name, ipAddress: actor.IpAddress, details: new Dictionary<string, object?>
            {
                ["changes"] = result.Select(c => new Dictionary<string, object?> { ["key"] = c.Key, ["old"] = c.OldValue, ["new"] = c.NewValue }).ToList(),
            });
            await db.SaveChangesAsync(cancellationToken);
            await db.SettingsRevisions
                .Where(r => r.Id == SettingsRevision.SingletonId)
                .ExecuteUpdateAsync(r => r.SetProperty(x => x.Revision, x => x.Revision + 1), cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await store.LoadAsync(validate: true, cancellationToken);
            return result;
        }
    }

    /// <summary>Rules that involve more than one section or the file system; checked only for the settings being changed.</summary>
    private static IEnumerable<string> CrossChecks(IReadOnlyDictionary<RuntimeSetting, string?> changes, IConfiguration merged)
    {
        var keys = changes.Keys.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var branding = merged.GetSection(BrandingOptions.SectionName).Get<BrandingOptions>() ?? new();
        foreach (var (setting, value) in changes)
        {
            if (setting.Kind == SettingKind.File && value is not null && !BrandingFiles.List(branding.Directory, setting.Property.Name).Contains(value, StringComparer.Ordinal))
            {
                yield return $"{setting.Key} must be one of the files in Branding:Directory.";
            }
        }

        var verificationKey = $"{EmailOptions.SectionName}:{nameof(EmailOptions.VerificationAtSignIn)}";
        var hostKey = $"{SmtpOptions.SectionName}:{nameof(SmtpOptions.Host)}";
        if (keys.Contains(verificationKey) || keys.Contains(hostKey))
        {
            var email = merged.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new();
            var smtp = merged.GetSection(SmtpOptions.SectionName).Get<SmtpOptions>() ?? new();
            if (email.VerificationAtSignIn != EmailVerificationAtSignIn.Off && !smtp.Enabled)
            {
                yield return "Email:VerificationAtSignIn can only be Prompt or Require while SMTP is configured.";
            }
        }
    }
}

/// <summary>The files in <c>Branding:Directory</c> that can be chosen as logo, background image, or stylesheet.</summary>
internal static class BrandingFiles
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".svg", ".ico"];

    public static IReadOnlyList<string> List(string? directory, string property)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        var extensions = property == nameof(BrandingOptions.CustomCss) ? [".css"] : ImageExtensions;
        return [.. new DirectoryInfo(directory).EnumerateFiles()
            .Select(f => f.Name)
            .Where(n => BrandingOptions.FileNamePattern().IsMatch(n) && extensions.Contains(Path.GetExtension(n), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];
    }
}
