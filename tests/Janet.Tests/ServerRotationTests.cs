using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The native front door to Update-McpServer.ps1: which arguments each server gets, the detached
/// launch, and reading the outcome back from files.
/// </summary>
/// <remarks>
/// Nothing here starts pwsh or touches a real server. The launcher is a stub that records the
/// ProcessStartInfo and returns a pid; liveness is a stub too; and every file lives in a private
/// temp directory -- a fake JanetHome checkout with the script, the project and a .mcp.json.
/// </remarks>
public sealed class ServerRotationTests : IDisposable
{
    private readonly string _base;
    private readonly string _state;

    public ServerRotationTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "janet-tests", "rotation-" + Guid.NewGuid().ToString("n")[..8]);
        _base = Path.Combine(root, "JanetHome");
        _state = Path.Combine(root, "state");

        Directory.CreateDirectory(Path.Combine(_base, "scripts"));
        Directory.CreateDirectory(Path.Combine(_base, "src", "Janet.Mcp"));
        File.WriteAllText(Path.Combine(_base, "scripts", "Update-McpServer.ps1"), "# stand-in");
        File.WriteAllText(Path.Combine(_base, "src", "Janet.Mcp", "Janet.Mcp.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_base, ".mcp.json"), """
            { "mcpServers": {
                "janet": { "type": "http", "url": "http://127.0.0.1:7717/" },
                "razorgraph": { "type": "http", "url": "http://127.0.0.1:7718/" } } }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_base)!, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    [Fact]
    public void JanetIsRotatedFromItsCheckoutWithItsPortAndTheCli()
    {
        RotationTarget target = ServerRotation.Target("janet", _base);

        Assert.Equal("janet-mcp", target.ProcessName);
        Assert.Equal(Path.Combine(_base, ".janet-bin"), target.Root);
        Assert.Equal(Path.Combine(_base, "src", "Janet.Mcp", "Janet.Mcp.csproj"), target.Project);

        // --port is what makes the script wait for the new server to LISTEN.
        Assert.Equal(["--http", "--base", _base, "--port", "7717"], target.ServerArguments);
        Assert.Equal(7717, target.Port);
        Assert.Equal([Path.Combine(_base, "src", "Janet.Cli", "Janet.Cli.csproj")], target.ToolProjects);
    }

    [Fact]
    public void RazorGraphGetsNoBaseBecauseAnUnknownArgumentStopsItsStartup()
    {
        RotationTarget target = ServerRotation.Target("RazorGraph", _base);

        Assert.Equal("RazorGraph.Mcp", target.ProcessName);
        Assert.Equal(["--http", "--port", "7718"], target.ServerArguments);
        Assert.EndsWith(Path.Combine("src", "RazorGraph.Mcp", "RazorGraph.Mcp.csproj"), target.Project);
        Assert.EndsWith(".mcp-bin", target.Root);
        Assert.Empty(target.ToolProjects);
    }

    [Fact]
    public void AnUnknownServerIsRefusedWithTheKnownOnes()
    {
        GraphException refused = Assert.Throws<GraphException>(() => ServerRotation.Target("postgres", _base));

        Assert.Contains("janet, razorgraph", refused.Message);
    }

    [Fact]
    public void NothingRecordedReadsAsNever()
    {
        RotationStatus status = ServerRotation.Status("janet", _base, _state, _ => false);

        Assert.Equal("never", status.State);
        Assert.Null(status.StartedAt);
        Assert.Empty(status.LogTail);
    }

    [Fact]
    public void StartingLaunchesDetachedAndAnswersStartedAtOnce()
    {
        ProcessStartInfo? launched = null;

        RotationStatus status = ServerRotation.Start("janet", _base, info => { launched = info; return 4242; }, _state, _ => true);

        Assert.Equal("started", status.State);
        Assert.Equal(4242, status.LauncherPid);
        Assert.NotNull(status.StartedAt);

        // Detached: nothing redirected to this process, whose death must not take the launcher with it.
        Assert.NotNull(launched);
        Assert.Equal("pwsh", launched!.FileName);
        Assert.False(launched.RedirectStandardOutput);
        Assert.False(launched.RedirectStandardError);
        Assert.False(launched.UseShellExecute);

        // The encoded command is exactly the wrapper Command builds.
        string encoded = launched.ArgumentList[launched.ArgumentList.IndexOf("-EncodedCommand") + 1];
        string decoded = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Contains("Update-McpServer.ps1", decoded);
        Assert.Contains("-ProcessName 'janet-mcp'", decoded);
    }

    [Fact]
    public void ASecondRotationIsRefusedWhileTheFirstRuns()
    {
        ServerRotation.Start("janet", _base, _ => 4242, _state, _ => true);

        GraphException refused = Assert.Throws<GraphException>(() =>
            ServerRotation.Start("janet", _base, _ => throw new InvalidOperationException("must not launch"), _state, _ => true));

        Assert.Contains("still running", refused.Message);
        Assert.Contains("4242", refused.Message);
    }

    [Fact]
    public void ALauncherThatVanishedWithoutAResultIsUnknownAndDoesNotBlockTheNext()
    {
        ServerRotation.Start("janet", _base, _ => 4242, _state, _ => true);

        Assert.Equal("unknown", ServerRotation.Status("janet", _base, _state, _ => false).State);

        // Not "running", so a new rotation is allowed.
        RotationStatus again = ServerRotation.Start("janet", _base, _ => 5151, _state, _ => false);
        Assert.Equal(5151, again.LauncherPid);
    }

    [Fact]
    public void AFailedRotationCarriesTheScriptsErrorAndTheLogTail()
    {
        ServerRotation.Start("janet", _base, _ => 4242, _state, _ => true);

        File.WriteAllLines(Path.Combine(_state, "rotation-janet.log"), [.. Enumerable.Range(1, 20).Select(i => $"line {i}")]);
        File.WriteAllText(
            Path.Combine(_state, "rotation-janet.result.json"),
            """{"ok":false,"error":"publish failed; current still points at the previous build.","finishedAt":"2026-10-08T23:30:00.0000000Z"}""");

        RotationStatus status = ServerRotation.Status("janet", _base, _state, _ => false);

        Assert.Equal("failed", status.State);
        Assert.StartsWith("publish failed", status.Error);
        Assert.Equal("2026-10-08T23:30:00.0000000Z", status.FinishedAt);
        Assert.Equal(15, status.LogTail.Count);
        Assert.Equal("line 20", status.LogTail[^1]);
    }

    [Fact]
    public void ASucceededRotationReadsAsSucceededEvenThoughTheLauncherIsGone()
    {
        ServerRotation.Start("janet", _base, _ => 4242, _state, _ => true);
        File.WriteAllText(Path.Combine(_state, "rotation-janet.result.json"), """{"ok":true,"error":null,"finishedAt":"2026-10-08T23:31:00Z"}""");

        RotationStatus status = ServerRotation.Status("janet", _base, _state, _ => false);

        Assert.Equal("succeeded", status.State);
        Assert.Null(status.Error);
    }

    [Fact]
    public void TheWrapperWritesAResultWhateverTheScriptDoes()
    {
        RotationTarget target = ServerRotation.Target("janet", _base);

        string command = ServerRotation.Command(target, @"C:\it's\Update-McpServer.ps1", @"C:\log.txt", @"C:\result.json");

        // Single quotes doubled, so a path with an apostrophe cannot end the string early.
        Assert.Contains(@"& 'C:\it''s\Update-McpServer.ps1'", command);
        Assert.Contains($"-ServerArgument @('--http', '--base', '{_base}', '--port', '7717')", command);
        Assert.Contains("-ToolProject @(", command);

        // Output to the log, and the result written in finally -- a script that throws still
        // leaves a result, or the rotation would read as running forever.
        Assert.Contains(@"*> 'C:\log.txt'", command);
        int finallyAt = command.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyAt > 0);
        Assert.True(command.IndexOf(@"Set-Content -LiteralPath 'C:\result.json'", StringComparison.Ordinal) > finallyAt);
    }

    [Fact]
    public void TheEnvelopeSerializesEveryKey()
    {
        RotationStatus status = ServerRotation.Status("janet", _base, _state, _ => false);

        JsonObject json = JsonNode.Parse(ServerRotationJson.Serialize(status))!.AsObject();

        Assert.Equal(RotationStatus.ContractVersion, json["contract"]!.GetValue<int>());
        Assert.Equal("never", json["state"]!.GetValue<string>());
        Assert.Equal(7717, json["target"]!["port"]!.GetValue<int>());
    }
}
