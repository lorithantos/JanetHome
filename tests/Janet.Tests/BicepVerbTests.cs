using System.Diagnostics;
using System.Text.Json.Nodes;
using Janet.Core;
using Janet.Mcp;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// A fact that needs the Bicep CLI, reported SKIPPED -- not passed -- on a machine without one.
/// </summary>
/// <remarks>
/// xUnit 2 has no runtime skip, so the decision is made when the attribute is constructed, by
/// running the same executable Bicep.Check would (Bicep.Executable: JANET_BICEP, else bicep on
/// PATH). Probed once per test run.
/// </remarks>
public sealed class BicepFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Absent = new(Probe);

    public BicepFactAttribute()
    {
        if (Absent.Value is string reason)
        {
            Skip = reason;
        }
    }

    private static string? Probe()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(Bicep.Executable, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return $"'{Bicep.Executable} --version' did not start.";
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(30_000) && process.ExitCode == 0
                ? null
                : $"'{Bicep.Executable} --version' did not succeed.";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"No Bicep CLI ('{Bicep.Executable}': {ex.Message}). winget install Microsoft.Bicep, or set JANET_BICEP.";
        }
    }
}

/// <summary>
/// `janet bicep` -- the CLI arm of bicep_check -- through BicepVerb, the Janet.Core seam Program.cs
/// calls, because Janet.Tests does not reference Janet.Cli.
/// </summary>
/// <remarks>
/// The exit-code and output-flag facts build their result by hand and need no Bicep. The live
/// ones run the real Bicep CLI on Fixtures\bicep and skip without it.
/// </remarks>
public class BicepVerbTests
{
    private static string Sample(string name) => Fixture.Resolve("Fixtures", "bicep", name);

    private static BicepCheckResult Result(bool succeeded) => new()
    {
        Path = @"C:\Fixture\main.bicep",
        Kind = "template",
        Succeeded = succeeded,
        Errors = succeeded ? 0 : 1,
        Warnings = 0,
        Notes = 0,
        Diagnostics = succeeded ? [] : [new BicepDiagnostic("error", "BCP057", "The name does not exist.", @"C:\Fixture\main.bicep", 5, 23)],
        BicepVersion = "Bicep CLI version 0.47.16 (3f73e1a234)",
        DurationSeconds = 1.25,
    };

    private static (int Exit, string Output) Run(string? path, bool text = false, bool pretty = false)
    {
        StringWriter output = new();
        int exit = BicepVerb.Run(path, text, pretty, output);
        return (exit, output.ToString());
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void TheExitCodeIsZeroExactlyWhenTheBuildSucceeded(bool succeeded, int expected)
    {
        Assert.Equal(expected, BicepVerb.Write(Result(succeeded), text: false, pretty: false, new StringWriter()));
    }

    [Fact]
    public void TheDefaultOutputIsTheCompactEnvelopeOnOneLine()
    {
        StringWriter output = new();
        BicepVerb.Write(Result(false), text: false, pretty: false, output);

        Assert.Equal(BicepJson.Serialize(Result(false)) + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void PrettyIndentsTheSameEnvelope()
    {
        StringWriter output = new();
        BicepVerb.Write(Result(false), text: false, pretty: true, output);

        Assert.Equal(BicepJson.Serialize(Result(false), pretty: true) + Environment.NewLine, output.ToString());
        Assert.Contains("\n  \"succeeded\": false", output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void TextIsTheTerminalRenderingNotJson()
    {
        StringWriter output = new();
        BicepVerb.Write(Result(false), text: true, pretty: false, output);

        Assert.Equal(BicepJson.Render(Result(false)), output.ToString());
        Assert.StartsWith("DOES NOT BUILD", output.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoPathIsAUsageErrorNotAnEmptySuccess(string? path)
    {
        StringWriter output = new();

        Assert.Throws<ArgumentException>(() => BicepVerb.Run(path, text: false, pretty: false, output));
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void AMissingFileThrowsBeforeAnythingIsWritten()
    {
        StringWriter output = new();
        string missing = Path.Combine(Path.GetTempPath(), "janet-no-such-" + Guid.NewGuid().ToString("N") + ".bicep");

        GraphException ex = Assert.Throws<GraphException>(() => BicepVerb.Run(missing, text: false, pretty: false, output));
        Assert.Contains("No such file", ex.Message);
        Assert.Empty(output.ToString());
    }

    [BicepFact]
    public void AWarningBuildsExitsZeroAndCarriesTheWarning()
    {
        (int exit, string output) = Run(Sample("warn.bicep"));
        JsonObject envelope = JsonNode.Parse(output)!.AsObject();

        Assert.Equal(0, exit);
        Assert.True(envelope["succeeded"]!.GetValue<bool>());
        Assert.Equal(0, envelope["errors"]!.GetValue<int>());
        Assert.Equal(1, envelope["warnings"]!.GetValue<int>());
        Assert.Equal("no-unused-params", envelope["diagnostics"]![0]!["code"]!.GetValue<string>());
    }

    [BicepFact]
    public void ABrokenTemplateExitsNonZeroWithItsErrors()
    {
        (int exit, string output) = Run(Sample("broken.bicep"));
        JsonObject envelope = JsonNode.Parse(output)!.AsObject();

        Assert.NotEqual(0, exit);
        Assert.False(envelope["succeeded"]!.GetValue<bool>());
        Assert.True(envelope["errors"]!.GetValue<int>() > 0);
        Assert.Contains(envelope["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == "BCP057");
    }

    /// <summary>
    /// The CLI arm and the bicep_check tool, on the same file, emit the same envelope -- key for
    /// key and value for value -- except durationSeconds, which is a wall clock.
    /// </summary>
    [BicepFact]
    public void TheCliArmAndTheMcpToolEmitTheSameEnvelope()
    {
        string path = Sample("broken.bicep");

        JsonObject cli = JsonNode.Parse(Run(path).Output)!.AsObject();
        JsonObject mcp = JsonNode.Parse(BicepTools.Check(path))!.AsObject();

        Assert.True(cli.Remove("durationSeconds"));
        Assert.True(mcp.Remove("durationSeconds"));
        Assert.Equal(mcp.ToJsonString(), cli.ToJsonString());
    }

    [BicepFact]
    public void AnEmptyTemplateBuildsCleanly()
    {
        (int exit, string output) = Run(Sample("empty.bicep"));
        JsonObject envelope = JsonNode.Parse(output)!.AsObject();

        Assert.Equal(0, exit);
        Assert.True(envelope["succeeded"]!.GetValue<bool>());
        Assert.Empty(envelope["diagnostics"]!.AsArray());
    }
}
