using ValkyrieID.Server.Administration;
using ValkyrieID.Server.Settings;

namespace ValkyrieID.Server.Cli;

/// <summary>Shows and changes the runtime settings stored in the database (PROJECT.md §10).</summary>
internal sealed class SettingsCommands(IServiceProvider services, TextReader input, TextWriter output)
{
    private readonly RuntimeSettingsStore _store = services.GetRequiredService<RuntimeSettingsStore>();
    private readonly SettingsAdministration _settings = services.GetRequiredService<SettingsAdministration>();
    private readonly IConfiguration _configuration = services.GetRequiredService<IConfiguration>();

    public Task RunAsync(CliArguments args, CancellationToken cancellationToken) => args.Argument(1, "subcommand") switch
    {
        "show" => ShowAsync(args),
        "set" => SetAsync(args, cancellationToken),
        "reset" => ResetAsync(args, cancellationToken),
        var other => throw new CliException($"Unknown settings command '{other}'."),
    };

    private async Task ShowAsync(CliArguments args)
    {
        var name = args.Positional.Count > 2 ? args.Positional[2] : null;
        args.EnsureConsumed(name is null ? 2 : 3);
        IEnumerable<RuntimeSettingSection> sections = name is null
            ? RuntimeSettings.Sections
            : [RuntimeSettings.FindSection(name) ?? throw new CliException($"There is no settings section '{name}'.")];
        foreach (var section in sections)
        {
            var options = section.Bind(_configuration);
            foreach (var setting in section.Settings)
            {
                var value = setting.Kind == SettingKind.Secret
                    ? setting.HasValue(options) ? "(set)" : "(not set)"
                    : setting.ToJson(options)?.ToJsonString() ?? "(none)";
                await output.WriteLineAsync($"{setting.Key} = {value}  [{_store.SourceOf(setting)}]");
            }
        }
    }

    /// <summary>
    /// <c>settings set &lt;key&gt; &lt;value&gt;</c>, with values written as in configuration (e.g. <c>00:30:00</c>).
    /// Secrets aren't accepted as arguments; they are read like passwords.
    /// </summary>
    private async Task SetAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var setting = Find(args.Argument(2, "key"));
        string text;
        if (setting.Kind == SettingKind.Secret)
        {
            args.EnsureConsumed(3);
            text = await PasswordReader.ReadSecretAsync(input, $"{setting.Key}: ");
        }
        else
        {
            text = args.Argument(3, "value");
            args.EnsureConsumed(4);
        }

        var value = Run(() => setting.Parse(text));
        await ApplyAsync(new Dictionary<RuntimeSetting, string?> { [setting] = value }, cancellationToken);
    }

    /// <summary><c>settings reset &lt;key|section&gt;</c>: removes the stored values, so the defaults apply.</summary>
    private async Task ResetAsync(CliArguments args, CancellationToken cancellationToken)
    {
        var name = args.Argument(2, "key or section");
        args.EnsureConsumed(3);
        var settings = RuntimeSettings.FindSection(name) is { } section
            ? section.Settings.Where(s => !_store.IsLocked(s))
            : [Find(name)];
        await ApplyAsync(settings.ToDictionary(s => s, _ => (string?)null), cancellationToken);
    }

    private async Task ApplyAsync(Dictionary<RuntimeSetting, string?> changes, CancellationToken cancellationToken)
    {
        var changed = await UserCommands.Run(() => _settings.UpdateAsync(changes, UserCommands.Actor, cancellationToken));
        if (changed.Count == 0)
        {
            await output.WriteLineAsync("Nothing changed.");
            return;
        }

        foreach (var change in changed)
        {
            await output.WriteLineAsync($"{change.Key}: {change.OldValue ?? "(default)"} -> {change.NewValue ?? "(default)"}");
        }

        await output.WriteLineAsync("Running servers apply the change within Settings:ReloadInterval.");
    }

    private static RuntimeSetting Find(string key) =>
        RuntimeSettings.Find(key) ?? throw new CliException($"'{key}' isn't a runtime setting. Run `settings show` for the list.");

    private static T Run<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (AdministrationException ex)
        {
            throw new CliException(ex.Message);
        }
    }
}
