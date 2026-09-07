using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Where the test time went, and where the evidence for it is.
/// </summary>
/// <remarks>
/// Added at envelope contract 7, after a gamehub suite spent roughly 60 percent of every run
/// inside one class of 16 tests and went unnoticed through an evening of full runs by several
/// sessions. Nothing was broken: the envelope carried counts and no time at all, so the tool
/// this machine tells its sessions to use instead of raw dotnet test was also the tool that
/// hid the number that would have shown it.
/// <para>
/// The fixture is deliberately built so wall clock and summed test time DISAGREE -- 90s of
/// tests inside 30s of clock -- because they genuinely do: xUnit runs collections in parallel
/// and defaults to one collection per class. Measured on this repo's own suite the same day:
/// 17.07s of test time in 3.96s of clock, 4.31x on 24 processors. A single field called
/// "duration" would have to be one or the other and would read as both.
/// </para>
/// </remarks>
public class DotnetCheckTimingTests
{
    private static TestRun Read() =>
        DotnetTests.ReadDirectory(
            Path.GetDirectoryName(Fixture.Resolve("Fixtures", "trx-slow", "Nav.Viewer.Tests.trx"))!);

    [Fact]
    public void WallClockComesFromTheRunsOwnTimes()
    {
        Assert.Equal(30, Read().DurationSeconds);
    }

    [Fact]
    public void TestTimeIsSummedPerTestAndOutrunsTheClock()
    {
        TestRun run = Read();

        Assert.Equal(90, run.TestTimeSeconds);

        // The point of carrying both. Collapsing them into one number is the thing this
        // fixture exists to forbid.
        Assert.True(run.TestTimeSeconds > run.DurationSeconds);
    }

    [Fact]
    public void TheSlowestClassIsNamedFirstWithItsShare()
    {
        TestClassTime slowest = Read().Slowest[0];

        Assert.Equal("Sample.Nav.Viewer.Tests.CameraTests", slowest.Class);
        Assert.Equal(80, slowest.TestTimeSeconds);
    }

    [Fact]
    public void ATheorysCasesCountTowardsTheirClassRatherThanSplittingIt()
    {
        // CameraTests holds four tests, two of which are cases of one theory and carry their
        // arguments in the test name ("Fits(zoom: 1)"). Bucketing by trimming the name would
        // report three classes of one or two; the class is joined from TestDefinitions on
        // testId precisely so it does not.
        TestClassTime slowest = Read().Slowest[0];

        Assert.Equal(4, slowest.Tests);
    }

    [Fact]
    public void ClassesAreOrderedSlowestFirst()
    {
        IReadOnlyList<TestClassTime> slowest = Read().Slowest;

        Assert.Equal(2, slowest.Count);
        Assert.Equal("Sample.Nav.Viewer.Tests.GridTests", slowest[1].Class);
        Assert.Equal(10, slowest[1].TestTimeSeconds);
    }

    [Fact]
    public void EveryAssemblyCarriesItsOwnTimeAndTheFileItWasReadFrom()
    {
        TestAssembly assembly = Assert.Single(Read().Assemblies);

        Assert.Equal(30, assembly.DurationSeconds);
        Assert.Equal(90, assembly.TestTimeSeconds);

        // The provenance half of contract 7: a session that wants something the envelope did
        // not think to include can open the TRX rather than re-run the suite to regenerate it.
        Assert.NotNull(assembly.ResultsFile);
        Assert.EndsWith("Nav.Viewer.Tests.trx", assembly.ResultsFile);
        Assert.True(File.Exists(assembly.ResultsFile));
    }

    [Fact]
    public void TheRunNamesTheDirectoryTheResultsAreIn()
    {
        TestRun run = Read();

        Assert.NotNull(run.ResultsDirectory);
        Assert.True(Directory.Exists(run.ResultsDirectory));
    }

    [Fact]
    public void ATrxWithNoResultsContributesNoTimeAndNoClasses()
    {
        // trx-aborted holds three files, one of them abandoned before it wrote a single
        // result. It must add nothing rather than a zero: a row reading 0s invites the reader
        // to conclude the assembly was fast, when the truth is that it never reported.
        TestRun run = DotnetTests.ReadDirectory(
            Path.GetDirectoryName(
                Fixture.Resolve("Fixtures", "trx-aborted", "user_MACHINE_2026-08-30_14_59_52_net10.0.trx"))!);

        TestAssembly resultless = run.Assemblies
            .Single(a => a.Name == "user_MACHINE_2026-08-30_14_59_52_net10.0.trx");

        Assert.Equal(0, resultless.TestTimeSeconds);
        Assert.Equal(0, resultless.DurationSeconds);

        // Only the two assemblies that did report show up as classes.
        Assert.Equal(2, run.Slowest.Count);
        Assert.DoesNotContain(run.Slowest, entry => entry.Tests == 0);
    }
}
