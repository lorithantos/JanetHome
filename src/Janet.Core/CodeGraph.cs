using System.Diagnostics;

namespace Janet.Core;

/// <summary>Where a repository's code graph stands relative to its source.</summary>
public sealed record GraphState
{
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
/// The convention is owned by the REPOSITORY and merely honoured here: a .graph directory holds
/// the generated graph and scripts\graph.ps1 regenerates it. Keeping the solution path, the
/// output path and the analyzer resolution in the repo's own script is what keeps this
/// repo-agnostic.
///
/// The check loop is exactly what would otherwise bypass a repo's build script and leave an
/// agent querying a stale graph, which is why refreshing belongs here at all. Staleness is
/// reported even where it cannot be fixed: a confidently stale graph is worse than none.
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
    public static GraphState? Read(string? repositoryRoot)
    {
        if (string.IsNullOrEmpty(repositoryRoot))
        {
            return null;
        }

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
    /// Delegates to the repository's own graph script and reports what happened.
    /// </summary>
    /// <remarks>
    /// Never throws and never fails the check: a graph is a convenience, and the repo script
    /// already treats a missing analyzer as a skip. This mirrors that posture rather than
    /// second-guessing it.
    /// </remarks>
    public static void Refresh(string repositoryRoot, GraphState state)
    {
        string refreshScript = System.IO.Path.Combine(repositoryRoot, "scripts", "graph.ps1");

        try
        {
            ProcessStartInfo info = new()
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = repositoryRoot,
            };

            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-File");
            info.ArgumentList.Add(refreshScript);
            info.ArgumentList.Add("-Quiet");

            using Process? process = Process.Start(info);
            if (process is null)
            {
                state.Status = "failed";
                return;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
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
