using System.Xml;

namespace Janet.Core;

/// <summary>One failed test, with its payload up front.</summary>
public sealed record TestFailure(string Test, string? Message, IReadOnlyList<string> Stack);

/// <summary>Per-assembly counters, so a failure can be placed without opening anything.</summary>
public sealed record TestAssembly(string Name, int Total, int Passed, int Failed, int Skipped);

/// <summary>The whole test run, summed over every TRX dotnet test wrote.</summary>
public sealed record TestRun(
    bool Succeeded,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<TestFailure> Failures,
    IReadOnlyList<TestAssembly> Assemblies);

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

        foreach (string path in Directory.EnumerateFiles(directory, "*.trx").Order(StringComparer.Ordinal))
        {
            XmlDocument run = new();
            run.Load(path);

            XmlNamespaceManager ns = new(run.NameTable);
            ns.AddNamespace("t", TrxNamespace);

            if (run.SelectSingleNode("//t:ResultSummary/t:Counters", ns) is not XmlElement counters)
            {
                continue;
            }

            int assemblyPassed = Attribute(counters, "passed");
            int assemblyFailed = Attribute(counters, "failed");
            int assemblyExecuted = Attribute(counters, "executed");
            int assemblyTotal = Attribute(counters, "total");
            int assemblySkipped = assemblyTotal - assemblyExecuted;

            total += assemblyTotal;
            passed += assemblyPassed;
            failed += assemblyFailed;
            skipped += assemblySkipped;

            assemblies.Add(new TestAssembly(
                AssemblyName(run, ns, Path.GetFileName(path)),
                assemblyTotal,
                assemblyPassed,
                assemblyFailed,
                assemblySkipped));

            foreach (XmlNode result in run.SelectNodes("//t:UnitTestResult[@outcome=\"Failed\"]", ns) ?? run.CreateDocumentFragment().ChildNodes)
            {
                failures.Add(ReadFailure(result, ns));
            }
        }

        return new TestRun(failed == 0, total, passed, failed, skipped, failures, assemblies);
    }

    private static int Attribute(XmlElement element, string name) =>
        int.TryParse(element.GetAttribute(name), out int value) ? value : 0;

    private static string AssemblyName(XmlDocument run, XmlNamespaceManager ns, string fallback)
    {
        if (run.SelectSingleNode("//t:TestDefinitions/t:UnitTest/t:TestMethod", ns) is XmlElement method)
        {
            string codeBase = method.GetAttribute("codeBase");
            if (!string.IsNullOrEmpty(codeBase))
            {
                return Path.GetFileNameWithoutExtension(codeBase);
            }
        }

        return fallback;
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
