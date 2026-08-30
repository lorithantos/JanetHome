using System.Xml;

namespace Janet.Core;

/// <summary>One failed test, with its payload up front.</summary>
public sealed record TestFailure(string Test, string? Message, IReadOnlyList<string> Stack);

/// <summary>Per-assembly counters, so a failure can be placed without opening anything.</summary>
/// <remarks>
/// Status is "complete" or "aborted". A TRX whose summary says the run was aborted, or that is
/// too partial to even name its assembly, is a crashed run's leftovers -- reporting it as an
/// assembly that ran zero tests is how a dead test host once read as harmless clutter.
/// </remarks>
public sealed record TestAssembly(string Name, int Total, int Passed, int Failed, int Skipped, string Status);

/// <summary>The whole test run, summed over every TRX dotnet test wrote.</summary>
public sealed record TestRun(
    bool Succeeded,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<TestFailure> Failures,
    IReadOnlyList<TestAssembly> Assemblies)
{
    /// <summary>What dotnet test itself exited with. Non-zero means the run did not pass,
    /// whatever the counters say -- a crashed host reports fewer tests, not failed ones.</summary>
    public int RunnerExitCode { get; init; }

    /// <summary>The runner's abort banner, when the run was aborted. Null for a run that
    /// finished. This is the only text that names why a test host died.</summary>
    public string? Abort { get; init; }
}

/// <summary>
/// Reads test results out of TRX files rather than scraping them from the console.
/// </summary>
/// <remarks>
/// The console form buries a failed assert: extracting the message takes three re-runs with
/// different filters, and the answer arrives split across lines that also carry progress
/// output. The TRX is structured and already on disk, so the message comes back whole.
/// </remarks>
public static class DotnetTests
{
    private const string TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>Sums every TRX in a directory -- dotnet test writes one per project.</summary>
    public static TestRun ReadDirectory(string directory)
    {
        int total = 0;
        int passed = 0;
        int failed = 0;
        int skipped = 0;
        List<TestFailure> failures = [];
        List<TestAssembly> assemblies = [];
        string? abort = null;

        foreach (string path in Directory.EnumerateFiles(directory, "*.trx").Order(StringComparer.Ordinal))
        {
            XmlDocument run = new();
            run.Load(path);

            XmlNamespaceManager ns = new(run.NameTable);
            ns.AddNamespace("t", TrxNamespace);

            // A TRX with no counters is not skipped: it is the most partial result there is,
            // and dropping it is how a crashed assembly once vanished from the envelope.
            XmlElement? counters = run.SelectSingleNode("//t:ResultSummary/t:Counters", ns) as XmlElement;

            int assemblyPassed = counters is null ? 0 : Attribute(counters, "passed");
            int assemblyFailed = counters is null ? 0 : Attribute(counters, "failed");
            int assemblyExecuted = counters is null ? 0 : Attribute(counters, "executed");
            int assemblyTotal = counters is null ? 0 : Attribute(counters, "total");
            int assemblySkipped = assemblyTotal - assemblyExecuted;

            total += assemblyTotal;
            passed += assemblyPassed;
            failed += assemblyFailed;
            skipped += assemblySkipped;

            // Independent signs of a crashed run, because none is guaranteed. The load-bearing
            // one is the RunInfo: the runner writes its abort into the TRX as a run-level Error,
            // and it is there whether or not the file also got counters and definitions before
            // the host died -- measured 2026-08-30, when a crashed assembly's TRX carried a
            // name, plausible counters, outcome "Failed" (also a healthy value), and the whole
            // diagnosis only in RunInfos. The attribute is locale-independent where the banner
            // text is not. The others: outcome="Aborted" is the documented closed-out form, and
            // a file abandoned mid-write may lack the definitions the name is read from or the
            // counters themselves.
            string? name = AssemblyName(run, ns);
            string? runError = RunError(run, ns);
            bool aborted = name is null
                || counters is null
                || runError is not null
                || string.Equals(Outcome(run, ns), "Aborted", StringComparison.OrdinalIgnoreCase);

            abort ??= runError;

            assemblies.Add(new TestAssembly(
                name ?? Path.GetFileName(path),
                assemblyTotal,
                assemblyPassed,
                assemblyFailed,
                assemblySkipped,
                aborted ? "aborted" : "complete"));

            foreach (XmlNode result in run.SelectNodes("//t:UnitTestResult[@outcome=\"Failed\"]", ns) ?? run.CreateDocumentFragment().ChildNodes)
            {
                failures.Add(ReadFailure(result, ns));
            }
        }

        bool succeeded = failed == 0 && assemblies.All(a => a.Status == "complete");

        return new TestRun(succeeded, total, passed, failed, skipped, failures, assemblies)
        {
            Abort = abort,
        };
    }

    /// <summary>
    /// Folds the runner's own verdict into a parsed run.
    /// </summary>
    /// <remarks>
    /// The counters describe the results the runner managed to write, which is not the same
    /// claim as the run passing: a test host that dies mid-run leaves a smaller total and no
    /// failures. "No failures found in the results I could read" is equally true of a crash and
    /// an empty set, so a summary must carry the runner's exit code rather than recompute a
    /// verdict from the parts it parsed. See notes\test-count-blind-spot.md for the measured
    /// instance.
    /// </remarks>
    public static TestRun WithRunnerVerdict(TestRun run, int exitCode, IReadOnlyList<string> lines) =>
        run with
        {
            Succeeded = run.Succeeded && exitCode == 0,
            RunnerExitCode = exitCode,
            // The TRX's own RunInfo is the preferred source -- structured, and per assembly.
            // The console banner is the fallback for an abort that never reached a TRX at all.
            Abort = run.Abort ?? ReadAbort(lines),
        };

    /// <summary>
    /// The abort banner out of the runner's console output, or null when none is there.
    /// </summary>
    /// <remarks>
    /// Console scraping, deliberately and only as a fallback: an abort is normally read from
    /// the TRX's RunInfos, but a host that dies before any TRX is written leaves this banner
    /// as the one channel naming the cause. Best effort -- the exit code is the verdict, this
    /// is the diagnosis. The phrases are the English runner's; a localized SDK falls through
    /// to the TRX path, whose Error outcome is locale-independent.
    /// </remarks>
    public static string? ReadAbort(IReadOnlyList<string> lines)
    {
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains("test run was aborted", StringComparison.OrdinalIgnoreCase)
                || lines[i].Contains("Test Run Aborted", StringComparison.OrdinalIgnoreCase)
                || lines[i].Contains("Test host process crashed", StringComparison.OrdinalIgnoreCase))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        // The banner, the exception, and the top of its stack. Capped because the payload is
        // the first few lines; the full scrollback is what this envelope exists to replace.
        List<string> banner = [];
        for (int i = start; i < lines.Count && banner.Count < 12; i++)
        {
            string line = lines[i].Trim();
            if (line.Length > 0)
            {
                banner.Add(line);
            }
        }

        return string.Join('\n', banner);
    }

    private static int Attribute(XmlElement element, string name) =>
        int.TryParse(element.GetAttribute(name), out int value) ? value : 0;

    private static string? Outcome(XmlDocument run, XmlNamespaceManager ns) =>
        run.SelectSingleNode("//t:ResultSummary", ns) is XmlElement summary
            ? summary.GetAttribute("outcome")
            : null;

    /// <summary>
    /// The text of the first run-level Error in the TRX's RunInfos, or null when there is none.
    /// This is where the runner records "the test run was aborted" and the exception that
    /// killed the host -- structured, in the file, whatever locale the runner speaks.
    /// </summary>
    private static string? RunError(XmlDocument run, XmlNamespaceManager ns)
    {
        foreach (XmlNode info in run.SelectNodes("//t:RunInfos/t:RunInfo", ns) ?? run.CreateDocumentFragment().ChildNodes)
        {
            if (info is XmlElement element
                && string.Equals(element.GetAttribute("outcome"), "Error", StringComparison.OrdinalIgnoreCase)
                && element.SelectSingleNode("t:Text", ns) is XmlNode text)
            {
                return text.InnerText.Trim();
            }
        }

        return null;
    }

    private static string? AssemblyName(XmlDocument run, XmlNamespaceManager ns)
    {
        if (run.SelectSingleNode("//t:TestDefinitions/t:UnitTest/t:TestMethod", ns) is XmlElement method)
        {
            string codeBase = method.GetAttribute("codeBase");
            if (!string.IsNullOrEmpty(codeBase))
            {
                return Path.GetFileNameWithoutExtension(codeBase);
            }
        }

        return null;
    }

    /// <summary>
    /// The message verbatim and whole -- it is the payload. The stack is capped to the top
    /// frames, because the deepest one is where the assert fired.
    /// </summary>
    private static TestFailure ReadFailure(XmlNode result, XmlNamespaceManager ns)
    {
        string? message = null;
        List<string> stack = [];

        if (result.SelectSingleNode(".//t:ErrorInfo", ns) is XmlNode info)
        {
            if (info.SelectSingleNode("t:Message", ns) is XmlNode messageNode)
            {
                message = messageNode.InnerText;
            }

            if (info.SelectSingleNode("t:StackTrace", ns) is XmlNode stackNode)
            {
                stack =
                [
                    .. stackNode.InnerText
                        .Split('\n')
                        .Select(frame => frame.Trim())
                        .Where(frame => frame.Length > 0)
                        .Take(4)
                ];
            }
        }

        string name = result is XmlElement element ? element.GetAttribute("testName") : "";

        return new TestFailure(name, message, stack);
    }
}
