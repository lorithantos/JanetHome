namespace Janet.Core;

/// <summary>
/// Resolves which graph file to operate on.
/// </summary>
/// <remarks>
/// The server is an accessor, not a store: the graph stays on disk in the user's JanetBase,
/// git-tracked, where the existing scripts and every diff can still see it.
/// </remarks>
public static class GraphLocator
{
    public const string LiveGraph = "research.json";

    /// <summary>The port's write target until cutover. See Swap-ResearchGraph.ps1.</summary>
    public const string CandidateGraph = "research.candidate.json";

    /// <summary>
    /// A port derived from the JanetBase, so one install always resolves to the same port and
    /// two different bases never collide.
    /// </summary>
    /// <remarks>
    /// An HTTP server is a service, not a per-session child process: every session should dial
    /// the SAME server rather than each starting its own. That only works if the port is
    /// predictable, because client config has to name it without coordinating with anyone --
    /// the same "resolve it by lookup" idea as the current junction, applied to the address
    /// instead of the path. A fixed default would instead make two repos fight over one port.
    ///
    /// Keyed on the BASE, deliberately, not on the graph file. Keying it on the graph looks
    /// more precise and is actively wrong: the cutover renames research.candidate.json to
    /// research.json, so the port would move at the exact moment sessions are attached, and
    /// every client would be left dialling an address nothing answers on -- with no error, just
    /// a server that stopped existing. The base survives the swap; the filename does not.
    ///
    /// Stability is the whole contract here. A derived port only helps if the derivation is
    /// pure and its input never moves under a running client.
    ///
    /// FNV-1a over the normalised base, mapped into 20000-29999.
    ///
    /// Deliberately NOT the IANA dynamic/private range (49152-65535): that is exactly what
    /// Windows hands out as ephemeral ports for outbound connections -- `netsh int ipv4 show
    /// dynamicport tcp` reports start 49152, count 16384 -- so a port derived into it can be
    /// taken by any unrelated outbound socket between one start and the next, and Hyper-V
    /// reserves chunks of it besides. A range meant for transient sockets is the wrong place
    /// to put a stable address.
    /// </remarks>
    public static int DerivePort(string basePath)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;

        string normalised = basePath.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

        uint hash = offset;
        foreach (char c in normalised)
        {
            hash = (hash ^ c) * prime;
        }

        return 20000 + (int)(hash % 10000);
    }

    /// <summary>
    /// Resolves a graph path from an explicit override, a base directory, or JanetBase.
    /// </summary>
    /// <param name="graph">
    /// An explicit path (used as-is when rooted), or a bare file name resolved against the base.
    /// </param>
    /// <param name="basePath">The JanetBase directory. Falls back to $env:JanetBase / $JANET_BASE.</param>
    /// <remarks>
    /// With no explicit graph, the candidate wins while it exists, and only then the live file.
    /// That inversion is deliberate and temporary: for the duration of the port the C# writes
    /// the candidate and the PowerShell writes the live graph, and a default pointing at the
    /// live file would mean one forgotten flag silently merges the two -- destroying the
    /// separation the cutover's delta computation depends on. Targeting the live graph is
    /// still possible, but has to be said out loud: --graph research.json.
    /// After the swap the candidate no longer exists and this resolves to the live file again,
    /// so the special case retires itself.
    /// </remarks>
    public static string Resolve(string? graph = null, string? basePath = null)
    {
        if (!string.IsNullOrWhiteSpace(graph) && Path.IsPathRooted(graph))
        {
            return Path.GetFullPath(graph);
        }

        string? root = basePath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetEnvironmentVariable("JanetBase")
                ?? Environment.GetEnvironmentVariable("JANET_BASE");
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            throw new GraphException(
                "No JanetBase. Pass --base <dir>, or set the JanetBase (Windows) / JANET_BASE " +
                "environment variable. Janet never assumes $HOME or any system path.");
        }

        if (!Directory.Exists(root))
        {
            throw new GraphException($"JanetBase does not exist: {root}");
        }

        if (!string.IsNullOrWhiteSpace(graph))
        {
            return Path.GetFullPath(Path.Combine(root, graph));
        }

        string candidate = Path.Combine(root, CandidateGraph);
        return Path.GetFullPath(File.Exists(candidate) ? candidate : Path.Combine(root, LiveGraph));
    }
}
