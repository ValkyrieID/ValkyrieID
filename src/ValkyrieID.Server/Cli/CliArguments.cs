namespace ValkyrieID.Server.Cli;

/// <summary>Parsed command-line arguments: positional values plus repeatable <c>--name value</c> options.</summary>
internal sealed class CliArguments
{
    /// <summary>Options that take no value.</summary>
    private static readonly HashSet<string> FlagNames = new(StringComparer.Ordinal) { "check" };

    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    private CliArguments(List<string> positional) => Positional = positional;

    public IReadOnlyList<string> Positional { get; }

    public static CliArguments Parse(IEnumerable<string> args)
    {
        List<string> positional = [];
        var parsed = new CliArguments(positional);
        using var enumerator = args.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var arg = enumerator.Current;
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                if (FlagNames.Contains(arg[2..]))
                {
                    parsed._flags.Add(arg[2..]);
                    continue;
                }

                if (!enumerator.MoveNext())
                {
                    throw new CliException($"Option {arg} requires a value.");
                }

                var name = arg[2..];
                if (!parsed._options.TryGetValue(name, out var values))
                {
                    parsed._options[name] = values = [];
                }

                values.Add(enumerator.Current);
            }
            else
            {
                positional.Add(arg);
            }
        }

        return parsed;
    }

    public string Argument(int index, string name) =>
        index < Positional.Count ? Positional[index] : throw new CliException($"Missing argument <{name}>.");

    public IReadOnlyList<string> Many(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var values) ? values : [];
    }

    public string? Optional(string name)
    {
        var values = Many(name);
        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => throw new CliException($"Option --{name} can only be given once."),
        };
    }

    public int? OptionalInt(string name)
    {
        var value = Optional(name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, out var number) && number > 0
            ? number
            : throw new CliException($"Option --{name} must be a positive integer.");
    }

    public bool Flag(string name)
    {
        _used.Add(name);
        return _flags.Contains(name);
    }

    /// <summary>Fails on options a command didn't read and on extra positional arguments.</summary>
    public void EnsureConsumed(int positionalCount)
    {
        var unknown = _options.Keys.Concat(_flags).FirstOrDefault(k => !_used.Contains(k));
        if (unknown is not null)
        {
            throw new CliException($"Unknown option --{unknown}.");
        }

        if (Positional.Count > positionalCount)
        {
            throw new CliException($"Unexpected argument '{Positional[positionalCount]}'.");
        }
    }
}

internal sealed class CliException(string message) : Exception(message);
