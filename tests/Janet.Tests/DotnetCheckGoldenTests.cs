using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Compares the build check's parsing, grouping, diffing and TRX reading with what
/// Invoke-DotnetCheck.ps1's own functions answered for the same fixtures.
/// </summary>
/// <remarks>
/// The goldens were recorded by lifting those functions out of the script with the AST and
/// calling them, because the script cannot be dot-sourced whole -- it defines its functions and
/// then runs a build. So this is still a comparison against an independent implementation, and
/// it needs neither PowerShell nor dotnet to run.
///
/// Whole-envelope goldens are deliberately absent: an envelope carries a build duration, an
/// absolute target path and the graph state of whatever repository it ran in. Everything that is
/// a function of text is pinned here instead, which is also where the bugs live.
/// </remarks>
public class DotnetCheckGoldenTests
{
    private static IReadOnlyList<Diagnostic> Parsed() =>
        DotnetDiagnostics.Read(File.ReadAllLines(Fixture.Resolve("Fixtures", "msbuild-output.txt")));

    private static List<Diagnostic> Warnings() => [.. Parsed().Where(d => d.Severity == "warning")];

    private static JsonNode Golden(string label) =>
        JsonNode.Parse(Fixture.ReadGolden("dotnet-check", label, ".json"))!;

    [Fact]
    public void ParsingMatchesTheRecordedAnswer()
    {
        JsonArray expected = Golden("read-build-output")["diagnostics"]!.AsArray();
        IReadOnlyList<Diagnostic> actual = Parsed();

        Assert.Equal(expected.Count, actual.Count);

        for (int i = 0; i < expected.Count; i++)
        {
            JsonNode golden = expected[i]!;

            Assert.Equal(golden["file"]!.GetValue<string>(), actual[i].File);
            Assert.Equal(Line(golden["line"]), actual[i].Line);
            Assert.Equal(golden["severity"]!.GetValue<string>(), actual[i].Severity);
            Assert.Equal(golden["code"]!.GetValue<string>(), actual[i].Code);
            Assert.Equal(golden["message"]!.GetValue<string>(), actual[i].Message);
        }
    }

    [Fact]
    public void GroupingMatchesTheRecordedAnswer()
    {
        JsonArray expected = Golden("group-warnings")["groups"]!.AsArray();
        IReadOnlyList<WarningGroup> actual = DotnetDiagnostics.Group(Warnings());

        Assert.Equal(
            expected.Select(g => g!["code"]!.GetValue<string>()),
            actual.Select(g => g.Code));

        for (int i = 0; i < expected.Count; i++)
        {
            JsonNode golden = expected[i]!;

            Assert.Equal(golden["count"]!.GetValue<int>(), actual[i].Count);
            Assert.Equal(golden["omittedInstances"]!.GetValue<int>(), actual[i].OmittedInstances);

            JsonArray instances = golden["instances"]!.AsArray();
            Assert.Equal(instances.Count, actual[i].Instances.Count);

            for (int j = 0; j < instances.Count; j++)
            {
                Assert.Equal(instances[j]!["file"]!.GetValue<string>(), actual[i].Instances[j].File);
                Assert.Equal(Line(instances[j]!["line"]), actual[i].Instances[j].Line);
                Assert.Equal(instances[j]!["message"]!.GetValue<string>(), actual[i].Instances[j].Message);
            }
        }
    }

    [Fact]
    public void WarningKeysMatchTheRecordedAnswer()
    {
        string[] expected = [.. Golden("warning-keys")["keys"]!.AsArray().Select(k => k!.GetValue<string>())];

        Assert.Equal(expected, Warnings().Select(DotnetDiagnostics.Key));
    }

    [Fact]
    public void TheBaselineDiffMatchesTheRecordedAnswer()
    {
        WarningBaseline? baseline = DotnetDiagnostics.ReadBaseline(
            Fixture.Resolve("Fixtures", "warning-baseline.json"),
            DotnetDiagnostics.BaselineContract);

        Assert.NotNull(baseline);

        BaselineDiff actual = DotnetDiagnostics.Compare(Warnings(), baseline);
        JsonNode expected = Golden("compare-baseline");

        Assert.Equal(expected["resolvedWarningCount"]!.GetValue<int>(), actual.ResolvedWarningCount);

        JsonArray fresh = expected["newWarnings"]!.AsArray();
        Assert.Equal(fresh.Count, actual.NewWarnings.Count);

        for (int i = 0; i < fresh.Count; i++)
        {
            Assert.Equal(fresh[i]!["file"]!.GetValue<string>(), actual.NewWarnings[i].File);
            Assert.Equal(Line(fresh[i]!["line"]), actual.NewWarnings[i].Line);
            Assert.Equal(fresh[i]!["code"]!.GetValue<string>(), actual.NewWarnings[i].Code);
            Assert.Equal(fresh[i]!["message"]!.GetValue<string>(), actual.NewWarnings[i].Message);
        }
    }

    [Fact]
    public void TheBaselineFileNameMatchesTheRecordedAnswer()
    {
        // The hash keys the file, so a change to how it is derived silently orphans every
        // baseline on disk and the next -New run reports the whole census as new.
        string expected = Golden("baseline-path")["fileName"]!.GetValue<string>();
        string actual = Path.GetFileName(DotnetDiagnostics.BaselinePath(@"D:\Repos\Sample\App.slnx", "Debug"));

        Assert.Equal(expected, actual, ignoreCase: true);
    }

    [Fact]
    public void TheTestRunMatchesTheRecordedAnswer()
    {
        TestRun actual = DotnetTests.ReadDirectory(Path.GetDirectoryName(Fixture.Resolve("Fixtures", "trx", "Core.Tests.trx"))!);
        JsonNode expected = Golden("read-trx");

        Assert.Equal(expected["succeeded"]!.GetValue<bool>(), actual.Succeeded);
        Assert.Equal(expected["total"]!.GetValue<int>(), actual.Total);
        Assert.Equal(expected["passed"]!.GetValue<int>(), actual.Passed);
        Assert.Equal(expected["failed"]!.GetValue<int>(), actual.Failed);
        Assert.Equal(expected["skipped"]!.GetValue<int>(), actual.Skipped);

        JsonArray failures = expected["failures"]!.AsArray();
        Assert.Equal(failures.Count, actual.Failures.Count);

        for (int i = 0; i < failures.Count; i++)
        {
            Assert.Equal(failures[i]!["test"]!.GetValue<string>(), actual.Failures[i].Test);

            // Verbatim and whole. A failed assert's message IS the payload, and a port that
            // trimmed or collapsed it would still pass every count above.
            Assert.Equal(failures[i]!["message"]!.GetValue<string>(), actual.Failures[i].Message);

            Assert.Equal(
                failures[i]!["stack"]!.AsArray().Select(f => f!.GetValue<string>()),
                actual.Failures[i].Stack);
        }

        JsonArray assemblies = expected["assemblies"]!.AsArray();
        Assert.Equal(
            assemblies.Select(a => a!["name"]!.GetValue<string>()).Order(),
            actual.Assemblies.Select(a => a.Name).Order());
    }

    private static int? Line(JsonNode? node) =>
        node is null ? null : node.GetValue<int>();
}
