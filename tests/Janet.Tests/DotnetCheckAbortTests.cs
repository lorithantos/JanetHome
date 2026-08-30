using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// An aborted test run must never sum to a passing one.
/// </summary>
/// <remarks>
/// These are not goldens: the behaviour is new, written after a crashed test host summed to
/// 423/423 passing with exit 0 (notes\test-count-blind-spot.md). The fixture directory models
/// that incident -- one healthy assembly, one whose TRX says Aborted, and one abandoned
/// mid-write with no test definitions to name it.
/// </remarks>
public class DotnetCheckAbortTests
{
    private static TestRun Read() =>
        DotnetTests.ReadDirectory(Path.GetDirectoryName(Fixture.Resolve("Fixtures", "trx-aborted", "Web.Tests.trx"))!);

    [Fact]
    public void AnAbortedAssemblyFailsTheRun()
    {
        TestRun run = Read();

        // Zero failures, and still not a pass: two of the three assemblies are partial.
        Assert.Equal(0, run.Failed);
        Assert.False(run.Succeeded);
    }

    [Fact]
    public void AnAbortedOutcomeIsMarkedEvenWhenTheAssemblyIsNamed()
    {
        TestAssembly aborted = Read().Assemblies.Single(a => a.Name == "App.Tests");

        // The counters carry what the host lived to write; the status says not to read them
        // as a result.
        Assert.Equal("aborted", aborted.Status);
        Assert.Equal(4, aborted.Passed);
    }

    [Fact]
    public void ANamelessPartialTrxIsAbortedNotAPhantomAssembly()
    {
        TestAssembly partial = Read().Assemblies
            .Single(a => a.Name == "user_MACHINE_2026-08-30_14_59_52_net10.0.trx");

        Assert.Equal("aborted", partial.Status);
        Assert.Equal(0, partial.Total);
    }

    [Fact]
    public void ACompleteAssemblyStaysComplete()
    {
        TestAssembly complete = Read().Assemblies.Single(a => a.Name == "Web.Tests");

        Assert.Equal("complete", complete.Status);
        Assert.Equal(2, complete.Passed);
    }

    [Fact]
    public void ARunLevelErrorMarksANamedCountedAssemblyAborted()
    {
        // The shape that slipped through the first fix, reported from the field 2026-08-30:
        // the crashed assembly's TRX had a name, plausible counters (3 of its 131 tests), and
        // outcome "Failed" -- a value healthy runs also use. The diagnosis lives only in
        // RunInfos, as a run-level Error.
        TestRun run = DotnetTests.ReadDirectory(
            Path.GetDirectoryName(Fixture.Resolve("Fixtures", "trx-runinfo", "App.Tests.trx"))!);

        TestAssembly crashed = run.Assemblies.Single(a => a.Name == "App.Tests");

        Assert.Equal("aborted", crashed.Status);
        Assert.Equal(3, crashed.Passed);
        Assert.False(run.Succeeded);
    }

    [Fact]
    public void TheAbortIsReadFromTheTrxRunInfoNotJustTheConsole()
    {
        TestRun run = DotnetTests.ReadDirectory(
            Path.GetDirectoryName(Fixture.Resolve("Fixtures", "trx-runinfo", "App.Tests.trx"))!);

        Assert.NotNull(run.Abort);
        Assert.Contains("Cannot create more than one System.Windows.Application instance", run.Abort);

        // And the verdict fold prefers it: the TRX's structured text survives even when the
        // console carries its own banner.
        TestRun verdict = DotnetTests.WithRunnerVerdict(run, 1, ["Test Run Aborted."]);
        Assert.Same(run.Abort, verdict.Abort);
    }

    [Fact]
    public void ANonZeroRunnerExitOverrulesCleanCounters()
    {
        // The incident's exact shape: every result the runner wrote passed, and the runner
        // said the run did not. The runner wins.
        TestRun clean = new(true, 423, 423, 0, 0, [], [new TestAssembly("A", 423, 423, 0, 0, "complete")]);

        TestRun verdict = DotnetTests.WithRunnerVerdict(clean, 1, []);

        Assert.False(verdict.Succeeded);
        Assert.Equal(1, verdict.RunnerExitCode);
    }

    [Fact]
    public void AZeroExitLeavesAPassingRunPassing()
    {
        TestRun clean = new(true, 5, 5, 0, 0, [], [new TestAssembly("A", 5, 5, 0, 0, "complete")]);

        TestRun verdict = DotnetTests.WithRunnerVerdict(clean, 0, []);

        Assert.True(verdict.Succeeded);
        Assert.Equal(0, verdict.RunnerExitCode);
        Assert.Null(verdict.Abort);
    }

    [Fact]
    public void TheAbortBannerIsCapturedFromTheRunnersOutput()
    {
        string[] lines =
        [
            "Passed!  - Failed:     0, Passed:   417, Skipped:     0, Total:   417",
            "",
            "The active test run was aborted. Reason: Test host process crashed :",
            "Unhandled exception. System.InvalidOperationException: Cannot create more than one System.Windows.Application instance in the same AppDomain.",
            "   at System.Windows.Application..ctor()",
            "",
            "Test Run Aborted.",
        ];

        string? abort = DotnetTests.ReadAbort(lines);

        Assert.NotNull(abort);
        Assert.Contains("Test host process crashed", abort);
        Assert.Contains("Cannot create more than one System.Windows.Application instance", abort);
    }

    [Fact]
    public void NoBannerMeansNoAbort()
    {
        Assert.Null(DotnetTests.ReadAbort(
            ["Passed!  - Failed:     0, Passed:     6, Skipped:     0, Total:     6"]));
    }
}
