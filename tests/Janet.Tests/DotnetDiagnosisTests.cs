using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The sentence the envelope carries when a build stopped before it reached the repository.
/// </summary>
/// <remarks>
/// A pure function of text, so this needs neither dotnet nor a disk. The fixture is a real
/// capture: a configuration-less .slnx built with Platform=x64 in the environment, which is the
/// shape that cost a session most of an hour on 2026-09-15 -- the envelope named "Debug|x64"
/// precisely and correctly, and nothing in it could say that the tool had never asked for x64.
/// </remarks>
public class DotnetDiagnosisTests
{
    private static IReadOnlyList<Diagnostic> Rejected() =>
        [.. DotnetDiagnostics.Read(File.ReadAllLines(Fixture.Resolve("Fixtures", "msbuild-msb4126.txt")))
            .Where(d => d.Severity == "error")];

    [Fact]
    public void MSB4126IsDiagnosedWithItsProvenance()
    {
        string? diagnosis = DotnetDiagnostics.Diagnose(
            Rejected(),
            target: @"D:\Repos\Sample\App.slnx",
            platformPassed: null,
            platformScrubbed: "x64",
            declaredPlatforms: []);

        Assert.NotNull(diagnosis);

        // The rejected pair, so a reader does not have to go back to the raw error for it.
        Assert.Contains("Debug|x64", diagnosis, StringComparison.Ordinal);
        Assert.Contains("App.slnx", diagnosis, StringComparison.Ordinal);

        // What the tool passed, and what it took out of the child's environment. Without these
        // two, MSB4126 reads as a verdict on the repository.
        Assert.Contains("NO platform", diagnosis, StringComparison.Ordinal);
        Assert.Contains("Platform=x64", diagnosis, StringComparison.Ordinal);

        // What the target declares -- none, so the default applies.
        Assert.Contains("declares no platforms", diagnosis, StringComparison.Ordinal);

        // And the part that stops the next hour being spent on the wrong list.
        Assert.Contains("RESTORE NEVER RAN", diagnosis, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeclaredPlatformsAreNamedWhenThereAreSome()
    {
        string? diagnosis = DotnetDiagnostics.Diagnose(
            Rejected(),
            target: @"D:\Repos\Sample\App.sln",
            platformPassed: null,
            platformScrubbed: null,
            declaredPlatforms: ["Any CPU", "x86"]);

        Assert.NotNull(diagnosis);
        Assert.Contains("App.sln declares: Any CPU, x86.", diagnosis, StringComparison.Ordinal);

        // An environment with no Platform in it is worth saying too: it rules out the one
        // cause this tool knows how to remove, and leaves the reader looking elsewhere.
        Assert.Contains("carried no Platform variable", diagnosis, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingKnownMeansNull()
    {
        // Ordinary CS and NU diagnostics. Null is load-bearing: the envelope documents it as
        // "nothing to say", and a default sentence on every failure is one nobody reads.
        IReadOnlyList<Diagnostic> ordinary =
            [.. DotnetDiagnostics.Read(File.ReadAllLines(Fixture.Resolve("Fixtures", "msbuild-output.txt")))];

        Assert.Null(DotnetDiagnostics.Diagnose(ordinary, "App.slnx", null, "x64"));
        Assert.Null(DotnetDiagnostics.Diagnose([], "App.slnx", null, "x64"));
    }

    [Fact]
    public void ASolutionDeclaringNoPlatformsReadsAsNoneRatherThanAsAFailure()
    {
        string fixture = Fixture.Resolve("Fixtures", "anycpu-solution", "AnyCpu.slnx");

        Assert.Empty(DotnetDiagnostics.DeclaredPlatforms(fixture));

        // A project has no solution configurations at all, and a missing file is not worth
        // failing a build over: a diagnosis is best effort, always.
        Assert.Empty(DotnetDiagnostics.DeclaredPlatforms(
            Fixture.Resolve("Fixtures", "anycpu-solution", "Lib", "Lib.csproj")));
        Assert.Empty(DotnetDiagnostics.DeclaredPlatforms(
            Path.Combine(Path.GetTempPath(), $"janet-no-such-{Guid.NewGuid():n}.slnx")));
    }

    [Fact]
    public void BothSolutionFormatsAreRead()
    {
        // .slnx names its platforms as elements; .sln names them in the left half of each
        // SolutionConfigurationPlatforms entry. A diagnosis that could only read one of the
        // two would go quiet on half the solutions on this machine.
        string directory = Path.Combine(Path.GetTempPath(), $"janet-platforms-{Guid.NewGuid():n}");
        Directory.CreateDirectory(directory);

        try
        {
            string slnx = Path.Combine(directory, "Declared.slnx");
            File.WriteAllText(slnx, """
                <Solution>
                  <Configurations>
                    <Platform Name="Any CPU" />
                    <Platform Name="x64" />
                  </Configurations>
                  <Project Path="Lib/Lib.csproj" />
                </Solution>
                """);

            Assert.Equal(["Any CPU", "x64"], DotnetDiagnostics.DeclaredPlatforms(slnx));

            string sln = Path.Combine(directory, "Declared.sln");
            File.WriteAllText(sln, """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Global
                	GlobalSection(SolutionConfigurationPlatforms) = preSolution
                		Debug|Any CPU = Debug|Any CPU
                		Release|Any CPU = Release|Any CPU
                	EndGlobalSection
                	GlobalSection(ProjectConfigurationPlatforms) = postSolution
                		{00000000-0000-0000-0000-000000000000}.Debug|x64.ActiveCfg = Debug|x64
                	EndGlobalSection
                EndGlobal
                """);

            // Deduplicated, and read from the SOLUTION's section only: the per-project section
            // below it lists platforms the solution does not offer.
            Assert.Equal(["Any CPU"], DotnetDiagnostics.DeclaredPlatforms(sln));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
        }
    }
}
