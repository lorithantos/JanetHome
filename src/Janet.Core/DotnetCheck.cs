using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Janet.Core;

/// <summary>What to build and test.</summary>
public sealed record CheckRequest
{
    public string Target { get; init; } = ".";
    public string Configuration { get; init; } = "Debug";
    public bool NoTests { get; init; }
    public string? TestFilter { get; init; }

    /// <summary>Diff the warning census against the previous -New run. Forces a full rebuild.</summary>
    public bool New { get; init; }

    /// <summary>Rebuild everything without the baseline machinery.</summary>
    public bool Full { get; init; }

    public bool NoGraph { get; init; }
}

/// <summary>Where the baseline lives and what this run did with it. Null unless -New.</summary>
public sealed record BaselineReport(string Path, string? ComparedTo, bool Saved);

/// <summary>The build half of the answer.</summary>
public sealed record BuildReport(
    bool Succeeded,
    double DurationSeconds,
    IReadOnlyList<Diagnostic> Errors,
    IReadOnlyList<WarningGroup> Warnings,
    int WarningCount,
    IReadOnlyList<Diagnostic>? NewWarnings,
    int? ResolvedWarningCount,
    BaselineReport? Baseline);

/// <summary>A finished check.</summary>
public sealed record CheckResult(
    string Target,
    string Configuration,
    bool Succeeded,
    BuildReport Build,
    TestRun? Tests,
    GraphState? Graph);

/// <summary>A check still running, and the handle to poll it with.</summary>
public sealed record CheckPending(
    string Target,
    string Configuration,
    string Handle,
    string StartedAt,
    double ElapsedSeconds);

/// <summary>
/// Builds and tests a .NET target and reports it as a structured answer rather than console
/// scrollback.
/// </summary>
/// <remarks>
/// dotnet's own output buries the payload: one real warning drowns in fifteen restore warnings,
/// and a failed test's assert message takes three re-runs with different console filters to
/// extract. This runs the build and the tests once and returns what a session actually needs.
/// </remarks>
public static class DotnetCheck
{
    /// <summary>
    /// A .sln/.slnx/.csproj, or a directory holding exactly one.
    /// </summary>
    /// <remarks>
    /// A directory holding several is refused with all of them named rather than resolved to a
    /// first match: building the wrong project reports a green that means nothing.
    /// </remarks>
    public static string ResolveTarget(string given)
    {
        string full = Path.GetFullPath(given);

        if (File.Exists(full))
        {
            return full;
        }

        if (!Directory.Exists(full))
        {
            throw new GraphException($"Build target not found: {given}");
        }

        string[] candidates =
        [
            .. new DirectoryInfo(full).GetFiles()
                .Where(f => f.Extension is ".sln" or ".slnx" or ".csproj")
                .Select(f => f.FullName)
                .Order(StringComparer.OrdinalIgnoreCase)
        ];

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        string names = string.Join(", ", candidates.Select(Path.GetFileName));

        throw new GraphException(
            $"Target directory holds {candidates.Length} buildable files ({names}) -- name one explicitly.");
    }

    public static CheckResult Run(CheckRequest request) => Run(request, CancellationToken.None);

    public static CheckResult Run(CheckRequest request, CancellationToken cancellation)
    {
        string target = ResolveTarget(request.Target);

        List<string> arguments = [target, "--configuration", request.Configuration, "-nologo"];

        // A diff is only meaningful against a complete census. --no-incremental recompiles
        // everything so the CSxxxx come back, and --force re-runs restore, because NUxxxx are
        // replayed only by a real restore and a baseline missing them would report every ancient
        // restore warning as new on the next real one.
        //
        // -Full asks for the same rebuild without the baseline machinery: "recompile everything"
        // and "diff the census" are separate wants, and -New was the only way to get the first.
        if (request.New || request.Full)
        {
            arguments.Add("--no-incremental");
            arguments.Add("--force");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        (int exitCode, IReadOnlyList<string> lines) = RunDotnet("build", arguments, cancellation);
        stopwatch.Stop();

        bool buildSucceeded = exitCode == 0;

        IReadOnlyList<Diagnostic> diagnostics = DotnetDiagnostics.Read(lines);
        List<Diagnostic> errors = [.. diagnostics.Where(d => d.Severity == "error")];
        List<Diagnostic> warnings = [.. diagnostics.Where(d => d.Severity == "warning")];

        IReadOnlyList<Diagnostic>? newWarnings = null;
        int? resolvedWarningCount = null;
        BaselineReport? baselineReport = null;

        if (request.New)
        {
            string baselinePath = DotnetDiagnostics.BaselinePath(target, request.Configuration);
            WarningBaseline? prior = DotnetDiagnostics.ReadBaseline(baselinePath, DotnetDiagnostics.BaselineContract);

            if (prior is not null)
            {
                BaselineDiff diff = DotnetDiagnostics.Compare(warnings, prior);
                newWarnings = diff.NewWarnings;
                resolvedWarningCount = diff.ResolvedWarningCount;
            }

            // Only a successful build overwrites it: a failed build is a partial census, and
            // saving one would report every warning it never reached as resolved.
            if (buildSucceeded)
            {
                DotnetDiagnostics.SaveBaseline(
                    baselinePath,
                    DotnetDiagnostics.BaselineContract,
                    target,
                    request.Configuration,
                    warnings,
                    DateTime.UtcNow.ToString("o"));
            }

            baselineReport = new BaselineReport(baselinePath, prior?.SavedAt, buildSucceeded);
        }

        BuildReport build = new(
            buildSucceeded,
            Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
            errors,
            DotnetDiagnostics.Group(warnings),
            warnings.Count,
            newWarnings,
            resolvedWarningCount,
            baselineReport);

        TestRun? tests = null;
        if (buildSucceeded && !request.NoTests)
        {
            tests = RunTests(target, request, cancellation);
        }

        // The graph describes source structure, so a failed build is the one state worth
        // refusing to graph: the analyzer would either fail too or record a tree that never
        // compiled. Refreshing only when stale keeps a tight edit loop from paying for a graph
        // nothing changed.
        string? repositoryRoot = CodeGraph.FindRepositoryRoot(target);
        GraphState? graph = CodeGraph.Read(repositoryRoot, target);

        bool refreshWanted = graph is not null
            && !request.NoGraph
            && buildSucceeded
            && graph.CanRefresh
            && graph.Status != "current";

        if (refreshWanted && repositoryRoot is not null)
        {
            CodeGraph.Refresh(repositoryRoot, graph!);
        }

        bool succeeded = buildSucceeded && (request.NoTests || (tests is not null && tests.Succeeded));

        return new CheckResult(target, request.Configuration, succeeded, build, tests, graph);
    }

    /// <summary>How many run directories to keep, and how long one is protected from pruning
    /// whatever its age rank. The second number is what makes this safe with concurrent
    /// sessions: four repos share this machine, and a run still writing its TRX must not be
    /// deleted by another session that started later.</summary>
    private const int KeptRunDirectories = 5;

    private static readonly TimeSpan PruneGrace = TimeSpan.FromMinutes(30);

    private static TestRun RunTests(string target, CheckRequest request, CancellationToken cancellation)
    {
        // The TRX files OUTLIVE the call now. They used to be deleted in a finally, which made
        // the envelope the only account of a run that ever existed: a session wanting anything
        // the envelope had not thought to include -- a duration, another assembly's results,
        // the raw XML behind a disputed count -- had no choice but to re-run the whole suite
        // with its own --logger. Reporting a path to a directory deleted before the caller read
        // the answer would have been worse than reporting none.
        string root = Path.Combine(Path.GetTempPath(), "janet-trx");
        Directory.CreateDirectory(root);
        PruneRunDirectories(root);

        // Timestamped so the directory name sorts chronologically and reads as something,
        // with a short random tail because two sessions can start in the same millisecond.
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string tail = Guid.NewGuid().ToString("N")[..4];
        string results = Path.Combine(root, $"{stamp}-{tail}");
        Directory.CreateDirectory(results);

        List<string> arguments =
        [
            target,
            "--no-build",
            "--configuration", request.Configuration,
            "-nologo",
            "--logger", "trx",
            "--results-directory", results,
        ];

        if (!string.IsNullOrEmpty(request.TestFilter))
        {
            arguments.Add("--filter");
            arguments.Add(request.TestFilter);
        }

        (int exitCode, IReadOnlyList<string> lines) = RunDotnet("test", arguments, cancellation);

        // The runner's verdict travels with the counters. Discarding it here is how a
        // crashed test host once summed to 423/423 passing and exit 0 -- the TRX files
        // held only the results the host lived to write.
        return DotnetTests.WithRunnerVerdict(DotnetTests.ReadDirectory(results), exitCode, lines);
    }

    /// <summary>
    /// Keeps the most recent run directories and removes the rest.
    /// </summary>
    /// <remarks>
    /// Pruning happens BEFORE a run rather than after, so the directory this call is about to
    /// report is never a candidate, and so an interrupted check leaves its results behind to
    /// be read. Names sort chronologically because they are timestamps -- ordering by name
    /// rather than by mtime keeps a directory a reader has just opened from looking newest.
    /// Failure here is swallowed: a temp directory that will not delete is not worth failing
    /// a build over, which is the same trade the old finally made.
    /// </remarks>
    private static void PruneRunDirectories(string root)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow - PruneGrace;

            string[] stale = [.. Directory.EnumerateDirectories(root)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(KeptRunDirectories)
                .Where(directory => Directory.GetLastWriteTimeUtc(directory) < cutoff)];

            foreach (string directory in stale)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { /* another session may be reading it */ }
                catch (UnauthorizedAccessException) { /* likewise */ }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing to prune.
        }
    }

    private static (int ExitCode, IReadOnlyList<string> Lines) RunDotnet(
        string verb, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        ProcessStartInfo info = new() { FileName = "dotnet" };

        info.ArgumentList.Add(verb);
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Both streams, because MSBuild writes diagnostics to stdout and the driver writes its
        // own failures to stderr; reading one is how a build failure comes back with no errors
        // in it. Drained together, by ProcessOutput: reading them one after the other deadlocked
        // under 6 KB of stderr, which is what 70 failing tests produce (2026-09-01).
        ProcessCapture captured = ProcessOutput.Capture(info, cancellation);

        List<string> lines = [.. captured.StandardOutput.Split('\n'), .. captured.StandardError.Split('\n')];

        return (captured.ExitCode, [.. lines.Select(l => l.TrimEnd('\r'))]);
    }
}

/// <summary>
/// Checks that outlive a client's patience, and the handles that get back to them.
/// </summary>
/// <remarks>
/// A full rebuild can take longer than an MCP client will wait, and a slow build presenting as a
/// dead tool is the worst available failure -- indistinguishable from thinking. So a call waits
/// a grace period and then hands back a handle rather than a timeout.
///
/// The table is per process and deliberately so: it belongs to the resident server that started
/// the work. A handle from a server that has since restarted is reported as unknown rather than
/// silently restarted, because re-running a build the caller did not ask for again is worse than
/// telling them it is gone.
/// </remarks>
public static class CheckJobs
{
    private sealed class Job
    {
        public required string Target { get; init; }
        public required string Configuration { get; init; }
        public required DateTime StartedAt { get; init; }
        public required Task<CheckResult> Work { get; init; }
    }

    private static readonly ConcurrentDictionary<string, Job> Running = new(StringComparer.Ordinal);

    /// <summary>How long a caller waits before being handed a handle instead of an answer.</summary>
    public static TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Starts a check and returns it if it finishes inside the grace period, or a handle if not.
    /// </summary>
    public static (CheckResult? Complete, CheckPending? Pending) Start(CheckRequest request)
    {
        // Resolved before starting, so a bad target is an error the caller gets now rather than
        // a handle that resolves to one later.
        string target = DotnetCheck.ResolveTarget(request.Target);
        string handle = Guid.NewGuid().ToString("n")[..12];
        DateTime startedAt = DateTime.UtcNow;

        Job job = new()
        {
            Target = target,
            Configuration = request.Configuration,
            StartedAt = startedAt,
            Work = Task.Run(() => DotnetCheck.Run(request with { Target = target })),
        };

        Running[handle] = job;

        return Wait(handle, job, Grace);
    }

    /// <summary>Polls a handle. Waits the grace period again rather than returning instantly.</summary>
    public static (CheckResult? Complete, CheckPending? Pending) Poll(string handle)
    {
        if (!Running.TryGetValue(handle, out Job? job))
        {
            throw new GraphException(
                $"No check with handle '{handle}'. Handles belong to the server process that started the work, so one from a restarted server is gone -- start the check again.");
        }

        return Wait(handle, job, Grace);
    }

    private static (CheckResult? Complete, CheckPending? Pending) Wait(string handle, Job job, TimeSpan grace)
    {
        if (job.Work.Wait(grace))
        {
            Running.TryRemove(handle, out _);

            // Unwrapped rather than left as an AggregateException: a caller reading the message
            // wants the reason the build could not start, not the shape of the task API.
            return (job.Work.GetAwaiter().GetResult(), null);
        }

        return (null, new CheckPending(
            job.Target,
            job.Configuration,
            handle,
            job.StartedAt.ToString("o"),
            Math.Round((DateTime.UtcNow - job.StartedAt).TotalSeconds, 1)));
    }
}
