using System.Diagnostics;

namespace Janet.Core;

/// <summary>How an answer reached the caller.</summary>
public enum ServedBy
{
    /// <summary>Computed in this process. No server was involved.</summary>
    Process,

    /// <summary>Answered by a running janet-mcp over HTTP.</summary>
    Server,
}

/// <summary>What the link did, and why, when it could not use a server.</summary>
public sealed record LinkOutcome(ServedBy ServedBy, string? Body, string Detail);

/// <summary>
/// Lets the CLI borrow a running janet-mcp instead of doing the work itself.
/// </summary>
/// <remarks>
/// THE PROBLEM THIS SOLVES is narrow and specific: state that is worth keeping between calls.
/// Every `janet` invocation is a fresh process, so anything it caches dies with it -- the token
/// cache measured 1.6s per acquisition from the CLI and 0.01s from the server, and the whole of
/// that difference is process lifetime rather than anything either front end does differently.
/// A server is already running for most Janet sessions. Asking it is strictly better than
/// repeating its work.
///
/// WHAT THIS IS NOT: a general forwarding layer. Nothing else the CLI does is routed through
/// here, deliberately. The CLI exists BECAUSE hooks and shells cannot speak MCP, and a CLI that
/// stopped working when the server was down would give that back -- so every path through this
/// class ends in "use the server if it is genuinely there, otherwise do the work locally", and
/// no path ends in a failure caused by the server's absence. A cache that can take the tool
/// down is worse than no cache.
///
/// The identity of a server is THE GRAPH IT SERVES, not the port it happens to be on. Two
/// JanetBases mean two servers, and dialling the wrong one would answer confidently with
/// another repo's state. /health carries the graph path for exactly this reason.
/// </remarks>
public static class ServerLink
{
    /// <summary>
    /// How long to wait for a freshly started server to answer /health.
    /// </summary>
    /// <remarks>
    /// Bounded because the fallback is cheap. Waiting longer than the local work would have
    /// taken is a worse outcome than not using the server at all, and the server we started is
    /// still there for the next call either way -- so a timeout here costs one acquisition,
    /// not the feature.
    /// </remarks>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The port a client would be told to dial for this graph.</summary>
    /// <remarks>
    /// Same precedence the server itself uses: what .mcp.json declares, then a derived
    /// suggestion. Deriving first would invent a second source of truth for an address already
    /// written down, and the two would agree only by luck.
    /// </remarks>
    public static int ResolvePort(string graphPath)
    {
        string configBase = Path.GetDirectoryName(graphPath) ?? graphPath;

        return McpConfig.TryReadPort(configBase, "janet", out int declared, out _)
            ? declared
            : GraphLocator.DerivePort(configBase);
    }

    /// <summary>
    /// Fetches a path from the janet-mcp serving this graph, starting one first if asked to.
    /// </summary>
    /// <param name="graphPath">Identifies WHICH server. A server for another graph is not used.</param>
    /// <param name="relativeUrl">Path and query, e.g. <c>az/token?scope=arm</c>.</param>
    /// <param name="allowStart">Start a server when none is serving this graph.</param>
    /// <returns>
    /// The response body with <see cref="ServedBy.Server"/>, or <see cref="ServedBy.Process"/>
    /// and a reason. NEVER throws for an absent, broken, or foreign server: the caller's
    /// fallback is the supported path, not an error path.
    /// </returns>
    public static LinkOutcome TryFetch(string graphPath, string relativeUrl, bool allowStart)
    {
        int port;
        try
        {
            port = ResolvePort(graphPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or GraphException)
        {
            return new LinkOutcome(ServedBy.Process, null, $"could not resolve a port: {ex.Message}");
        }

        (Health.PortState state, string? detail) = Health.ProbeAsync(port, graphPath).GetAwaiter().GetResult();

        if (state == Health.PortState.ServingAnotherGraph)
        {
            // Emphatically not a server to borrow. Answering from it would be confidently wrong
            // about which repo the caller meant, which is worse than doing the work here.
            return new LinkOutcome(ServedBy.Process, null, $"port {port} serves a different graph ({detail})");
        }

        if (state != Health.PortState.AlreadyServing)
        {
            if (!allowStart)
            {
                return new LinkOutcome(ServedBy.Process, null, $"no server on port {port}, and starting one was not allowed");
            }

            if (!TryStart(graphPath, out string startDetail))
            {
                return new LinkOutcome(ServedBy.Process, null, startDetail);
            }
        }

        string? body = Get(port, relativeUrl);

        return body is null

            // Reached a healthy janet-mcp and still got nothing: almost always a server older
            // than the endpoint being asked for. Naming the port makes that diagnosable instead
            // of looking like the feature silently not working.
            ? new LinkOutcome(ServedBy.Process, null, $"the server on port {port} did not answer {relativeUrl}")
            : new LinkOutcome(ServedBy.Server, body, $"served by janet-mcp on port {port}");
    }

    private static string? Get(int port, string relativeUrl)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };

        try
        {
            HttpResponseMessage response = client.GetAsync($"http://127.0.0.1:{port}/{relativeUrl}")
                .GetAwaiter().GetResult();

            // A 4xx here is the SERVER's considered refusal -- an unknown scope alias, say -- and
            // it is the same refusal this process would have produced. Passing the body back lets
            // the caller report it rather than silently retrying locally to reach the identical
            // error a second and slower time.
            return response.IsSuccessStatusCode || (int)response.StatusCode == 400
                ? response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Starts a janet-mcp for this graph and waits for it to answer.</summary>
    /// <remarks>
    /// Started DETACHED with its streams left alone. Redirecting them and not draining is how a
    /// child process deadlocks once a pipe buffer fills -- the same failure Janet.Mcp's stdio
    /// logging comment describes -- and draining them would mean this short-lived CLI process
    /// babysitting a server meant to outlive it.
    /// </remarks>
    private static bool TryStart(string graphPath, out string detail)
    {
        string? server = FindServer(graphPath);

        if (server is null)
        {
            detail = "no janet-mcp found on PATH or under .janet-bin";
            return false;
        }

        string basePath = Path.GetDirectoryName(graphPath) ?? graphPath;

        try
        {
            ProcessStartInfo start = new(server)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            start.ArgumentList.Add("--http");
            start.ArgumentList.Add("--base");
            start.ArgumentList.Add(basePath);

            using Process? process = Process.Start(start);

            if (process is null)
            {
                detail = $"could not start {server}";
                return false;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            detail = $"could not start {server}: {ex.Message}";
            return false;
        }

        // Poll rather than sleep a fixed amount: a server that is ready in 300ms should not cost
        // the caller the whole timeout, and one that never comes up should not cost more.
        int port = ResolvePort(graphPath);
        DateTime deadline = DateTime.UtcNow + StartTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (Health.ProbeAsync(port, graphPath).GetAwaiter().GetResult().State == Health.PortState.AlreadyServing)
            {
                detail = $"started janet-mcp on port {port}";
                return true;
            }

            Thread.Sleep(150);
        }

        // It may yet come up and serve the NEXT call, which is why this is worded as a timeout
        // rather than a failure to start. Nothing is killed here: a server that is slow today is
        // still the server this machine wants.
        detail = $"started janet-mcp but it did not answer within {StartTimeout.TotalSeconds:0}s";
        return false;
    }

    /// <summary>Where a janet-mcp might be, in the order Ensure-McpServer.ps1 looks.</summary>
    private static string? FindServer(string graphPath)
    {
        string basePath = Path.GetDirectoryName(graphPath) ?? graphPath;

        // The junction Update-McpServer.ps1 maintains, first: it is the newest build, and a
        // repo that rotates its server wants the rotated one rather than whatever was installed
        // globally months ago.
        string rotated = Path.Combine(basePath, ".janet-bin", "current",
            OperatingSystem.IsWindows() ? "janet-mcp.exe" : "janet-mcp");

        if (File.Exists(rotated))
        {
            return rotated;
        }

        // Then the global tool. Resolved from PATH by name, which is the whole point of packing
        // these as global tools.
        return OperatingSystem.IsWindows() ? "janet-mcp.exe" : "janet-mcp";
    }
}
