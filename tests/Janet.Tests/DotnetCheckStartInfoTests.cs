using System.Diagnostics;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// What reaches the dotnet child, asserted without starting one.
/// </summary>
/// <remarks>
/// The seam exists for this test. MSBuild promotes environment variables to global properties,
/// so a Platform the server merely INHERITED became the platform every build was asked for, and
/// no argument list anywhere named it -- which is why reading the code found nothing and why the
/// assertion has to be on the environment rather than on the arguments.
/// <para>
/// Setting Platform on this process is how the test inherits one, so it runs in
/// ProcessEnvironmentCollection: any dotnet child another class starts in that window would
/// inherit it too.
/// </para>
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public class DotnetCheckStartInfoTests
{
    [Fact]
    public void AnInheritedPlatformDoesNotReachDotnet()
    {
        string? original = Environment.GetEnvironmentVariable(DotnetCheck.ScrubbedVariable);

        try
        {
            // Process-global, hence the finally. The assertion is on the ProcessStartInfo built
            // while it is set: that is exactly the situation a vcvars64 prompt leaves behind.
            Environment.SetEnvironmentVariable(DotnetCheck.ScrubbedVariable, "x64");

            ProcessStartInfo info = DotnetCheck.StartInfo("build", ["App.slnx", "--configuration", "Debug"], @"D:\Repos\Sample");

            Assert.False(
                info.Environment.ContainsKey(DotnetCheck.ScrubbedVariable),
                "Platform reached the child environment, where MSBuild reads it as a global property.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetCheck.ScrubbedVariable, original);
        }
    }

    [Fact]
    public void AnInheritedSdkPathDoesNotReachDotnet()
    {
        // What every child of `dotnet build` or `dotnet test` inherits, a test host included.
        // A child dotnet honours these over its own, so the parent's SDK would build the target.
        Dictionary<string, string?> original = DotnetCheck.SdkPinningVariables
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);

        try
        {
            foreach (string name in DotnetCheck.SdkPinningVariables)
            {
                Environment.SetEnvironmentVariable(name, @"C:\Program Files\dotnet\sdk\10.0.404\Sdks");
            }

            ProcessStartInfo info = DotnetCheck.StartInfo("build", ["App.slnx"], @"D:\Repos\Sample");

            Assert.Equal(["MSBuildExtensionsPath", "MSBuildSDKsPath"], DotnetCheck.SdkPinningVariables.Order(StringComparer.Ordinal));
            Assert.All(DotnetCheck.SdkPinningVariables, name => Assert.False(
                info.Environment.ContainsKey(name),
                $"{name} reached the child environment, where it picks the SDK over the target's global.json."));
        }
        finally
        {
            foreach ((string name, string? value) in original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void TheArgumentsAreTheOnesItWasGiven()
    {
        // The scrub is a removal and must stay one: nothing is added to the command line to
        // compensate, because MSBuild resolves a solution's default platform perfectly well
        // when nothing sets one, and a platform this tool chose would be a new way to be
        // confidently wrong.
        ProcessStartInfo info = DotnetCheck.StartInfo("test", ["App.slnx", "--no-build"], @"D:\Repos\Sample");

        Assert.Equal("dotnet", info.FileName);
        Assert.Equal(["test", "App.slnx", "--no-build"], info.ArgumentList);
    }

    [Fact]
    public void DotnetRunsInTheDirectoryItWasGiven()
    {
        // The host resolves global.json, and so the SDK, from the working directory upward.
        // Left unset, the child inherits this process's directory and its SDK pin -- which is
        // how JanetHome's pin built a .NET 11 repository with SDK 10 (2026-11-24).
        ProcessStartInfo info = DotnetCheck.StartInfo("build", ["App.slnx"], @"D:\Repos\Sample");

        Assert.Equal(@"D:\Repos\Sample", info.WorkingDirectory);
    }

    [Fact]
    public void TheRestOfTheEnvironmentIsLeftAlone()
    {
        // Only Platform, and only here. Configuration travels on the command line, where a
        // global property beats the environment, and ProcessOutput.Capture also launches
        // graph.ps1 and the server itself -- a scrub there would be wider than the defect.
        string marker = $"JANET_TEST_{Guid.NewGuid():n}";

        try
        {
            Environment.SetEnvironmentVariable(marker, "kept");

            ProcessStartInfo info = DotnetCheck.StartInfo("build", [], @"D:\Repos\Sample");

            Assert.Equal("kept", info.Environment[marker]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(marker, null);
        }
    }

    [Fact]
    public void TheInheritedValueIsReportableSoADiagnosisCanNameIt()
    {
        string? original = Environment.GetEnvironmentVariable(DotnetCheck.ScrubbedVariable);

        try
        {
            Environment.SetEnvironmentVariable(DotnetCheck.ScrubbedVariable, "x64");
            Assert.Equal("x64", DotnetCheck.InheritedPlatform());

            // Absent and empty are the same thing to MSBuild, and both must read as "nothing
            // was scrubbed" rather than as a scrub of the empty string.
            Environment.SetEnvironmentVariable(DotnetCheck.ScrubbedVariable, null);
            Assert.Null(DotnetCheck.InheritedPlatform());
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetCheck.ScrubbedVariable, original);
        }
    }
}
