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

        // Thread items. --no-lead is the reporter's: drop notesLead, keep notesLength.
        "--active", "--none", "--append-notes", "--append-refs", "--no-lead",

        // API and assembly introspection. --compact is the opposite of --pretty and exists
        // because the two scripts disagreed: Get-ApiDoc printed compressed JSON and
        // Get-AssemblyApi printed indented, and both defaults are kept rather than unified.
        "--inherited", "--static", "--compact",

        // The build check.
        "--no-tests", "--new", "--full", "--no-graph",

        // Azure tokens. --raw is the opt-in that lets the token itself out; without it the
        // answer is metadata, so forgetting the flag costs a re-run rather than a leaked secret.
        "--raw", "--refresh",

        // Borrowing a running server's cache. Both are opt-OUT: the useful default is to use a
        // server that is already there, and both of these exist for callers who need to know
        // that this process did the work -- a test, or a hook that must not spawn anything.
        "--local", "--no-launch", "--why",
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
