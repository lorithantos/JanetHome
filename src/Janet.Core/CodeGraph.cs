using System.Diagnostics;

namespace Janet.Core;

/// <summary>Where a repository's code graph stands relative to its source.</summary>
public sealed record GraphState
{
    /// <summary>
    /// Which convention answered: "script" (a .graph directory refreshed by scripts\graph.ps1) or
    /// "razorgraph" (a graph held by the RazorGraph MCP server the repository's .mcp.json declares).
    /// </summary>
    public string Via { get; init; } = "script";

    /// <summary>The server-side id of the graph. Razorgraph only.</summary>
    public string? GraphId { get; init; }

    /// <summary>The graph file (script), or the solution the server graphed (razorgraph).</summary>
    public string? Path { get; set; }

    public string? BuiltAt { get; set; }
    public string? NewestSourceAt { get; init; }
    public string Status { get; set; } = "absent";
    public bool Refreshed { get; set; }
    public bool CanRefresh { get; init; }
}

/// <summary>
/// Reports, and where it can, refreshes the repository's code graph.
/// </summary>
/// <remarks>
/// Two conventions, both owned by the REPOSITORY and merely honoured here. The original: a .graph
/// directory holds the generated graph and scripts\graph.ps1 regenerates it. The second, since
/// 2026-09-01: the repository's .mcp.json declares a razorgraph HTTP server, and the graph lives in
/// that server's registry rather than in a file. RetirementCore moved to the second on 2026-08-29
/// and deleted the first, after which nothing rebuilt its graph across a day of edits -- the server
/// reports per-node staleness, but no caller asked for build_solution again, and the session
/// drifted from graph queries back to grep. That is exactly the failure this class exists to
/// prevent, one convention over.
///
/// The check loop is exactly what would otherwise bypass a repo's build script and leave an agent
/// querying a stale graph, which is why refreshing belongs here at all. Staleness is reported even
/// where it cannot be fixed: a confidently stale graph is worse than none.
/// </remarks>
public static class CodeGraph
{
    /// <summary>
    /// The graph belongs to a repository, not to a project file: walks up from the build target
    /// until a .git directory appears.
    /// </summary>
    public static string? FindRepositoryRoot(string startPath)
    {
        string? current = System.IO.Path.GetDirectoryName(startPath);

        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(System.IO.Path.Combine(current, ".git")))
            {
                return current;
            }

            string? parent = System.IO.Path.GetDirectoryName(current);
            if (parent == current)
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    /// <summary>
    /// Null when the repository has no graph convention at all -- most of them -- so the field
    /// reads as "not applicable" rather than "missing", the same way tests does.
    /// </summary>
    /// <param name="target">The file being built; among several served graphs, the one built from it wins.</param>
    public static GraphState? Read(string? repositoryRoot, string? target = null)
    {
        if (string.IsNullOrEmpty(repositoryRoot))
        {
            return null;
        }

        return ReadScripted(repositoryRoot) ?? ReadServed(repositoryRoot, target);
    }

    private static GraphState? ReadScripted(string repositoryRoot)
    {
        string graphDirectory = System.IO.Path.Combine(repositoryRoot, ".graph");
        string refreshScript = System.IO.Path.Combine(repositoryRoot, "scripts", "graph.ps1");
        bool hasDirectory = Directory.Exists(graphDirectory);
        bool hasScript = File.Exists(refreshScript);

        if (!hasDirectory && !hasScript)
        {
            return null;
        }

        FileInfo? graphFile = hasDirectory ? LargestGraph(graphDirectory) : null;
        DateTime? newest = NewestSourceWrite(repositoryRoot);

        GraphState state = new()
        {
            Via = "script",
            Path = graphFile?.FullName,
            BuiltAt = graphFile?.LastWriteTimeUtc.ToString("o"),
            NewestSourceAt = newest?.ToString("o"),
            CanRefresh = hasScript,
        };

        if (graphFile is not null)
        {
            bool outrun = newest is not null && newest > graphFile.LastWriteTimeUtc;
            state.Status = outrun ? "stale" : "current";
        }

        return state;
    }

    /// <summary>
    /// The served convention. A graph is matched by SOURCE: an entry whose solution or project
    /// lies under this repository.
    /// </summary>
    /// <remarks>
    /// Nothing is built that nobody asked for. An absent graph stays absent with canRefresh
    /// false, because a solution graph compiles every project and a session decides when to pay
    /// for one; only a graph that exists and has been outrun is rebuilt. A server that does not
    /// answer reads the same way -- absent, not refreshable -- rather than as an error, because a
    /// graph is a convenience the check must not fail over.
    /// </remarks>
    private static GraphState? ReadServed(string repositoryRoot, string? target)
    {
        RazorGraphClient? client = RazorGraphClient.FromRepository(repositoryRoot);
        if (client is null)
        {
            return null;
        }

        DateTime? newest = NewestSourceWrite(repositoryRoot);

        ServedGraph? mine = client.TryListGraphs()?
            .Where(g => IsUnder(g.Source, repositoryRoot))
            .OrderByDescending(g => target is not null && PathsEqual(g.Source, target))
            .ThenByDescending(g => g.LoadedAt)
            .FirstOrDefault();

        if (mine is null)
        {
            return new GraphState
            {
                Via = "razorgraph",
                NewestSourceAt = newest?.ToString("o"),
                Status = "absent",
                CanRefresh = false,
            };
        }

        bool outrun = newest is not null && newest > mine.LoadedAt.UtcDateTime;

        return new GraphState
        {
            Via = "razorgraph",
            GraphId = mine.Id,
            Path = mine.Source,
            BuiltAt = mine.LoadedAt.UtcDateTime.ToString("o"),
            NewestSourceAt = newest?.ToString("o"),
            Status = outrun ? "stale" : "current",
            CanRefresh = RazorGraphClient.CanRebuild(mine.Source),
        };
    }

    private static bool IsUnder(string path, string root)
    {
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        string prefix = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;

        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(System.IO.Path.GetFullPath(left), System.IO.Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static FileInfo? LargestGraph(string graphDirectory) =>
        new DirectoryInfo(graphDirectory).GetFiles("*.json")
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();

    /// <summary>
    /// Newest write across the sources a code graph would analyze. Build output is excluded:
    /// obj\ regenerates on every build and would make every graph look stale the moment it was
    /// written.
    /// </summary>
    private static DateTime? NewestSourceWrite(string repositoryRoot)
    {
        string[] extensions = [".cs", ".xaml", ".razor", ".cshtml"];
        DateTime? newest = null;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(repositoryRoot, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (string file in files)
        {
            if (!extensions.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsExcluded(file))
            {
                continue;
            }

            DateTime written;
            try
            {
                written = File.GetLastWriteTimeUtc(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (newest is null || written > newest)
            {
                newest = written;
            }
        }

        return newest;
    }

    private static bool IsExcluded(string file)
    {
        string separated = $"{System.IO.Path.DirectorySeparatorChar}";

        foreach (string directory in (string[])["bin", "obj", ".git", ".graph"])
        {
            if (file.Contains($"{separated}{directory}{separated}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Refreshes the graph through whichever convention reported it, and records what happened.
    /// </summary>
    /// <remarks>
    /// Never throws and never fails the check: a graph is a convenience, and the repo script
    /// already treats a missing analyzer as a skip. This mirrors that posture rather than
    /// second-guessing it.
    /// </remarks>
    public static void Refresh(string repositoryRoot, GraphState state)
    {
        if (state.Via == "razorgraph")
        {
            RefreshServed(repositoryRoot, state);
            return;
        }

        RefreshScripted(repositoryRoot, state);
    }

    private static void RefreshServed(string repositoryRoot, GraphState state)
    {
        RazorGraphClient? client = RazorGraphClient.FromRepository(repositoryRoot);
        if (client is null || state.GraphId is null || state.Path is null)
        {
            state.Status = "failed";
            return;
        }

        DateTimeOffset? rebuilt = client.TryRebuild(state.GraphId, state.Path);
        if (rebuilt is null)
        {
            state.Status = "failed";
            return;
        }

        state.BuiltAt = rebuilt.Value.UtcDateTime.ToString("o");
        state.Status = "current";
        state.Refreshed = true;
    }

    private static void RefreshScripted(string repositoryRoot, GraphState state)
    {
        string refreshScript = System.IO.Path.Combine(repositoryRoot, "scripts", "graph.ps1");

        try
        {
            ProcessStartInfo info = new()
            {
                FileName = "pwsh",
                WorkingDirectory = repositoryRoot,
            };

            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-File");
            info.ArgumentList.Add(refreshScript);
            info.ArgumentList.Add("-Quiet");

            // Output is drained, not read: the script's verdict is the graph file it leaves behind.
            ProcessOutput.Capture(info, CancellationToken.None);
        }
        catch (Exception ex) when (ex is GraphException or IOException or UnauthorizedAccessException)
        {
            state.Status = "failed";
            return;
        }

        FileInfo? graphFile = LargestGraph(System.IO.Path.Combine(repositoryRoot, ".graph"));
        if (graphFile is null)
        {
            state.Status = "failed";
            return;
        }

        state.Path = graphFile.FullName;
        state.BuiltAt = graphFile.LastWriteTimeUtc.ToString("o");
        state.Status = "current";
        state.Refreshed = true;
    }
}
