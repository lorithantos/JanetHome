using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The one test here that runs a real build, and the only kind that could have caught this.
/// </summary>
/// <remarks>
/// The suite deliberately avoids running dotnet -- DotnetCheckJobTests says so, and everything
/// else in this file's neighbourhood is a function of text. This one earns the exception because
/// the defect it guards is invisible to text: nothing in JanetHome ever set a platform, no
/// argument list mentioned one, and every text-only test passed for a day while the resident
/// server could not build any solution on the machine. The value came from the ENVIRONMENT of
/// the shell that started the process, which only a real child process can be asked about.
/// <para>
/// The fixture declares no configurations at all, which is the shape of an ordinary .slnx and of
/// the repository this was found in. That is load-bearing: give it a &lt;Platform Name="x64" /&gt;
/// and the test passes for the wrong reason, proving only that MSBuild accepts a platform the
/// solution declares.
/// </para>
/// </remarks>
[Trait("Category", "Live")]
public class DotnetCheckPlatformTests
{
    [Fact]
    public void ASolutionWithNoX64ConfigurationBuildsUnderAnInheritedPlatform()
    {
        string? original = Environment.GetEnvironmentVariable(DotnetCheck.ScrubbedVariable);
        string directory = Path.Combine(Path.GetTempPath(), $"janet-anycpu-{Guid.NewGuid():n}");

        try
        {
            // Built in a copy outside the repository, so the fixture project is not handed
            // JanetHome's own Directory.Build.props, and so nothing writes into the test
            // output directory while the suite is reading it.
            string solution = CopyFixture(directory);

            // Exactly what an x64 Native Tools prompt leaves in the environment of everything
            // it starts, including a janet-mcp server started from it.
            Environment.SetEnvironmentVariable(DotnetCheck.ScrubbedVariable, "x64");

            CheckResult result = DotnetCheck.Run(new CheckRequest
            {
                Target = solution,
                NoTests = true,
                NoGraph = true,
            });

            string errors = string.Join(
                Environment.NewLine,
                result.Build.Errors.Select(e => $"{e.Code}: {e.Message}"));

            Assert.DoesNotContain(
                result.Build.Errors,
                e => e.Code == DotnetDiagnostics.SolutionConfigurationInvalid);

            Assert.True(result.Build.Succeeded, $"the build failed:{Environment.NewLine}{errors}");

            // Nothing to diagnose on a build that ran: the field is not a status line.
            Assert.Null(result.Build.Diagnosis);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetCheck.ScrubbedVariable, original);

            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
            catch (UnauthorizedAccessException) { /* likewise */ }
        }
    }

    /// <summary>Copies the configuration-less fixture solution to <paramref name="directory"/>
    /// and returns the copied .slnx.</summary>
    private static string CopyFixture(string directory)
    {
        string source = Path.GetDirectoryName(Fixture.Resolve("Fixtures", "anycpu-solution", "AnyCpu.slnx"))!;

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(directory, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return Path.Combine(directory, "AnyCpu.slnx");
    }
}
