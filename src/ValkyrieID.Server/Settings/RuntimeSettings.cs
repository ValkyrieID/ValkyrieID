using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ValkyrieID.Server.Administration;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Settings;

internal enum SettingKind
{
    /// <summary>A <see cref="TimeSpan"/>; whole seconds in the Admin API.</summary>
    Duration,
    Integer,
    Boolean,
    String,
    Enum,

    /// <summary>A file name in <c>Branding:Directory</c>.</summary>
    File,

    /// <summary>Encrypted at rest with the master key, never returned or logged.</summary>
    Secret,
}

/// <summary>A setting that can be stored in the database. Anything not listed here is configuration-only.</summary>
internal sealed class RuntimeSetting(RuntimeSettingSection section, string property, SettingKind? kind = null)
{
    private const int MaxStringLength = 1000;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromDays(3650);

    public RuntimeSettingSection Section { get; } = section;

    public PropertyInfo Property { get; } = section.OptionsType.GetProperty(property)
        ?? throw new InvalidOperationException($"{section.OptionsType.Name} has no property {property}.");

    /// <summary>The configuration key, e.g. <c>Tokens:AccessTokenLifetime</c>.</summary>
    public string Key => $"{Section.ConfigSection}:{Property.Name}";

    /// <summary>The name in the Admin API, e.g. <c>accessTokenLifetime</c>.</summary>
    public string Name => JsonNamingPolicy.CamelCase.ConvertName(Property.Name);

    public SettingKind Kind { get; } = kind ?? KindOf(section.OptionsType.GetProperty(property)!.PropertyType);

    public Type ValueType => Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;

    public IReadOnlyList<string>? EnumValues => Kind == SettingKind.Enum ? System.Enum.GetNames(ValueType) : null;

    /// <summary>Converts a value from the Admin API to its configuration string; <see langword="null"/> resets it.</summary>
    public string? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.Number when Kind == SettingKind.Duration && value.TryGetInt64(out var seconds) && seconds >= 0 && seconds <= MaxDuration.TotalSeconds =>
            Format(TimeSpan.FromSeconds(seconds)),
        JsonValueKind.Number when Kind == SettingKind.Integer && value.TryGetInt32(out var number) => Format(number),
        JsonValueKind.True or JsonValueKind.False when Kind == SettingKind.Boolean => Format(value.GetBoolean()),
        JsonValueKind.String when Kind is SettingKind.String or SettingKind.Enum or SettingKind.File or SettingKind.Secret =>
            Parse(value.GetString()!),
        _ => throw Invalid(Kind switch
        {
            SettingKind.Duration => $"a whole number of seconds between 0 and {(long)MaxDuration.TotalSeconds}",
            SettingKind.Integer => "an integer",
            SettingKind.Boolean => "true or false",
            _ => "a string",
        }),
    };

    /// <summary>
    /// Converts a value as written in configuration (e.g. <c>00:30:00</c>, <c>true</c>, <c>StartTls</c>) to its
    /// normalized configuration string. An empty string resets it.
    /// </summary>
    public string? Parse(string text)
    {
        if (Kind == SettingKind.Secret)
        {
            return text.Length == 0 ? null : text.Length > MaxStringLength ? throw Invalid($"at most {MaxStringLength} characters") : text;
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        switch (Kind)
        {
            case SettingKind.Duration:
                return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span) && span >= TimeSpan.Zero && span <= MaxDuration
                    ? Format(span)
                    : throw Invalid("a duration such as 00:30:00 or 14.00:00:00, at most 3650 days");
            case SettingKind.Integer:
                return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                    ? Format(number)
                    : throw Invalid("an integer");
            case SettingKind.Boolean:
                return bool.TryParse(text, out var flag) ? Format(flag) : throw Invalid("true or false");
            case SettingKind.Enum:
                // Names only; numeric values would bypass the list of allowed values.
                var match = EnumValues!.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                return match ?? throw Invalid($"one of {string.Join(", ", EnumValues!)}");
            default:
                return text.Length > MaxStringLength ? throw Invalid($"at most {MaxStringLength} characters") : text;
        }
    }

    /// <summary>The value of this setting in bound options, as shown in the Admin API.</summary>
    public JsonNode? ToJson(object options)
    {
        var value = Property.GetValue(options);
        return value switch
        {
            null => null,
            _ when Kind == SettingKind.Secret => null,
            TimeSpan span => JsonValue.Create((long)span.TotalSeconds),
            int number => JsonValue.Create(number),
            bool flag => JsonValue.Create(flag),
            System.Enum e => JsonValue.Create(e.ToString()),
            _ => JsonValue.Create(value.ToString()),
        };
    }

    /// <summary>Whether a secret has a value in bound options.</summary>
    public bool HasValue(object options) => Property.GetValue(options) is not (null or "");

    /// <summary>A display form of a configuration string for the CLI and the audit log.</summary>
    public string? Display(string? configValue) => Kind == SettingKind.Secret
        ? configValue is null ? null : "(secret)"
        : configValue;

    private static string Format(TimeSpan span) => span.ToString("c", CultureInfo.InvariantCulture);

    private static string Format(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static string Format(bool flag) => flag ? "true" : "false";

    private AdministrationException Invalid(string expected) => new($"{Key} must be {expected}.");

    private static SettingKind KindOf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(TimeSpan) ? SettingKind.Duration
            : type == typeof(int) ? SettingKind.Integer
            : type == typeof(bool) ? SettingKind.Boolean
            : type.IsEnum ? SettingKind.Enum
            : type == typeof(string) ? SettingKind.String
            : throw new InvalidOperationException($"Unsupported setting type {type}.");
    }
}

/// <summary>A group of settings bound to one options class, e.g. <c>Tokens</c> to <see cref="TokenOptions"/>.</summary>
internal sealed class RuntimeSettingSection
{
    private readonly Func<IConfiguration, object> _bind;
    private readonly Func<object, IServiceProvider, IEnumerable<string>> _validate;

    private RuntimeSettingSection(
        string name, string configSection, Type optionsType, Func<IConfiguration, object> bind, Func<object, IServiceProvider, IEnumerable<string>> validate)
    {
        Name = name;
        ConfigSection = configSection;
        OptionsType = optionsType;
        _bind = bind;
        _validate = validate;
    }

    /// <summary>The name in the Admin API, e.g. <c>passwordPolicy</c>.</summary>
    public string Name { get; }

    public string ConfigSection { get; }

    public Type OptionsType { get; }

    public IReadOnlyList<RuntimeSetting> Settings { get; private set; } = [];

    public static RuntimeSettingSection Create<T>(string name, string configSection, params (string Property, SettingKind? Kind)[] settings)
        where T : class, new()
    {
        var section = new RuntimeSettingSection(
            name,
            configSection,
            typeof(T),
            configuration => configuration.GetSection(configSection).Get<T>() ?? new T(),
            (options, services) => services.GetServices<IValidateOptions<T>>()
                .Select(v => v.Validate(Options.DefaultName, (T)options))
                .Where(r => r.Failed)
                .SelectMany(r => r.Failures ?? []));
        section.Settings = [.. settings.Select(s => new RuntimeSetting(section, s.Property, s.Kind))];
        return section;
    }

    /// <summary>The options as bound from <paramref name="configuration"/>, without validation.</summary>
    public object Bind(IConfiguration configuration) => _bind(configuration);

    /// <summary>The options' defaults, as if nothing were configured.</summary>
    public object Defaults() => Activator.CreateInstance(OptionsType)!;

    /// <summary>Binds and validates the section with the registered <see cref="IValidateOptions{TOptions}"/>.</summary>
    public IEnumerable<string> Validate(IConfiguration configuration, IServiceProvider services)
    {
        object options;
        try
        {
            options = Bind(configuration);
        }
        catch (InvalidOperationException ex)
        {
            return [$"{ConfigSection}: {ex.Message}"];
        }

        return _validate(options, services);
    }
}

/// <summary>
/// The settings that can be edited at runtime and stored in the database (PROJECT.md §10). Values from appsettings,
/// environment variables, or the command line take precedence and are shown as locked.
/// </summary>
internal static class RuntimeSettings
{
    public static readonly IReadOnlyList<RuntimeSettingSection> Sections =
    [
        RuntimeSettingSection.Create<TokenOptions>("tokens", TokenOptions.SectionName,
            (nameof(TokenOptions.AccessTokenLifetime), null),
            (nameof(TokenOptions.IdTokenLifetime), null),
            (nameof(TokenOptions.RefreshTokenSlidingLifetime), null),
            (nameof(TokenOptions.RefreshTokenAbsoluteLifetime), null)),
        RuntimeSettingSection.Create<SsoSessionOptions>("session", SsoSessionOptions.SectionName,
            (nameof(SsoSessionOptions.Lifetime), null)),
        RuntimeSettingSection.Create<PasswordPolicyOptions>("passwordPolicy", PasswordPolicyOptions.SectionName,
            (nameof(PasswordPolicyOptions.MinimumLength), null),
            (nameof(PasswordPolicyOptions.MaximumLength), null),
            (nameof(PasswordPolicyOptions.RequireUppercase), null),
            (nameof(PasswordPolicyOptions.RequireLowercase), null),
            (nameof(PasswordPolicyOptions.RequireDigit), null),
            (nameof(PasswordPolicyOptions.RequireNonAlphanumeric), null),
            (nameof(PasswordPolicyOptions.CheckBreachedPasswords), null)),
        RuntimeSettingSection.Create<LockoutOptions>("lockout", LockoutOptions.SectionName,
            (nameof(LockoutOptions.MaxFailedAttempts), null),
            (nameof(LockoutOptions.Duration), null)),
        RuntimeSettingSection.Create<TwoFactorOptions>("twoFactor", TwoFactorOptions.SectionName,
            (nameof(TwoFactorOptions.Required), null)),
        RuntimeSettingSection.Create<AdminApiOptions>("adminApi", AdminApiOptions.SectionName,
            (nameof(AdminApiOptions.RequireTwoFactor), null)),
        RuntimeSettingSection.Create<EmailOptions>("email", EmailOptions.SectionName,
            (nameof(EmailOptions.VerificationAtSignIn), null),
            (nameof(EmailOptions.PasswordResetLifetime), null),
            (nameof(EmailOptions.VerificationLifetime), null)),
        RuntimeSettingSection.Create<SmtpOptions>("smtp", SmtpOptions.SectionName,
            (nameof(SmtpOptions.Host), null),
            (nameof(SmtpOptions.Port), null),
            (nameof(SmtpOptions.Security), null),
            (nameof(SmtpOptions.UserName), null),
            (nameof(SmtpOptions.Password), SettingKind.Secret),
            (nameof(SmtpOptions.FromAddress), null),
            (nameof(SmtpOptions.FromName), null)),
        RuntimeSettingSection.Create<BrandingOptions>("branding", BrandingOptions.SectionName,
            (nameof(BrandingOptions.ProductName), null),
            (nameof(BrandingOptions.PrimaryColor), null),
            (nameof(BrandingOptions.BackgroundColor), null),
            (nameof(BrandingOptions.SurfaceColor), null),
            (nameof(BrandingOptions.TextColor), null),
            (nameof(BrandingOptions.DarkPrimaryColor), null),
            (nameof(BrandingOptions.DarkBackgroundColor), null),
            (nameof(BrandingOptions.DarkSurfaceColor), null),
            (nameof(BrandingOptions.DarkTextColor), null),
            (nameof(BrandingOptions.Logo), SettingKind.File),
            (nameof(BrandingOptions.BackgroundImage), SettingKind.File),
            (nameof(BrandingOptions.DarkBackgroundImage), SettingKind.File),
            (nameof(BrandingOptions.CustomCss), SettingKind.File)),
        RuntimeSettingSection.Create<AuditLogOptions>("auditLog", AuditLogOptions.SectionName,
            (nameof(AuditLogOptions.Retention), null)),
    ];

    public static readonly IReadOnlyList<RuntimeSetting> All = [.. Sections.SelectMany(s => s.Settings)];

    /// <summary>The <see cref="Core.Security.SecretProtector"/> purpose of a secret setting, bound to its key.</summary>
    public static string SecretPurpose(RuntimeSetting setting) => $"ValkyrieID.Setting:{setting.Key}";

    private static readonly Dictionary<string, RuntimeSetting> ByKey = All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    public static RuntimeSetting? Find(string key) => ByKey.GetValueOrDefault(key);

    public static RuntimeSettingSection? FindSection(string name) =>
        Sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.ConfigSection, name, StringComparison.OrdinalIgnoreCase));
}
