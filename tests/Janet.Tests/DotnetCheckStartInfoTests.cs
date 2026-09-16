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
/// </remarks>
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

            ProcessStartInfo info = DotnetCheck.StartInfo("build", ["App.slnx", "--configuration", "Debug"]);

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
    public void TheArgumentsAreTheOnesItWasGiven()
    {
        // The scrub is a removal and must stay one: nothing is added to the command line to
        // compensate, because MSBuild resolves a solution's default platform perfectly well
        // when nothing sets one, and a platform this tool chose would be a new way to be
        // confidently wrong.
        ProcessStartInfo info = DotnetCheck.StartInfo("test", ["App.slnx", "--no-build"]);

        Assert.Equal("dotnet", info.FileName);
        Assert.Equal(["test", "App.slnx", "--no-build"], info.ArgumentList);
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

            ProcessStartInfo info = DotnetCheck.StartInfo("build", []);

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
