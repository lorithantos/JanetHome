using Janet.Core;
using Janet.Mcp;
using Janet.Mcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The `janet-mcp` server. Two transports, for two different moments:
//
//   stdio  -- the default, and what consumers get. Zero configuration, no port, supported by
//            every MCP client. The client owns the process.
//   http   -- opt-in, and what you develop against. The server is a SEPARATE process the client
//            dials, so it can be killed and rebuilt while a session stays open: Claude Code
//            reconnects an HTTP server automatically with exponential backoff (five attempts,
//            1s doubling), and one /mcp retry covers a rebuild that outlasts that window.
//            A stdio server is a child process the client holds open, and is never reconnected
//            automatically -- which is exactly why RazorGraph needs a publish-and-restart cycle
//            for every code change.

string? basePath = null;
string? graph = null;
bool http = false;
bool printUrl = false;
int port = 0;
BearerMode authMode = BearerMode.Require;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--base" when i + 1 < args.Length: basePath = args[++i]; break;
        case "--graph" when i + 1 < args.Length: graph = args[++i]; break;
        case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
        case "--http": http = true; break;
        case "--print-url": printUrl = true; break;
        case "--auth" when i + 1 < args.Length:
            if (!McpAuth.TryParseMode(args[++i], out authMode))
            {
                Console.Error.WriteLine($"--auth takes {string.Join(", ", McpAuth.Modes)}, not '{args[i]}'.");
                return 2;
            }

            break;
        case "--help":
            Console.Error.WriteLine("""
                janet-mcp [--base DIR] [--graph PATH] [--http [--port N] [--auth MODE]] [--print-url]

                  --base       JanetBase directory. Falls back to $env:JanetBase / $JANET_BASE.
                  --graph      Graph file name or absolute path. Defaults to research.json.
                  --http       Serve over HTTP instead of stdio, for the development loop:
                               an HTTP server survives being killed and rebuilt mid-session.
                  --port       HTTP port. Defaults to one derived from the graph path, so a
                               given JanetBase always gets the same port and two never
                               collide. Starting a second server for the same graph is a
                               no-op, not an error: sessions share one HTTP server.
                  --auth       require (default), report or off. The bearer token every HTTP
                               request but /health must carry, read from
                               HKCU\Software\Janet\McpToken (see `janet mcp-token`).
                               require answers anything else 401, and refuses to START
                               when there is no token; report lets everything through
                               and counts outcomes on /health; off checks nothing. The
                               loopback guard runs in every mode. Ignored on stdio.
                  --print-url  Print the URL this graph resolves to and exit. Use it to write
                               client config without having to start anything.
                """);
            return 0;
    }
}

// The environment a fresh sign-in would give this server, not the launching terminal's. First,
// before anything is started: every child -- dotnet, bicep, a rotation script and the server it
// starts -- inherits it. JANET_* survives, so the settings read below still arrive.
// JANET_KEEP_ENVIRONMENT=1 keeps the inherited environment instead. See ServerEnvironment.
ServerEnvironment.Apply();

// How long dotnet_check waits before handing back a handle instead of an answer. An
// environment variable rather than a flag because the only callers that care are the process
// supervisor and the harness that tests the running arm -- which is otherwise unreachable,
// since it needs a build slower than the client's patience.
if (Environment.GetEnvironmentVariable("JANET_CHECK_GRACE") is string grace &&
    double.TryParse(grace, out double graceSeconds) &&
    graceSeconds >= 0)
{
    CheckJobs.Grace = TimeSpan.FromSeconds(graceSeconds);
}

string graphPath;
try
{
    graphPath = GraphLocator.Resolve(graph, basePath);
}
catch (GraphException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

GraphContext context = new(graphPath);

// Port precedence: what you asked for, then what the config already declares, then a derived
// suggestion. The config comes SECOND rather than not at all because it is the thing the
// client actually dials -- deriving a port independently would be a second source of truth
// for an address already written down, and they would agree only by luck.
string configBase = Path.GetDirectoryName(graphPath) ?? graphPath;
string portSource = "--port";

if (port == 0)
{
    if (McpConfig.TryReadPort(configBase, "janet", out int declared, out string? source))
    {
        port = declared;
        portSource = source ?? McpConfig.FileName;
    }
    else
    {
        // Nothing has declared anything yet. Derivation exists only for this moment: it gives
        // --print-url something stable to propose for a config that does not exist.
        port = GraphLocator.DerivePort(configBase);
        portSource = "derived (no .mcp.json declares a url)";
    }
}

if (printUrl)
{
    Console.Out.WriteLine($"http://127.0.0.1:{port}/");
    return 0;
}

if (http)
{
    // An HTTP server is shared infrastructure, not a per-session child process: every session
    // dials the same one. So a second start is usually redundant rather than wrong, and
    // crashing with an unhandled AddressInUseException serves nobody.
    (Health.PortState state, string? detail) = await Health.ProbeAsync(port, graphPath);

    switch (state)
    {
        case Health.PortState.AlreadyServing:
            Console.Error.WriteLine(
                $"janet-mcp {detail} is already serving this graph on http://127.0.0.1:{port}/ -- nothing to do.");
            return 0;

        case Health.PortState.ServingAnotherGraph:
            Console.Error.WriteLine(
                $"Port {port} is serving a different graph ({detail}). Pass --port to run alongside it.");
            return 3;

        case Health.PortState.Foreign:
            Console.Error.WriteLine($"Cannot use port {port}: {detail}. Pass --port.");
            return 3;

        case Health.PortState.Free:
        default:
            break; // Nothing answered as a janet-mcp. The bind below decides.
    }

    // The bearer check asks its provider once now and again (throttled) when a token does not
    // match, so a rotated token is picked up without a restart. Owned here and disposed when
    // this block ends -- after shutdown, or on any early return below.
    using BearerAuthenticator bearer = new(authMode, ReadToken);

    // Services, the loopback guard (Host, Origin, JSON-only POST), the bearer check and every
    // endpoint live in HttpServer, so a test can host exactly this application.
    WebApplication app;
    try
    {
        app = HttpServer.Build(context, port, bearer);
    }
    catch (StartupRefusedException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 4;
    }

    try
    {
        await app.StartAsync();
    }
    catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
    {
        // Lost a race with another instance that probed free at the same moment. Re-probe:
        // if it is a janet-mcp for this graph, the job is done and this is not a failure.
        (Health.PortState raced, string? racedDetail) = await Health.ProbeAsync(port, graphPath);
        if (raced == Health.PortState.AlreadyServing)
        {
            Console.Error.WriteLine(
                $"janet-mcp {racedDetail} won the race for http://127.0.0.1:{port}/ -- nothing to do.");
            return 0;
        }

        Console.Error.WriteLine(
            $"Port {port} is in use by something that is not a janet-mcp for this graph. " +
            $"Pass --port to run elsewhere.{Environment.NewLine}{ex.Message}");
        return 3;
    }

    // Printed AFTER binding. The previous order announced it was listening and then died of
    // AddressInUseException, which is the tool asserting something it had not established.
    // Loopback only: this transport is a local development loop, not a network service.
    Console.Error.WriteLine(
        $"janet-mcp listening on http://127.0.0.1:{port}/ over graph {graphPath} [port from {portSource}] " +
        $"[auth {authMode.ToString().ToLowerInvariant()}]");
    await app.WaitForShutdownAsync();
    return 0;
}

// The real token, from HKCU\Software\Janet under DPAPI. Only janet-mcp's own process reads it this
// way; razorgraph-mcp runs `janet mcp-token --print`. An unreadable value throws, which the bearer
// check keeps as its provider error for /health rather than letting it take the server down.
static string? ReadToken()
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("The MCP token store is Windows-only (DPAPI under HKCU).");
    }

    return McpTokenStore.Default.Read();
}

HostApplicationBuilder host = Host.CreateApplicationBuilder();

// stdout carries the MCP protocol; anything else written there corrupts the stream.
host.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Quiet by default on stdio. At Information the SDK narrates every request, and a client
// that redirects stderr without draining it deadlocks once that pipe's buffer fills -- the
// server blocks on a write nobody is reading, and presents as a hang with no diagnosis.
// Set Logging__LogLevel__Default=Information to get the narration back when debugging.
host.Logging.SetMinimumLevel(LogLevel.Warning);

host.Services.AddSingleton(context);
host.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly().WithRequestFilters(Surfaced.Filter);

await host.Build().RunAsync();
return 0;


