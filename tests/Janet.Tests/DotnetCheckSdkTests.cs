using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The target's global.json picks the SDK, not the directory janet happens to run in.
/// </summary>
/// <remarks>
/// The dotnet host resolves global.json from the WORKING DIRECTORY upward, not from the project
/// path. dotnet_check used to start dotnet without one, so every child inherited janet's own
/// directory -- and JanetHome's SDK pin built every repository on the machine. Harmless while
/// every repo was on 10; on 2026-11-24 a .NET 11 repository pinning an installed 11.0.100 failed
/// NETSDK1045 out of SDK 10.0.404. Only a real build can catch this: no argument list names an
/// SDK, so a text-only test passes either way.
/// <para>
/// The fixture pins an installed SDK OTHER than the one this process's directory resolves, so
/// the two answers differ and a dotnet that inherits this process's directory gets the wrong
/// one. Today this process runs under JanetHome (10.0.100/latestFeature, so 10.0.404) and the
/// fixture pins 11.0.100; once JanetHome moves to 11 the choice flips to 10.0.404 by itself.
/// With a single SDK installed there is nothing to choose between and the test cannot
/// discriminate -- it still runs, pinned to the only SDK there is.
/// </para>
/// <para>
/// "The build used it" is read from the build, not inferred from the sdk field: the project
/// emits a warning carrying $(NETCoreSdkVersion), which is the version of the SDK whose MSBuild
/// evaluated it. That is how the second leak was found: with the working directory fixed, the
/// sdk field read 11.0.100 while the build reported 10.0.404, because this test host -- a child
/// of `dotnet test` -- carried MSBuildSDKsPath and MSBuildExtensionsPath from SDK 10, and a child
/// dotnet honours those over its own. DotnetCheck.SdkPinningVariables scrubs them.
/// </para>
/// <para>
/// In ProcessEnvironmentCollection because DotnetCheckStartInfoTests sets those variables on
/// this process, and a build started in that window would inherit them.
/// </para>
/// </remarks>
[Trait("Category", "Live")]
[Collection(ProcessEnvironmentCollection.Name)]
public class DotnetCheckSdkTests
{
    private const string SdkWarningCode = "JANET901";

    [Fact]
    public void TheTargetsGlobalJsonPicksTheSdkForTheBuildAndTheEnvelope()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"janet-sdk-{Guid.NewGuid():n}");
        Directory.CreateDirectory(directory);

        try
        {
            // What a dotnet started without a working directory would resolve.
            string? ambient = DotnetCheck.ResolveSdk(Environment.CurrentDirectory, CancellationToken.None);
            string[] installed = InstalledSdks(directory);

            Assert.NotEmpty(installed);

            string pinned = installed.FirstOrDefault(version => version != ambient) ?? installed[0];

            File.WriteAllText(
                Path.Combine(directory, "global.json"),
                $$"""{ "sdk": { "version": "{{pinned}}", "rollForward": "disable" } }""");

            string project = Path.Combine(directory, "Probe.csproj");
            File.WriteAllText(
                project,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <Target Name="ReportSdk" AfterTargets="Build">
                    <Warning Code="{SdkWarningCode}" Text="built-by-sdk $(NETCoreSdkVersion)" />
                  </Target>
                </Project>
                """);

            CheckResult result = DotnetCheck.Run(new CheckRequest
            {
                Target = project,
                NoTests = true,
                NoGraph = true,
            });

            string errors = string.Join(
                Environment.NewLine,
                result.Build.Errors.Select(e => $"{e.Code}: {e.Message}"));

            Assert.True(result.Build.Succeeded, $"the build failed:{Environment.NewLine}{errors}");

            Assert.Equal(pinned, result.Sdk);

            WarningGroup reported = Assert.Single(result.Build.Warnings, group => group.Code == SdkWarningCode);
            Assert.Equal($"built-by-sdk {pinned}", reported.Instances[0].Message);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
            catch (UnauthorizedAccessException) { /* likewise */ }
        }
    }

    [Fact]
    public void ADirectoryPinningAnSdkThatIsNotInstalledResolvesNone()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"janet-sdk-{Guid.NewGuid():n}");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(
                Path.Combine(directory, "global.json"),
                """{ "sdk": { "version": "1.0.999", "rollForward": "disable" } }""");

            // Null, not the host's complaint read as a version string.
            Assert.Null(DotnetCheck.ResolveSdk(directory, CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
            catch (UnauthorizedAccessException) { /* likewise */ }
        }
    }

    /// <summary>Every installed SDK version, from `dotnet --list-sdks`.</summary>
    private static string[] InstalledSdks(string workingDirectory)
    {
        ProcessCapture listed = ProcessOutput.Capture(
            DotnetCheck.StartInfo("--list-sdks", [], workingDirectory),
            CancellationToken.None);

        return
        [
            .. listed.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ')[0])
        ];
    }
}
