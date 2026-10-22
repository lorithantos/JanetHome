using System.ComponentModel;
using Janet.Core;
using ModelContextProtocol.Server;

namespace Janet.Mcp;

/// <summary>
/// A live endpoint, asked from this machine, answered as fields.
/// </summary>
/// <remarks>
/// The native answer to "is it up": before this, sessions reached for Claude Code's WebFetch, which
/// reports no status or headers and caches each URL for fifteen minutes. Read-only (GET, HEAD,
/// OPTIONS) so it can be allow-listed. The CLI twin is `janet http-probe`; both call
/// <see cref="HttpProbe.Shared"/> and serialise through <see cref="HttpProbeJson"/>.
/// </remarks>
[McpServerToolType]
public static class HttpTools
{
    [McpServerTool(Name = "http_probe")]
    [Description(
        "READ-ONLY. One real HTTP request from this machine to a URL -- no cache, no cookies, no " +
        "redirects followed -- reported as fields: status, reason, response headers, elapsed ms " +
        "per attempt, and the body capped at bodyCap bytes with bodyTruncated. 'ok' is true when " +
        "the status equals expectStatus, or is any 2xx when expectStatus is omitted. RETRIES " +
        "ARE FOR WARM-UPS: with retries > 0 it asks again, delayMs apart, only while the answer " +
        "is warming -- no HTTP answer at all (refused, reset, timed out) or a status in " +
        "warmingStatuses (default [503]; pass e.g. [502, 503, 404] for a slot swap). Any other " +
        "status ends the probe at once. NEVER THROWS: a transport failure comes back with status " +
        "null and 'error' set, and a request that cannot be attempted (not http or https, a " +
        "method other than GET, HEAD or OPTIONS, a malformed header) comes back refused with no " +
        "attempts. Only the headers you pass are sent -- no credentials of janet's -- and they " +
        "are never echoed.")]
    public static string Probe(
        [Description("The http:// or https:// URL to request.")]
        string url,
        [Description("GET (default), HEAD or OPTIONS.")]
        string? method = null,
        [Description("The status that counts as success. Omit for any 2xx.")]
        int? expectStatus = null,
        [Description("Further attempts after the first, made only while the answer is warming. 0 (default) to 20.")]
        int retries = 0,
        [Description("Milliseconds between attempts. Default 5000, at most 60000.")]
        int delayMs = HttpProbe.DefaultDelayMs,
        [Description("Statuses that mean 'not up yet, ask again'. Default [503]. No HTTP answer at all always counts.")]
        int[]? warmingStatuses = null,
        [Description("Per-attempt timeout in milliseconds, covering the body read too. Default 30000, at most 120000.")]
        int timeoutMs = HttpProbe.DefaultTimeoutMs,
        [Description("Request headers, each 'Name: value'. Sent as given and never echoed back.")]
        string[]? headers = null,
        [Description("Response headers to return, by name. Omit for all of them, up to 50.")]
        string[]? responseHeaders = null,
        [Description("Bytes of the final body to return. Default 4096, at most 65536; 0 for none.")]
        int bodyCap = HttpProbe.DefaultBodyCap) =>
        HttpProbeJson.Serialize(HttpProbe.Shared.Probe(new HttpProbeRequest
        {
            Url = url,
            Method = method ?? "GET",
            ExpectStatus = expectStatus,
            Retries = retries,
            DelayMs = delayMs,
            WarmingStatuses = warmingStatuses ?? HttpProbe.DefaultWarmingStatuses,
            TimeoutMs = timeoutMs,
            Headers = headers ?? [],
            ResponseHeaders = responseHeaders ?? [],
            BodyCap = bodyCap,
        }));
}
