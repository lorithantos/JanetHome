using Janet.Core;
using Janet.Mcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Janet.Mcp;

/// <summary>
/// Builds the HTTP transport's application: services, the loopback guard and bearer check, and
/// every endpoint.
/// </summary>
/// <remarks>
/// Lifted out of Program.cs on 2026-10-21 so a test can host exactly what production hosts. The
/// security's ordering is the thing worth pinning -- it must run before /health, /az/token and
/// the MCP endpoint answer anything -- and a test that assembled its own pipeline would pin only
/// its own ordering. Program.cs still owns probing, binding and the race handling, and it owns
/// the <see cref="BearerAuthenticator"/>: it creates it with the real token provider and
/// disposes it when the server stops. Tests pass one over a provider of their own.
/// </remarks>
internal static class HttpServer
{
    /// <summary>
    /// The application for <paramref name="context"/>'s graph, listening on 127.0.0.1 at
    /// <paramref name="port"/> once started. Port 0 lets the OS choose, which is what tests use.
    /// </summary>
    /// <exception cref="StartupRefusedException">
    /// <paramref name="bearer"/> is in Require mode and its provider returned no token: a server
    /// that would answer every request 401 is refused at startup instead.
    /// </exception>
    internal static WebApplication Build(GraphContext context, int port, BearerAuthenticator bearer)
    {
        ArgumentNullException.ThrowIfNull(bearer);
        string graphPath = context.GraphPath;

        // Before anything is built: Require with no token is a server nobody can use, and it
        // would look healthy -- /health is exempt -- while refusing everything else.
        BearerAuthSnapshot auth = bearer.Snapshot();
        if (bearer.Mode == BearerMode.Require && !auth.TokenConfigured)
        {
            throw new StartupRefusedException(
                "janet-mcp: --auth require needs the MCP bearer token, and there is none" +
                (auth.ProviderError is null ? string.Empty : $" ({auth.ProviderError})") + ". " +
                "Run `janet mcp-token --ensure` to create it, then start the server again. " +
                "--auth report or --auth off starts without one.");
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(context);
        // Tried and removed: pushing NotificationMethods.ToolListChangedNotification out of band
        // via RunSessionHandler, three seconds after each session established. The push happened
        // (the server logged it) and an attached session's tool list still did not refresh. Nor
        // did sending it from inside a tool call. Measurements are on note.janet-mcp-port; the
        // short version is that a client's tool LIST is fixed at connection time and nothing the
        // server says changes that.
        //
        // The assembly is named rather than left to WithToolsFromAssembly's default, which is the
        // CALLING assembly: correct from here, and wrong the moment a test calls this method.
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(HttpServer).Assembly)
            .WithRequestFilters(Surfaced.Filter);

        WebApplication app = builder.Build();

        // FIRST, before anything is mapped: a request with a forged Host or Origin, or a POST that
        // is not JSON, is answered by the guard and reaches no endpoint; then, unless --auth is
        // off, the bearer check (401 under require, counted under report). /health is exempt from
        // the bearer check, never from the guard. Both live in the Janet.Mcp.Security package.
        app.UseMcpSecurity(bearer);

        app.MapMcp();

        // Lets another instance tell "a janet-mcp for my graph" from "someone else's port", and
        // carries the bearer check's state: mode, whether a token is configured, the outcome
        // counters and any provider error. The snapshot holds no token, expected or received.
        app.MapGet("/health", () => new HealthReport(
            Health.Name,
            graphPath,
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            bearer.Snapshot()));

        // The CLI's way in. Not a second implementation: it calls the same AzToken.Acquire and the
        // same AzTokenJson as the az_token tool, so the two front ends cannot drift about what an
        // envelope means. It exists because a fresh CLI process has no cache and this one does.
        //
        // Plain HTTP rather than the MCP tool because the CLI would otherwise need an MCP client, a
        // session handshake, and a package it does not currently reference -- three round trips and
        // a dependency to reach six lines of Core.
        //
        // Loopback only, like everything else this server maps. Worth being explicit that this
        // hands a bearer token to any local process that asks: that is the same access any local
        // process already has by running `az account get-access-token` itself, so the boundary is
        // unchanged. It would NOT be, the moment this server bound anything but 127.0.0.1 -- or
        // answered a browser that had been rebound to 127.0.0.1, which is what the guard above
        // refuses (MEASURED 2026-10-21: before it, a forged Host and Origin got a token here).
        app.MapGet("/az/token", (string? scope, string? tenant, bool? raw, bool? refresh) =>
        {
            try
            {
                return Results.Text(
                    AzTokenJson.Serialize(AzToken.Acquire(new AzTokenRequest
                    {
                        Scope = scope ?? AzToken.DefaultResource,
                        Tenant = tenant,
                        Raw = raw ?? false,
                        Refresh = refresh ?? false,
                    })),
                    "application/json");
            }
            catch (GraphException ex)
            {
                // 400 with the message in a field the caller can find. The CLI forwards this rather
                // than retrying locally, because a refusal here is one it would reach itself.
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        });

        app.Urls.Add($"http://127.0.0.1:{port}");
        return app;
    }
}

/// <summary>The server cannot usefully start as configured; the message says what to do.</summary>
internal sealed class StartupRefusedException(string message) : Exception(message);
