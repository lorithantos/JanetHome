namespace Janet.Cli;

/// <summary>
/// Minimal option parser.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taking a dependency: the surface is a dozen options and the tool
/// has to be trivially installable. Repeated options accumulate -- <c>--tag a --tag b</c> -- so
/// the caller never has to know a delimiter, which is exactly the trap that cost two rounds
/// of debugging in the parity harness.
/// </remarks>
public sealed class Args
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Positional { get; }

    private Args(IReadOnlyList<string> positional) => Positional = positional;

    /// <summary>Options that take no value.</summary>
    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase)
    {
        "--all", "--expand", "--full", "--text", "--pretty", "--append", "--dry-run", "--http",
        "--help", "--no-trace",

        // Thread items.
        "--active", "--none", "--append-notes", "--append-refs",
    };

    public static Args Parse(IReadOnlyList<string> argv)
    {
        List<string> positional = [];
        Args parsed = new(positional);

        for (int i = 0; i < argv.Count; i++)
        {
            string arg = argv[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            // --name=value is accepted alongside --name value; both appear in the wild and
            // guessing wrong at a shell prompt is a needless round trip.
            int equals = arg.IndexOf('=');
            if (equals > 0)
            {
                parsed.AddValue(arg[..equals], arg[(equals + 1)..]);
                continue;
            }

            if (Switches.Contains(arg))
            {
                parsed._flags.Add(arg);
                continue;
            }

            if (i + 1 >= argv.Count)
            {
                throw new ArgumentException($"{arg} needs a value");
            }

            parsed.AddValue(arg, argv[++i]);
        }

        return parsed;
    }

    private void AddValue(string name, string value)
    {
        if (!_values.TryGetValue(name, out List<string>? list))
        {
            list = [];
            _values[name] = list;
        }

        list.Add(value);
    }

    public bool Has(string name) => _flags.Contains(name) || _values.ContainsKey(name);

    public bool Flag(string name) => _flags.Contains(name);

    public string? Value(string name) => _values.TryGetValue(name, out List<string>? list) ? list[^1] : null;

    public IReadOnlyList<string> Values(string name) =>
        _values.TryGetValue(name, out List<string>? list) ? list : [];

    public int Int(string name, int fallback) =>
        Value(name) is string raw && int.TryParse(raw, out int value) ? value : fallback;
}
