using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Janet.Mcp;

/// <summary>
/// Refuses any HTTP request that did not come from a loopback client talking to a loopback name:
/// a non-loopback Host (400), a cross-site Origin (403), or a POST that is not JSON (415).
/// </summary>
/// <remarks>
/// MEASURED 2026-10-21: binding 127.0.0.1 is not a boundary on its own. A probe sending
/// <c>Host: rebind.evil.example</c> and <c>Origin: http://rebind.evil.example</c> got 200 from MCP
/// <c>initialize</c> and 200 with an Azure bearer token from <c>GET /az/token</c>. That is the shape
/// of a DNS-rebinding attack: a page on an attacker's domain re-resolves that domain to 127.0.0.1,
/// and the browser then talks to this port while still sending the attacker's Host and Origin.
///
/// Three checks, each closing a different way in:
///   Host          -- only 127.0.0.1, localhost or [::1], any port. A rebound page cannot forge
///                    this: the browser sends the name it resolved, which is the attacker's.
///   Origin        -- absent is allowed (Claude Code, the janet CLI, Invoke-McpTool.ps1 and
///                    Ensure-McpServer.ps1 send none). Present, it must be http(s) on one of the
///                    same three names. "null" is refused: sandboxed frames and file: pages send it.
///   Content-Type  -- every POST must be application/json (parameters allowed). A browser can send
///                    a text/plain POST cross-site with no preflight; application/json forces one,
///                    and nothing here answers a preflight.
///
/// Hand-rolled rather than ASP.NET Core's HostFiltering because that middleware's refusal is a
/// fixed HTML page that does not say which Host was refused, it lets an empty Host through by
/// default, and it has nothing for Origin or Content-Type -- so it would be a second mechanism
/// for one of three checks. The MCP SDK (ModelContextProtocol.AspNetCore 1.4.1) has no origin
/// option either. The decisions are pure functions so the tests can hold each one to its cases;
/// <see cref="UseLoopbackGuard"/> is the thin middleware over them.
/// </remarks>
internal static class LoopbackGuard
{
    private static readonly string[] LoopbackNames = ["127.0.0.1", "localhost", "[::1]"];

    /// <summary>The longest stretch of a refused header echoed back, so the body stays short.</summary>
    private const int EchoLimit = 200;

    /// <summary>A refusal: the status to answer with and the plain-text body.</summary>
    internal readonly record struct Refusal(int Status, string Message);

    /// <summary>
    /// True when <paramref name="host"/> is a Host header naming a loopback address, with or
    /// without a port. Case-insensitive. Bare (unbracketed) IPv6 is refused, as RFC 9110 does.
    /// </summary>
    internal static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        string name;
        string rest;

        if (host[0] == '[')
        {
            int close = host.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            name = host[..(close + 1)];
            rest = host[(close + 1)..];
        }
        else
        {
            int colon = host.IndexOf(':');
            name = colon < 0 ? host : host[..colon];
            rest = colon < 0 ? string.Empty : host[colon..];
        }

        if (rest.Length > 0 && !IsPortSuffix(rest))
        {
            return false;
        }

        foreach (string loopback in LoopbackNames)
        {
            if (string.Equals(name, loopback, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a request may carry this Origin: none at all, or an http(s) origin whose host is
    /// a loopback name. "null", an empty value, a path, credentials or any other scheme is refused.
    /// </summary>
    internal static bool IsAllowedOrigin(string? origin)
    {
        if (origin is null)
        {
            return true;
        }

        string authority;
        if (origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            authority = origin["http://".Length..];
        }
        else if (origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            authority = origin["https://".Length..];
        }
        else
        {
            return false;
        }

        // An Origin is scheme://host[:port] and nothing more. Anything that could move the host
        // (userinfo) or hang text after it (path, query, fragment) is not an origin.
        if (authority.IndexOfAny(['/', '?', '#', '@', '\\']) >= 0)
        {
            return false;
        }

        return IsLoopbackHost(authority);
    }

    /// <summary>
    /// True when <paramref name="contentType"/> is application/json, ignoring case and any
    /// parameters such as charset.
    /// </summary>
    internal static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        int semicolon = contentType.IndexOf(';');
        string mediaType = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The whole decision for one request. Null means let it through. <paramref name="origins"/>
    /// is every Origin value the request carried: none is allowed, more than one is refused.
    /// </summary>
    internal static Refusal? Check(string method, string? host, StringValues origins, string? contentType)
    {
        if (!IsLoopbackHost(host))
        {
            return new Refusal(
                StatusCodes.Status400BadRequest,
                $"Refused: Host '{Echo(host)}' is not a loopback name. This server answers only " +
                "127.0.0.1, localhost or [::1].");
        }

        if (origins.Count > 1)
        {
            return new Refusal(
                StatusCodes.Status403Forbidden,
                $"Refused: {origins.Count} Origin headers ('{Echo(origins.ToString())}'). Send one or none.");
        }

        string? origin = origins.Count == 1 ? origins[0] ?? string.Empty : null;
        if (!IsAllowedOrigin(origin))
        {
            return new Refusal(
                StatusCodes.Status403Forbidden,
                $"Refused: Origin '{Echo(origin)}' is not a loopback origin. Send no Origin, or one " +
                "on 127.0.0.1, localhost or [::1].");
        }

        if (HttpMethods.IsPost(method) && !IsJsonContentType(contentType))
        {
            return new Refusal(
                StatusCodes.Status415UnsupportedMediaType,
                $"Refused: a POST here must be Content-Type application/json, not '{Echo(contentType)}'.");
        }

        return null;
    }

    /// <summary>
    /// Puts the guard in front of everything mapped after it. Call it first, before any Map, so no
    /// endpoint -- /health and /az/token included -- answers a request the guard would refuse.
    /// </summary>
    internal static IApplicationBuilder UseLoopbackGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            HttpRequest request = context.Request;
            Refusal? refusal = Check(
                request.Method,
                request.Headers.Host.ToString(),
                request.Headers.Origin,
                request.ContentType);

            if (refusal is { } r)
            {
                context.Response.StatusCode = r.Status;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                await context.Response.WriteAsync(r.Message + Environment.NewLine);
                return;
            }

            await next(context);
        });

    private static bool IsPortSuffix(string rest)
    {
        // ":" then one or more digits. An empty port (host:) is refused rather than guessed at.
        if (rest.Length < 2 || rest[0] != ':')
        {
            return false;
        }

        for (int i = 1; i < rest.Length; i++)
        {
            if (!char.IsAsciiDigit(rest[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string Echo(string? value)
    {
        if (value is null)
        {
            return "(none)";
        }

        if (value.Length == 0)
        {
            return "(empty)";
        }

        return value.Length <= EchoLimit ? value : value[..EchoLimit] + "...";
    }
}
