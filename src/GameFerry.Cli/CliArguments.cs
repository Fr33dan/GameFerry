namespace GameFerry.Cli;

/// <summary>Positional arguments plus <c>--option value</c> pairs and bare <c>--flag</c> switches.</summary>
internal sealed class CliArguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    /// <param name="flags">Option names that take no value.</param>
    public static CliArguments Parse(IEnumerable<string> args, params string[] flags)
    {
        CliArguments parsed = new();
        using IEnumerator<string> enumerator = args.GetEnumerator();
        while (enumerator.MoveNext())
        {
            string arg = enumerator.Current;
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                parsed.Positional.Add(arg);
                continue;
            }

            string name = arg[2..];
            if (flags.Contains(name, StringComparer.OrdinalIgnoreCase))
                parsed._options[name] = null;
            else if (enumerator.MoveNext())
                parsed._options[name] = enumerator.Current;
            else
                throw new UsageException($"Option --{name} needs a value.");
        }

        return parsed;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public string Require(string name) =>
        Get(name) ?? throw new UsageException($"Missing required option --{name}.");

    /// <summary>Fails if an option was given that the command doesn't know.</summary>
    public void AllowOnly(params string[] names)
    {
        string? unknown = _options.Keys.FirstOrDefault(key => !names.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
            throw new UsageException($"Unknown option --{unknown}.");
    }
}

internal sealed class UsageException(string message) : Exception(message);
