using System.ComponentModel;
using Janet.Core;
using ModelContextProtocol.Server;

namespace Janet.Mcp;

/// <summary>
/// Build and test, reported as one structured answer.
/// </summary>
/// <remarks>
/// The only tool here that can outlast a client's patience, which is why it is the only one
/// that answers in two shapes. A full rebuild of a large solution takes minutes; a tool that
/// simply blocked would present as dead, and a dead tool is indistinguishable from thinking.
/// </remarks>
[McpServerToolType]
public static class CheckTools
{
    [McpServerTool(Name = "dotnet_check")]
    [Description(
        "Build and test a .NET target and get back structured JSON instead of console " +
        "scrollback. Use this for any dotnet build or test a session runs: one real warning " +
        "drowns in fifteen restore warnings on the console, and a failed assert takes three " +
        "re-runs with different filters to extract, while here the message comes back whole " +
        "and read from the TRX rather than scraped.\n\n" +
        "THE RESPONSE IS A TAGGED UNION. Read 'status' first. \"complete\" carries the whole " +
        "answer; \"running\" carries a 'handle' and nothing else, because the build outlived " +
        "the wait -- call this tool again passing that handle to poll. A handle belongs to the " +
        "server process that started the work, so one from a restarted server is gone rather " +
        "than silently restarted.\n\n" +
        "THREE FIELDS MEAN SOMETHING SPECIFIC WHEN NULL, and reading them as emptiness is the " +
        "mistake this envelope is shaped to prevent: newWarnings null means NO COMPARISON " +
        "HAPPENED (no 'new', or no prior baseline), not that none were new; tests null means " +
        "NOT RUN (noTests, or the build failed), not a suite with no tests; graph null means " +
        "NOT APPLICABLE -- this repository carries neither graph convention (a .graph directory " +
        "with scripts\\graph.ps1, or a razorgraph server declared in its .mcp.json) -- while a " +
        "missing graph is status 'absent'. Where razorgraph is declared and holds a graph built " +
        "from this repository, a successful build rebuilds it whenever source has outrun it, so " +
        "the graph tools stay current across the check loop; 'via' says which convention " +
        "answered and 'graphId' names the server-side graph.\n\n" +
        "'new' answers what did THIS change introduce, by diffing the warning census against " +
        "the previous 'new' run. 'full' rebuilds everything without the baseline machinery and " +
        "is what to reach for whenever the SHAPE of the build changed -- a project added or " +
        "removed, a reference swapped, a target framework moved -- because an incremental run " +
        "that skipped a project entirely and one that had nothing to say about it produce the " +
        "identical green. 'succeeded' means exactly one thing: the build succeeded and every " +
        "test passed.")]
    public static string Check(
        [Description("A .sln/.slnx/.csproj, or a directory holding exactly one. Defaults to the current directory.")]
        string? target = null,
        [Description("Build configuration. Default Debug.")]
        string configuration = "Debug",
        [Description("Build only. 'tests' comes back null.")]
        bool noTests = false,
        [Description("Passed to dotnet test --filter. The counters then describe the filtered run, not the whole suite.")]
        string? testFilter = null,
        [Description("Diff the warning census against the previous run of this kind. Forces a full rebuild, because a diff against an incremental census reports every later full build as all-new.")]
        bool @new = false,
        [Description("Rebuild everything without the baseline machinery.")]
        bool full = false,
        [Description("Skip the code-graph refresh. Only relevant where the repository carries a convention: a .graph directory with scripts\\graph.ps1, or a razorgraph server in its .mcp.json.")]
        bool noGraph = false,
        [Description("A handle from an earlier 'running' response. Poll with it; everything else is ignored.")]
        string? handle = null)
    {
        (CheckResult? complete, CheckPending? pending) = string.IsNullOrEmpty(handle)
            ? CheckJobs.Start(new CheckRequest
            {
                Target = target ?? ".",
                Configuration = configuration,
                NoTests = noTests,
                TestFilter = testFilter,
                New = @new,
                Full = full,
                NoGraph = noGraph,
            })
            : CheckJobs.Poll(handle);

        return complete is not null
            ? DotnetCheckJson.Serialize(complete)
            : DotnetCheckJson.Serialize(pending!);
    }
}
