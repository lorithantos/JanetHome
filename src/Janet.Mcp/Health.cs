using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Janet.Mcp;

/// <summary>What a janet-mcp reports about itself at /health.</summary>
public sealed record HealthReport(
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("graph")] string Graph,
    [property: JsonPropertyName("version")] string Version);

/// <summary>
/// Decides whether starting an HTTP server on a port is necessary, redundant, or a conflict.
/// </summary>
/// <remarks>
/// Without this, a second session starting its own server crashes with an unhandled
/// AddressInUseException and a CLR exit code -- after already printing that it was listening.
/// Neither the crash nor the claim is useful. An HTTP server is shared infrastructure: a
/// second start against a healthy server serving the same graph is not an error, it is a
/// no-op, and should say so and exit 0.
/// </remarks>
public static class Health
{
    public const string Name = "janet-mcp";

    public enum PortState
    {
        /// <summary>Nothing there. Bind it.</summary>
        Free,

        /// <summary>A janet-mcp already serving this graph. Nothing to do.</summary>
        AlreadyServing,

        /// <summary>A janet-mcp, but pointed at a different graph.</summary>
        ServingAnotherGraph,

        /// <summary>Something else owns the port.</summary>
        Foreign,
    }

    /// <summary>
    /// Asks who owns a port. Only a valid janet-mcp answer is treated as occupied.
    /// </summary>
    /// <remarks>
    /// Every other outcome -- refused, timed out, garbage -- reports Free, because this probe is
    /// an optimisation and the BIND is the authority. Treating a non-answer as "occupied by
    /// something foreign" gets it backwards and refuses to start over a port that is simply
    /// empty: measured here, where connections to a dead loopback port hang for the full
    /// timeout instead of being refused, which would have blocked every start.
    ///
    /// The cost of guessing Free wrongly is bounded and self-correcting: the bind fails with
    /// AddressInUse, the caller re-probes, and the situation resolves with a real error or a
    /// clean no-op. The cost of guessing Foreign wrongly is a tool that will not start.
    /// </remarks>
    public static async Task<(PortState State, string? Detail)> ProbeAsync(int port, string graphPath)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromMilliseconds(500) };

        HealthReport? report;
        try
        {
            report = await client.GetFromJsonAsync<HealthReport>($"http://127.0.0.1:{port}/health");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or NotSupportedException or System.Text.Json.JsonException or UriFormatException)
        {
            return (PortState.Free, null);
        }

        if (report is null || report.Server != Name)
        {
            return (PortState.Free, null);
        }

        return string.Equals(report.Graph, graphPath, StringComparison.OrdinalIgnoreCase)
            ? (PortState.AlreadyServing, report.Version)
            : (PortState.ServingAnotherGraph, report.Graph);
    }
}
