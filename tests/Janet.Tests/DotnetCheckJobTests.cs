using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The half of the check contract that has no PowerShell counterpart: the running arm, and the
/// handles that reach it.
/// </summary>
/// <remarks>
/// Nothing here is a parity test, because there is nothing to be at parity with -- the original
/// blocked until it was done. These pin the shape the schema declares and the behaviour a caller
/// depends on when a rebuild outlasts their patience.
///
/// The live handoff -- a real build that misses the grace period and is then polled to
/// completion -- is exercised against a running server with JANET_CHECK_GRACE=0 rather than
/// here, because reproducing it in-process would mean running dotnet build inside the suite.
/// </remarks>
public class DotnetCheckJobTests
{
    [Fact]
    public void TheRunningArmCarriesTheHandleAndNothingElse()
    {
        CheckPending pending = new(@"D:\Repos\Sample\App.slnx", "Debug", "abc123def456", "2026-08-12T04:00:00.0000000Z", 41.5);

        JsonObject envelope = JsonNode.Parse(DotnetCheckJson.Serialize(pending))!.AsObject();

        Assert.Equal("running", envelope["status"]!.GetValue<string>());
        Assert.Equal(4, envelope["contract"]!.GetValue<int>());
        Assert.Equal(@"D:\Repos\Sample\App.slnx", envelope["target"]!.GetValue<string>());
        Assert.Equal("Debug", envelope["configuration"]!.GetValue<string>());
        Assert.Equal("abc123def456", envelope["handle"]!.GetValue<string>());
        Assert.Equal(41.5, envelope["elapsedSeconds"]!.GetValue<double>());

        // A caller that has not read the discriminator has no business reading anything else,
        // so the running arm carries none of the answer. 'succeeded' here would be a lie with
        // a value, which is worse than an absence.
        Assert.Equal(
            ["status", "contract", "target", "configuration", "handle", "startedAt", "elapsedSeconds"],
            envelope.Select(p => p.Key));
    }

    [Fact]
    public void TheCompleteArmIsTaggedToo()
    {
        CheckResult result = new(
            @"D:\Repos\Sample\App.slnx",
            "Debug",
            true,
            new BuildReport(true, 1.2, [], [], 0, null, null, null),
            null,
            null);

        JsonObject envelope = JsonNode.Parse(DotnetCheckJson.Serialize(result))!.AsObject();

        Assert.Equal("complete", envelope["status"]!.GetValue<string>());
        Assert.Equal(4, envelope["contract"]!.GetValue<int>());

        // The three fields whose null is a statement rather than an absence. They are present
        // and null, not missing: a reader has to be able to tell "not applicable" from "this
        // build of the tool does not report it".
        Assert.True(envelope.ContainsKey("tests"));
        Assert.Null(envelope["tests"]);
        Assert.True(envelope.ContainsKey("graph"));
        Assert.Null(envelope["graph"]);
        Assert.True(envelope["build"]!.AsObject().ContainsKey("newWarnings"));
        Assert.Null(envelope["build"]!["newWarnings"]);
    }

    [Fact]
    public void AnUnknownHandleSaysWhyRatherThanRestartingTheWork()
    {
        GraphException ex = Assert.Throws<GraphException>(() => CheckJobs.Poll("nosuchhandle"));

        Assert.Contains("nosuchhandle", ex.Message, StringComparison.Ordinal);

        // Re-running a build the caller did not ask for again would be worse than telling them
        // the handle is gone, so the message has to say that is what happened.
        Assert.Contains("start the check again", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABadTargetFailsAtStartRatherThanBehindAHandle()
    {
        // Resolved before the job starts, deliberately: a handle that resolves to "that target
        // does not exist" thirty seconds later is a worse answer than an immediate one.
        Assert.Throws<GraphException>(() => CheckJobs.Start(new CheckRequest
        {
            Target = Path.Combine(Path.GetTempPath(), $"janet-no-such-target-{Guid.NewGuid():n}"),
        }));
    }

    [Fact]
    public void ADirectoryHoldingSeveralBuildablesNamesThemAll()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"janet-check-{Guid.NewGuid():n}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "One.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(directory, "Two.csproj"), "<Project />");

            GraphException ex = Assert.Throws<GraphException>(() => DotnetCheck.ResolveTarget(directory));

            Assert.Contains("One.csproj", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Two.csproj", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
        }
    }

    [Fact]
    public void ADirectoryHoldingOneBuildableResolvesToIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"janet-check-{Guid.NewGuid():n}");
        Directory.CreateDirectory(directory);

        try
        {
            string project = Path.Combine(directory, "Only.csproj");
            File.WriteAllText(project, "<Project />");

            Assert.Equal(project, DotnetCheck.ResolveTarget(directory));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
        }
    }

    [Fact]
    public void AWrongContractBaselineReadsAsAbsentRatherThanAsAWrongComparison()
    {
        string path = Fixture.Resolve("Fixtures", "warning-baseline.json");

        Assert.NotNull(DotnetDiagnostics.ReadBaseline(path, DotnetDiagnostics.BaselineContract));
        Assert.Null(DotnetDiagnostics.ReadBaseline(path, DotnetDiagnostics.BaselineContract + 1));
    }

    [Fact]
    public void TheBaselineContractIsNotTheEnvelopeContract()
    {
        // Deliberately different. The original stamped both from one number, so bumping the
        // envelope silently discarded every baseline on disk and the first -New run after an
        // upgrade lost its comparison without saying so.
        Assert.NotEqual(DotnetDiagnostics.Contract, DotnetDiagnostics.BaselineContract);
    }
}
