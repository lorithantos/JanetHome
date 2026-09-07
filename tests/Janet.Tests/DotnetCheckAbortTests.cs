using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// An aborted test run must never sum to a passing one -- and a healthy one must never
/// report itself as a crash.
/// </summary>
/// <remarks>
/// These are not goldens: the behaviour is new, written after a crashed test host summed to
/// 423/423 passing with exit 0 (notes\test-count-blind-spot.md). The fixture directory models
/// that incident -- one healthy assembly, one whose TRX says Aborted, and one abandoned
/// mid-write with no test definitions to name it.
/// <para>
/// The second half of the class is the correction, and it is the direction that was missing:
/// the first fix could only be wrong by calling a crash healthy, so every test asked that
/// question, and the detection it shipped was wrong the OTHER way on two shapes nobody had a
/// fixture for. trx-failing and trx-nomatch are those shapes, recorded from a real runner on
/// 2026-09-07 rather than imagined -- one ordinary failing suite, one filter that matched
/// nothing. Both used to come back "aborted".
/// </para>
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

    private static TestRun ReadFailing() =>
        DotnetTests.ReadDirectory(
            Path.GetDirectoryName(Fixture.Resolve("Fixtures", "trx-failing", "Core.Tests.trx"))!);

    private static TestRun ReadNoMatch() =>
        DotnetTests.ReadDirectory(
            Path.GetDirectoryName(Fixture.Resolve("Fixtures", "trx-nomatch", "Core.Tests.trx"))!);

    [Fact]
    public void AnOrdinaryFailingRunIsCompleteNotAborted()
    {
        // The whole host lived, the counters are whole, one assert failed. Reported as
        // "aborted" from 2026-09-01 until this fixture existed, on every failing suite.
        TestRun run = ReadFailing();

        TestAssembly assembly = Assert.Single(run.Assemblies);

        Assert.Equal("complete", assembly.Status);
        Assert.Equal(1, run.Failed);
        Assert.False(run.Succeeded);
    }

    [Fact]
    public void AnXunitFailDiagnosticIsNotAnAbortBanner()
    {
        // xUnit files "[xUnit.net ...] Foo.Bar [FAIL]" as a run-level RunInfo outcome="Error",
        // which is the same slot a crashed host writes its banner into. The outcome attribute
        // therefore cannot be the discriminator; the text is.
        Assert.Null(ReadFailing().Abort);
    }

    [Fact]
    public void AFilterThatMatchedNothingIsEmptyNotAborted()
    {
        TestAssembly none = ReadNoMatch().Assemblies.Single(a => a.Total == 0);

        Assert.Equal("empty", none.Status);
    }

    [Fact]
    public void AnEmptyAssemblyDoesNotFailTheRun()
    {
        // The reported symptom: a narrow filter over a multi-assembly solution came back
        // succeeded:false with runnerExitCode 0 and no abort -- a pass reported as a crash.
        TestRun run = ReadNoMatch();

        Assert.True(run.Succeeded);
        Assert.Contains(run.Assemblies, a => a.Status == "complete");
        Assert.Contains(run.Assemblies, a => a.Status == "empty");
    }

    [Fact]
    public void AnEmptyAssemblyIsNamedFromTheRunInfoRatherThanItsTrxFilename()
    {
        // A TRX with no test definitions has no codeBase to read a name from, and the
        // filename fallback produced rows called "sample_SAMPLE_2026-09-06_10_17_20_net10.0".
        // The runner's own warning names the dll, so the row can say which assembly it is.
        TestAssembly none = ReadNoMatch().Assemblies.Single(a => a.Total == 0);

        Assert.Equal("Nav.Viewer.Tests", none.Name);
    }

    [Fact]
    public void AnAbortStillOutranksACleanOutcome()
    {
        // The guard on the above: relaxing the detection must not cost the case it was
        // built for. A named, counted, outcome="Failed" TRX whose RunInfo carries a real
        // crash banner is still an abort.
        TestRun run = DotnetTests.ReadDirectory(
            Path.GetDirectoryName(Fixture.Resolve("Fixtures", "trx-runinfo", "App.Tests.trx"))!);

        Assert.Equal("aborted", run.Assemblies.Single().Status);
        Assert.False(run.Succeeded);
    }
}
