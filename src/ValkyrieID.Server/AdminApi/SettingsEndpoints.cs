using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ValkyrieID.Data;
using ValkyrieID.Server.Administration;
using ValkyrieID.Server.Auditing;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Email;
using ValkyrieID.Server.Settings;

namespace ValkyrieID.Server.AdminApi;

/// <summary>
/// A runtime setting. <see cref="Kind"/> is <c>duration</c> (seconds), <c>integer</c>, <c>boolean</c>, <c>string</c>,
/// <c>enum</c> or <c>file</c> (one of <see cref="Options"/>), or <c>secret</c> (never returned; see <see cref="IsSet"/>).
/// <see cref="Source"/> is <c>default</c>, <c>database</c>, or <c>configuration</c>; settings from configuration are
/// <see cref="Locked"/>.
/// </summary>
internal sealed record SettingDto(
    string Name,
    string Key,
    string Kind,
    JsonElement? Value,
    JsonElement? DefaultValue,
    bool Locked,
    string Source,
    bool? IsSet,
    IReadOnlyList<string>? Options);

internal sealed record SettingsSectionDto(string Name, IReadOnlyList<SettingDto> Settings);

internal sealed record SettingsDto(IReadOnlyList<SettingsSectionDto> Sections);

/// <summary>The result of a test email: <c>sent</c>, or why it failed (e.g. <c>authentication_failed</c>).</summary>
internal sealed record SmtpTestResult(bool Success, string Result, string To);

internal static class SettingsEndpoints
{
    private static readonly string RequireTwoFactorKey = $"{AdminApiOptions.SectionName}:{nameof(AdminApiOptions.RequireTwoFactor)}";

    public static void Map(RouteGroupBuilder api)
    {
        var settings = api.MapGroup("/settings").WithTags("Settings");
        settings.MapGet("/", GetAll);
        settings.MapGet("/{section}", Get);
        settings.MapPatch("/{section}", UpdateAsync);
        settings.MapPost("/smtp/test", SendTestEmailAsync).RequireRateLimiting(RateLimitPolicies.Email);
    }

    private static Ok<SettingsDto> GetAll(RuntimeSettingsStore store, IConfiguration configuration) =>
        TypedResults.Ok(new SettingsDto([.. RuntimeSettings.Sections.Select(s => Dto(s, store, configuration))]));

    private static Ok<SettingsSectionDto> Get(string section, RuntimeSettingsStore store, IConfiguration configuration) =>
        TypedResults.Ok(Dto(FindSection(section), store, configuration));

    /// <summary>
    /// Changes the listed settings of a section; settings not listed stay as they are. <c>null</c> resets a setting to
    /// its default (for a secret: removes it). Returns the section afterwards.
    /// </summary>
    private static async Task<Ok<SettingsSectionDto>> UpdateAsync(
        string section,
        Dictionary<string, JsonElement> values,
        AdminCaller caller,
        SettingsAdministration administration,
        RuntimeSettingsStore store,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var found = FindSection(section);
        Dictionary<RuntimeSetting, string?> changes = [];
        List<string> errors = [];
        foreach (var (name, value) in values)
        {
            var setting = found.Settings.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            if (setting is null)
            {
                errors.Add($"Unknown setting '{name}' in section '{found.Name}'.");
                continue;
            }

            try
            {
                changes[setting] = setting.FromJson(value);
            }
            catch (AdministrationException ex)
            {
                errors.AddRange(ex.Errors);
            }
        }

        if (errors.Count > 0)
        {
            throw new AdministrationException(errors);
        }

        // An admin whose own sign-in didn't use a second factor would lock themselves out of the API.
        if (changes.Any(c => c.Key.Key == RequireTwoFactorKey && c.Value != "false") && !caller.SecondFactor)
        {
            throw AdministrationException.Conflict(
                "Sign in with two-factor authentication before requiring it for the Admin API, or you would lose access.");
        }

        await administration.UpdateAsync(changes, caller.Actor, ct);
        return TypedResults.Ok(Dto(found, store, configuration));
    }

    /// <summary>Sends a test email to the calling admin's address right away, bypassing the queue.</summary>
    private static async Task<Ok<SmtpTestResult>> SendTestEmailAsync(
        AdminCaller caller,
        ValkyrieDbContext db,
        IEmailSender sender,
        AuditRecorder audit,
        IOptionsMonitor<SmtpOptions> smtp,
        IOptionsMonitor<BrandingOptions> branding,
        CancellationToken ct)
    {
        if (!smtp.CurrentValue.Enabled)
        {
            throw AdministrationException.Conflict("SMTP isn't configured.");
        }

        var email = await db.Users.AsNoTracking().Where(u => u.Id == caller.UserId).Select(u => u.Email).SingleOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(email))
        {
            throw AdministrationException.Conflict("Your account has no email address to send the test email to.");
        }

        string result;
        try
        {
            await sender.SendAsync(EmailTemplates.Test(branding.CurrentValue.ProductName, email), ct);
            result = "sent";
        }
        catch (EmailDeliveryException ex)
        {
            // Only the result code is returned and recorded, not the server's reply or exception details.
            result = ex.Result;
        }

        var success = result == "sent";
        audit.Add("email.test", success: success, actor: caller.Actor.Name, ipAddress: caller.IpAddress, subjectId: caller.UserId,
            details: new Dictionary<string, object?> { ["result"] = result });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new SmtpTestResult(success, result, email));
    }

    private static RuntimeSettingSection FindSection(string name) =>
        RuntimeSettings.Sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
        ?? throw AdministrationException.NotFound($"There is no settings section '{name}'.");

    private static SettingsSectionDto Dto(RuntimeSettingSection section, RuntimeSettingsStore store, IConfiguration configuration)
    {
        var current = section.Bind(configuration);
        var defaults = section.Defaults();
        var brandingDirectory = configuration.GetSection(BrandingOptions.SectionName).Get<BrandingOptions>()?.Directory;
        return new SettingsSectionDto(section.Name, [.. section.Settings.Select(s => new SettingDto(
            s.Name,
            s.Key,
            JsonNamingPolicy.CamelCase.ConvertName(s.Kind.ToString()),
            Element(s.ToJson(current)),
            Element(s.ToJson(defaults)),
            store.IsLocked(s),
            store.SourceOf(s),
            s.Kind == SettingKind.Secret ? s.HasValue(current) : null,
            s.Kind switch
            {
                SettingKind.Enum => s.EnumValues,
                SettingKind.File => BrandingFiles.List(brandingDirectory, s.Property.Name),
                _ => null,
            }))]);
    }

    private static JsonElement? Element(System.Text.Json.Nodes.JsonNode? node) =>
        node is null ? null : JsonSerializer.SerializeToElement(node);
}
