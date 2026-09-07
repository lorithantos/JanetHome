using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace Janet.Core;

/// <summary>One failed test, with its payload up front.</summary>
public sealed record TestFailure(string Test, string? Message, IReadOnlyList<string> Stack);

/// <summary>How long one test class took, summed over the tests in it.</summary>
/// <remarks>
/// The CLASS is the unit worth reporting, because it is the unit that serialises. xUnit runs
/// test collections in parallel and defaults to one collection per class, so two classes
/// overlap and two tests in the same class never do. A class whose tests sum to a minute
/// costs a minute of wall clock however many cores are idle, which is why a slow class hides
/// so well behind a healthy-looking total. Measured on this repo 2026-09-07: 382 tests,
/// 17.07s of test time inside 3.96s of wall clock -- 4.31x, on 24 logical processors.
/// </remarks>
public sealed record TestClassTime(string Class, double TestTimeSeconds, int Tests);

/// <summary>Per-assembly counters, so a failure can be placed without opening anything.</summary>
/// <remarks>
/// Status is "complete", "empty", or "aborted", and the three are not interchangeable:
/// "empty" ran nothing and is not a failure, "aborted" is a crashed run's leftovers whose
/// counters are what the host lived to write. <see cref="DotnetTests"/> carries the evidence
/// each one is read from.
/// </remarks>
public sealed record TestAssembly(string Name, int Total, int Passed, int Failed, int Skipped, string Status)
{
    /// <summary>Wall clock for this assembly's run, from the TRX's own Times. What you waited.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>The per-test durations added up. EXCEEDS <see cref="DurationSeconds"/> whenever
    /// collections ran in parallel, so the two are different questions and neither is "the"
    /// duration: this one is where the time went, that one is how long you sat there.</summary>
    public double TestTimeSeconds { get; init; }

    /// <summary>The TRX this was read from, kept on disk so the claim can be checked.</summary>
    public string? ResultsFile { get; init; }
}

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

    /// <summary>Wall clock across every assembly, earliest start to latest finish.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Every test's own duration added up, across every assembly.</summary>
    public double TestTimeSeconds { get; init; }

    /// <summary>The slowest classes by summed test time, longest first. Diagnostic output,
    /// never a budget: suite duration is machine-dependent and nothing here fails a run for
    /// being slow. Empty when the TRX carried no durations.</summary>
    public IReadOnlyList<TestClassTime> Slowest { get; init; } = [];

    /// <summary>Where the TRX files are, so a session can open one instead of re-running the
    /// suite with a narrower filter to see what this envelope already read.</summary>
    public string? ResultsDirectory { get; init; }
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

    /// <summary>How many classes the slowest list carries. Enough to name a culprit, short
    /// enough that it costs nothing to always send.</summary>
    private const int SlowestClasses = 5;

    /// <summary>Sums every TRX in a directory -- dotnet test writes one per project.</summary>
    public static TestRun ReadDirectory(string directory)
    {
        int total = 0;
        int passed = 0;
        int failed = 0;
        int skipped = 0;
        List<TestFailure> failures = [];
        List<TestAssembly> assemblies = [];
        Dictionary<string, (double Seconds, int Tests)> classTimes = new(StringComparer.Ordinal);
        string? abort = null;
        DateTimeOffset? earliestStart = null;
        DateTimeOffset? latestFinish = null;
        double testTime = 0;

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

            XmlNodeList results = run.SelectNodes("//t:Results/t:UnitTestResult", ns)
                ?? run.CreateDocumentFragment().ChildNodes;

            string status = Classify(run, ns, results.Count, out string? runAbort);
            abort ??= runAbort;

            double assemblyTestTime = ReadClassTimes(run, ns, results, classTimes);
            testTime += assemblyTestTime;

            (DateTimeOffset? start, DateTimeOffset? finish) = ReadTimes(run, ns);
            if (start is not null && (earliestStart is null || start < earliestStart))
            {
                earliestStart = start;
            }

            if (finish is not null && (latestFinish is null || finish > latestFinish))
            {
                latestFinish = finish;
            }

            double wall = start is not null && finish is not null
                ? Math.Max(0, (finish.Value - start.Value).TotalSeconds)
                : assemblyTestTime;

            assemblies.Add(new TestAssembly(
                AssemblyName(run, ns) ?? NameFromRunInfo(run, ns) ?? Path.GetFileName(path),
                assemblyTotal,
                assemblyPassed,
                assemblyFailed,
                assemblySkipped,
                status)
            {
                DurationSeconds = Round(wall),
                TestTimeSeconds = Round(assemblyTestTime),
                ResultsFile = path,
            });

            foreach (XmlNode result in run.SelectNodes("//t:UnitTestResult[@outcome=\"Failed\"]", ns) ?? run.CreateDocumentFragment().ChildNodes)
            {
                failures.Add(ReadFailure(result, ns));
            }
        }

        // "empty" does not fail a run. A filter that matches nothing in four of six assemblies
        // is a narrow filter, not four crashes -- which is what this used to report.
        bool succeeded = failed == 0 && assemblies.All(a => a.Status != "aborted");

        double duration = earliestStart is not null && latestFinish is not null
            ? Math.Max(0, (latestFinish.Value - earliestStart.Value).TotalSeconds)
            : testTime;

        return new TestRun(succeeded, total, passed, failed, skipped, failures, assemblies)
        {
            Abort = abort,
            DurationSeconds = Round(duration),
            TestTimeSeconds = Round(testTime),
            Slowest = Slowest(classTimes),
            ResultsDirectory = directory,
        };
    }

    /// <summary>
    /// Which of the three things this TRX is, and the abort banner when it is the bad one.
    /// </summary>
    /// <remarks>
    /// The first version of this asked four independent questions -- no assembly name, no
    /// counters, any run-level RunInfo Error, ResultSummary "Aborted" -- and OR'd them. Every
    /// one of the first three fires on a healthy run, which was measured twice:
    /// <list type="bullet">
    /// <item>xUnit writes its per-test "[FAIL]" diagnostics as RunInfo outcome="Error", so
    /// EVERY failing run was labelled aborted and handed a [FAIL] line as its crash banner.
    /// Captured 2026-09-07: outcome="Failed", one RunInfo Error reading
    /// "[xUnit.net 00:00:01.20]     Janet.Tests.TempTrxProbe.ProbeFailsOnPurpose [FAIL]".</item>
    /// <item>A filter matching nothing in an assembly writes a TRX with no TestDefinitions and
    /// zero counters -- the "no name, no counters" shape -- so a narrow filter over a
    /// multi-assembly solution reported a failed run. Captured the same day: outcome="Completed",
    /// total="0", and a RunInfo outcome="Warning" reading "No test matches the given testcase
    /// filter ... in ...\Janet.Tests.dll".</item>
    /// </list>
    /// So absence is no longer evidence. What is left is positive evidence only, and the
    /// ResultSummary outcome carries most of it: "Completed" and "Failed" are both closed-out
    /// runs, "Aborted" is the documented crash, and anything else -- "InProgress", or no
    /// ResultSummary at all -- is a file the runner never finished writing. A RunInfo Error
    /// counts only when its TEXT reads as an abort banner, which is the same vocabulary
    /// <see cref="ReadAbort"/> already applied to the console; the two channels disagreeing
    /// was the defect.
    /// <para>
    /// The cost is a localised runner whose banner text does not match: its crash reads as
    /// "complete" unless the outcome attribute also says Aborted. That is the safe direction
    /// and it is backstopped -- a non-zero runner exit still forces succeeded false through
    /// <see cref="WithRunnerVerdict"/>, so a crash cannot read as a pass. Losing the LABEL is
    /// recoverable; the confidently wrong label on every failing run was not.
    /// </para>
    /// </remarks>
    private static string Classify(XmlDocument run, XmlNamespaceManager ns, int resultCount, out string? abort)
    {
        abort = AbortRunInfo(run, ns);

        string? outcome = Outcome(run, ns);
        bool closedOut = string.Equals(outcome, "Completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(outcome, "Aborted", StringComparison.OrdinalIgnoreCase)
            || abort is not null
            || !closedOut)
        {
            return "aborted";
        }

        return resultCount == 0 ? "empty" : "complete";
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
    /// to the TRX path, whose Aborted outcome is locale-independent.
    /// </remarks>
    public static string? ReadAbort(IReadOnlyList<string> lines)
    {
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (ReadsAsAbort(lines[i]))
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

    /// <summary>
    /// Whether a line of runner output announces an aborted run.
    /// </summary>
    /// <remarks>
    /// One vocabulary, applied to both channels. It used to live only here, while the TRX path
    /// accepted ANY run-level Error -- and that asymmetry is exactly what let an xUnit "[FAIL]"
    /// diagnostic be reported as a crash banner.
    /// </remarks>
    private static bool ReadsAsAbort(string text) =>
        text.Contains("test run was aborted", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Test Run Aborted", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Test host process crashed", StringComparison.OrdinalIgnoreCase);

    private static int Attribute(XmlElement element, string name) =>
        int.TryParse(element.GetAttribute(name), out int value) ? value : 0;

    private static double Round(double seconds) => Math.Round(seconds, 3, MidpointRounding.AwayFromZero);

    private static string? Outcome(XmlDocument run, XmlNamespaceManager ns) =>
        run.SelectSingleNode("//t:ResultSummary", ns) is XmlElement summary
            ? summary.GetAttribute("outcome")
            : null;

    /// <summary>
    /// The text of the first run-level Error that READS as an abort, or null when there is none.
    /// </summary>
    /// <remarks>
    /// The outcome attribute alone is not the discriminator it was taken for: xUnit files a
    /// RunInfo Error for every failing test. The text is what separates "the host died" from
    /// "a test failed", and it is the same text the console fallback matches on.
    /// </remarks>
    private static string? AbortRunInfo(XmlDocument run, XmlNamespaceManager ns)
    {
        foreach (XmlNode info in run.SelectNodes("//t:RunInfos/t:RunInfo", ns) ?? run.CreateDocumentFragment().ChildNodes)
        {
            if (info is XmlElement element
                && string.Equals(element.GetAttribute("outcome"), "Error", StringComparison.OrdinalIgnoreCase)
                && element.SelectSingleNode("t:Text", ns) is XmlNode text
                && ReadsAsAbort(text.InnerText))
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
    /// The assembly name recovered from a RunInfo that names a .dll, for a TRX with no test
    /// definitions to read it from.
    /// </summary>
    /// <remarks>
    /// This is the "no test matches the given testcase filter ... in ...\Foo.Tests.dll" case.
    /// Without it, a narrow filter over six assemblies produced five entries named after their
    /// TRX FILES -- the phantom rows that made a healthy run unreadable. A path is a path in
    /// any locale, so this reads the token rather than the sentence around it, and taking the
    /// LAST match keeps a filter expression that happens to mention a dll from winning.
    /// Nothing is guessed: a RunInfo naming no dll leaves the name to the filename fallback.
    /// </remarks>
    private static string? NameFromRunInfo(XmlDocument run, XmlNamespaceManager ns)
    {
        foreach (XmlNode info in run.SelectNodes("//t:RunInfos/t:RunInfo/t:Text", ns) ?? run.CreateDocumentFragment().ChildNodes)
        {
            MatchCollection matches = Regex.Matches(info.InnerText, @"\S+\.dll\b", RegexOptions.IgnoreCase);
            if (matches.Count > 0)
            {
                return Path.GetFileNameWithoutExtension(matches[^1].Value);
            }
        }

        return null;
    }

    /// <summary>The run's wall clock, from the TRX's Times element.</summary>
    private static (DateTimeOffset? Start, DateTimeOffset? Finish) ReadTimes(XmlDocument run, XmlNamespaceManager ns)
    {
        if (run.SelectSingleNode("//t:Times", ns) is not XmlElement times)
        {
            return (null, null);
        }

        return (ParseTime(times.GetAttribute("start")), ParseTime(times.GetAttribute("finish")));
    }

    private static DateTimeOffset? ParseTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;

    /// <summary>
    /// Adds this TRX's per-test durations into the running per-class tally, and returns the
    /// assembly's own summed test time.
    /// </summary>
    /// <remarks>
    /// The class comes from TestDefinitions, joined on testId, rather than from trimming the
    /// test name: a theory's name carries its arguments ("Refuses(limit: -1)"), so string
    /// surgery on the name splits one class across as many buckets as it has cases.
    /// </remarks>
    private static double ReadClassTimes(
        XmlDocument run,
        XmlNamespaceManager ns,
        XmlNodeList results,
        Dictionary<string, (double Seconds, int Tests)> classTimes)
    {
        Dictionary<string, string> classByTestId = new(StringComparer.OrdinalIgnoreCase);
        foreach (XmlNode definition in run.SelectNodes("//t:TestDefinitions/t:UnitTest", ns) ?? run.CreateDocumentFragment().ChildNodes)
        {
            if (definition is XmlElement unit
                && unit.SelectSingleNode("t:TestMethod", ns) is XmlElement method)
            {
                string id = unit.GetAttribute("id");
                string className = method.GetAttribute("className");
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(className))
                {
                    classByTestId[id] = className;
                }
            }
        }

        double seconds = 0;

        foreach (XmlNode node in results)
        {
            if (node is not XmlElement result
                || !TimeSpan.TryParse(result.GetAttribute("duration"), CultureInfo.InvariantCulture, out TimeSpan duration))
            {
                continue;
            }

            seconds += duration.TotalSeconds;

            if (!classByTestId.TryGetValue(result.GetAttribute("testId"), out string? className))
            {
                continue;
            }

            (double existing, int count) = classTimes.TryGetValue(className, out (double, int) tally) ? tally : (0, 0);
            classTimes[className] = (existing + duration.TotalSeconds, count + 1);
        }

        return seconds;
    }

    /// <summary>The slowest classes, longest first, with ties broken by name so the list is
    /// stable across runs of an unchanged suite.</summary>
    private static IReadOnlyList<TestClassTime> Slowest(Dictionary<string, (double Seconds, int Tests)> classTimes) =>
    [
        .. classTimes
            .OrderByDescending(entry => entry.Value.Seconds)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(SlowestClasses)
            .Select(entry => new TestClassTime(entry.Key, Round(entry.Value.Seconds), entry.Value.Tests))
    ];

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
