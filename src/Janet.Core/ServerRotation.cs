using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>How one server is rotated: the arguments Update-McpServer.ps1 is given for it.</summary>
public sealed record RotationTarget(
    string Server,
    string Project,
    string Root,
    string ProcessName,
    IReadOnlyList<string> ServerArguments,
    IReadOnlyList<string> ToolProjects,
    int? Port);

/// <summary>A rotation's state, read back from the files its launcher writes.</summary>
public sealed record RotationStatus
{
    /// <summary>Format version. See contracts\server-rotation.schema.json.</summary>
    public const int ContractVersion = 1;

    public int Contract => ContractVersion;

    public required string Server { get; init; }

    /// <summary>
    /// never (no rotation recorded), started (just launched), running, succeeded, failed, or
    /// unknown (the launcher is gone and left no result -- killed, or the machine restarted).
    /// </summary>
    public required string State { get; init; }

    public required string? StartedAt { get; init; }

    public required string? FinishedAt { get; init; }

    public required int? LauncherPid { get; init; }

    /// <summary>What the rotation script threw, verbatim: a failed publish names the locked file.</summary>
    public required string? Error { get; init; }

    /// <summary>The build directory the 'current' junction points at now, or null when there is none.</summary>
    public required string? CurrentBuild { get; init; }

    public required string LogPath { get; init; }

    /// <summary>The last lines the rotation script wrote, so a failure arrives with its context.</summary>
    public required IReadOnlyList<string> LogTail { get; init; }

    public required RotationTarget Target { get; init; }

    /// <summary>What the server answering THIS call dropped from its own launcher's environment.</summary>
    public required EnvironmentReport? ServingEnvironment { get; init; }
}

/// <summary>
/// Rotates janet-mcp or razorgraph-mcp by launching scripts\Update-McpServer.ps1 DETACHED, and
/// reads the result back from files.
/// </summary>
/// <remarks>
/// ONE ROTATION FOR BOTH SERVERS, and it stays the script. Update-McpServer.ps1 already rotates
/// both -- publish to build-stamp, repoint the 'current' junction, restart, wait for the listener,
/// reinstall the CLI -- so this is a native front door to it, not a port of it, the same way
/// dotnet_check is a front door to dotnet.
///
/// DETACHED, BECAUSE A SERVER CAN BE ASKED TO ROTATE ITSELF. The script is a separate process
/// that inherits nothing it needs from the caller once started: it publishes while the old
/// server still serves, and only then stops it. A child that the server waited on, or whose
/// pipes it held, would die with it. So nothing is redirected to this process -- the launcher
/// writes its own log and result file -- and the call answers 'started' immediately. The caller
/// reconnects on its next MCP call and reads the outcome with server_rotation; the files, not
/// this process's memory, carry it across the restart.
///
/// The launcher inherits this server's environment, which <see cref="ServerEnvironment"/> has
/// already normalised, so the server it starts is clean as well -- whichever terminal started
/// the one being replaced.
/// </remarks>
public static class ServerRotation
{
    public static readonly IReadOnlyList<string> Servers = ["janet", "razorgraph"];

    /// <summary>Where the launcher's state, log and result files live. Overridable for tests.</summary>
    public static string StateDirectory(string? overridden = null) =>
        overridden ?? Path.Combine(Path.GetTempPath(), "Janet");

    /// <summary>
    /// The arguments for one server, from the JanetHome checkout and its .mcp.json.
    /// </summary>
    /// <remarks>
    /// Ports come from .mcp.json -- the address clients dial -- so they are not a third copy of a
    /// number Ensure-*.ps1 and the config already hold. A port is passed as --port so the script
    /// waits for the new server to LISTEN, not merely to exist; without it the script says it
    /// is not waiting, and the first call after a rotation can fail.
    ///
    /// RazorGraphTool's checkout defaults to C:\repos\RazorGraphTool, the same machine-specific
    /// default Ensure-RazorGraphServer.ps1 carries; JANET_RAZORGRAPH_REPO overrides it.
    /// </remarks>
    public static RotationTarget Target(string server, string janetBase)
    {
        string script = Path.Combine(janetBase, "scripts", "Update-McpServer.ps1");
        if (!File.Exists(script))
        {
            throw new GraphException($"No rotation script at {script}. Is '{janetBase}' a JanetHome checkout?");
        }

        switch (server.Trim().ToLowerInvariant())
        {
            case "janet":
            {
                int? port = McpConfig.TryReadPort(janetBase, "janet", out int declared, out _) ? declared : null;
                return new RotationTarget(
                    "janet",
                    Path.Combine(janetBase, "src", "Janet.Mcp", "Janet.Mcp.csproj"),
                    Path.Combine(janetBase, ".janet-bin"),
                    "janet-mcp",
                    ["--http", "--base", janetBase, .. port is null ? Array.Empty<string>() : ["--port", port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]],
                    [Path.Combine(janetBase, "src", "Janet.Cli", "Janet.Cli.csproj")],
                    port);
            }

            case "razorgraph":
            {
                string repo = Environment.GetEnvironmentVariable("JANET_RAZORGRAPH_REPO") is { Length: > 0 } configured
                    ? configured
                    : @"C:\repos\RazorGraphTool";
                int? port = McpConfig.TryReadPort(janetBase, "razorgraph", out int declared, out _) ? declared : null;

                // No --base: razorgraph-mcp is told which solution to graph per call, and an
                // argument it does not know makes it exit at startup.
                return new RotationTarget(
                    "razorgraph",
                    Path.Combine(repo, "src", "RazorGraph.Mcp", "RazorGraph.Mcp.csproj"),
                    Path.Combine(repo, ".mcp-bin"),
                    "RazorGraph.Mcp",
                    ["--http", .. port is null ? Array.Empty<string>() : ["--port", port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]],
                    [],
                    port);
            }

            default:
                throw new GraphException($"'{server}' is not a server this can rotate. Known: {string.Join(", ", Servers)}.");
        }
    }

    /// <summary>
    /// Launches the rotation and returns at once with state 'started'. Refuses while one for the
    /// same server is still running: two rotations would race to repoint one junction.
    /// </summary>
    /// <param name="launch">Starts the process and returns its pid. Injectable so the tests never start pwsh.</param>
    public static RotationStatus Start(
        string server,
        string janetBase,
        Func<ProcessStartInfo, int>? launch = null,
        string? stateDirectory = null,
        Func<int, bool>? isAlive = null)
    {
        RotationTarget target = Target(server, janetBase);

        if (!File.Exists(target.Project))
        {
            throw new GraphException($"No project at {target.Project} to publish for {target.Server}.");
        }

        RotationStatus previous = Status(server, janetBase, stateDirectory, isAlive);
        if (previous.State is "started" or "running")
        {
            throw new GraphException(
                $"A {target.Server} rotation started at {previous.StartedAt} is still running (launcher pid {previous.LauncherPid}). " +
                "Read it with server_rotation; two rotations would race to repoint the same junction.");
        }

        string directory = StateDirectory(stateDirectory);
        Directory.CreateDirectory(directory);
        (string state, string log, string result) = Files(directory, target.Server);

        File.Delete(result);
        File.Delete(log);

        ProcessStartInfo info = new("pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = janetBase,
        };

        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(
            Command(target, Path.Combine(janetBase, "scripts", "Update-McpServer.ps1"), log, result))));

        int pid = (launch ?? DefaultLaunch)(info);

        JsonObject record = new()
        {
            ["server"] = target.Server,
            ["startedAt"] = DateTime.UtcNow.ToString("o"),
            ["launcherPid"] = pid,
        };
        File.WriteAllText(state, record.ToJsonString());

        return Status(server, janetBase, stateDirectory, isAlive) with { State = "started" };
    }

    /// <summary>Reads the last rotation of a server back from its files.</summary>
    public static RotationStatus Status(string server, string janetBase, string? stateDirectory = null, Func<int, bool>? isAlive = null)
    {
        RotationTarget target = Target(server, janetBase);
        (string state, string log, string result) = Files(StateDirectory(stateDirectory), target.Server);

        JsonObject? started = ReadObject(state);
        JsonObject? finished = ReadObject(result);
        int? pid = started?["launcherPid"]?.GetValue<int>();

        string status =
            started is null ? "never"
            : finished is not null ? (finished["ok"]?.GetValue<bool>() == true ? "succeeded" : "failed")
            : pid is int live && (isAlive ?? IsAlive)(live) ? "running"
            : "unknown";

        return new RotationStatus
        {
            Server = target.Server,
            State = status,
            StartedAt = started?["startedAt"]?.GetValue<string>(),
            FinishedAt = finished?["finishedAt"]?.GetValue<string>(),
            LauncherPid = pid,
            Error = finished?["error"]?.GetValue<string>(),
            CurrentBuild = CurrentBuild(target.Root),
            LogPath = log,
            LogTail = Tail(log, 15),
            Target = target,
            ServingEnvironment = ServerEnvironment.Last,
        };
    }

    /// <summary>
    /// The PowerShell the launcher runs: the rotation script with its output to a log, and a
    /// result file written whatever happens. Exposed for the tests.
    /// </summary>
    /// <remarks>
    /// The script reports success by finishing and failure by throwing; it emits no result of its
    /// own. So the wrapper turns "finished" and "threw this" into a small JSON file, and the
    /// finally block writes it even when the script fails -- a rotation that left no result
    /// would read as still running forever.
    /// </remarks>
    public static string Command(RotationTarget target, string script, string log, string result)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        static string List(IEnumerable<string> values) => "@(" + string.Join(", ", values.Select(Quote)) + ")";

        StringBuilder call = new();
        call.Append("& ").Append(Quote(script))
            .Append(" -Project ").Append(Quote(target.Project))
            .Append(" -Root ").Append(Quote(target.Root))
            .Append(" -ProcessName ").Append(Quote(target.ProcessName))
            .Append(" -ServerArgument ").Append(List(target.ServerArguments));

        if (target.ToolProjects.Count > 0)
        {
            call.Append(" -ToolProject ").Append(List(target.ToolProjects));
        }

        return $$"""
            $ErrorActionPreference = 'Stop'
            $result = [ordered]@{ ok = $false; error = $null; finishedAt = $null }
            try {
                {{call}} *> {{Quote(log)}}
                $result.ok = $true
            }
            catch {
                $result.error = $_.Exception.Message
                ($_ | Out-String) | Add-Content -LiteralPath {{Quote(log)}}
            }
            finally {
                $result.finishedAt = (Get-Date).ToUniversalTime().ToString('o')
                $result | ConvertTo-Json -Compress | Set-Content -LiteralPath {{Quote(result)}} -Encoding utf8
            }
            """;
    }

    private static (string State, string Log, string Result) Files(string directory, string server) =>
        (Path.Combine(directory, $"rotation-{server}.json"),
         Path.Combine(directory, $"rotation-{server}.log"),
         Path.Combine(directory, $"rotation-{server}.result.json"));

    private static int DefaultLaunch(ProcessStartInfo info)
    {
        try
        {
            using Process process = Process.Start(info)
                ?? throw new GraphException("Could not start pwsh for the rotation. Is PowerShell 7 on PATH?");
            return process.Id;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GraphException($"Could not start pwsh for the rotation: {ex.Message}. Is PowerShell 7 on PATH?", ex);
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? CurrentBuild(string root)
    {
        try
        {
            string? target = new DirectoryInfo(Path.Combine(root, "current")).LinkTarget;
            return target is null ? null : Path.GetFullPath(target, root);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static JsonObject? ReadObject(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Tail(string path, int lines)
    {
        try
        {
            return File.Exists(path)
                ? [.. File.ReadLines(path).Where(line => line.Trim().Length > 0).TakeLast(lines)]
                : [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
