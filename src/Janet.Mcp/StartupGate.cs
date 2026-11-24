using Janet.Core;
using Janet.Mcp.Security;

namespace Janet.Mcp;

/// <summary>
/// Whether the HTTP server has finished starting, and its bearer check once it has.
/// </summary>
/// <remarks>
/// The server binds FIRST and does its startup work after, so a client dialling during startup
/// is answered rather than refused at the socket. Until <see cref="FinishAsync"/> resolves,
/// <see cref="HttpServer"/> answers /health 503 with state "starting" and every other route 503
/// with Retry-After: 1, after the loopback guard and ahead of any bearer check -- so startup never
/// answers 401. The work itself is unchanged from when it ran before the bind: construct the
/// bearer check, which reads the token once, and refuse to start under Require with no token.
/// Only WHEN it runs moved (2026-11-24).
/// </remarks>
internal sealed class StartupGate : IDisposable
{
    private readonly object sync = new();
    private BearerAuthenticator? bearer;
    private bool disposed;

    /// <summary>The bearer check, or null while the server is starting.</summary>
    internal BearerAuthenticator? Bearer => Volatile.Read(ref bearer);

    /// <summary>Whether startup has resolved and requests go through to the endpoints.</summary>
    internal bool IsReady => Bearer is not null;

    /// <summary>
    /// Runs the startup work off the request path, then opens the gate. A provider that fails is
    /// kept as the bearer check's provider error and the gate still opens, as the server used to
    /// start anyway; Require with no token throws instead and the gate stays shut.
    /// </summary>
    /// <exception cref="StartupRefusedException">
    /// <paramref name="mode"/> is Require and the provider returned no token: a server that would
    /// answer every request 401 is refused, as it always was.
    /// </exception>
    internal async Task<BearerAuthenticator> FinishAsync(BearerMode mode, Func<string?> tokenProvider)
    {
        BearerAuthenticator ready = await Task.Run(() => StartupGate.Admit(
            new BearerAuthenticator(mode, tokenProvider, headerHint: McpAuth.HeaderHint))).ConfigureAwait(false);

        lock (sync)
        {
            if (disposed || bearer is not null)
            {
                ready.Dispose();
                throw disposed
                    ? new ObjectDisposedException(nameof(StartupGate))
                    : new InvalidOperationException("Startup has already finished.");
            }

            Volatile.Write(ref bearer, ready);
        }

        return ready;
    }

    /// <summary>Disposes the bearer check, if startup got that far.</summary>
    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            bearer?.Dispose();
        }
    }

    /// <summary>
    /// <paramref name="candidate"/>, unless it is Require with no token: a server nobody can use,
    /// which would look healthy -- /health is exempt -- while refusing everything else.
    /// </summary>
    private static BearerAuthenticator Admit(BearerAuthenticator candidate)
    {
        BearerAuthSnapshot auth = candidate.Snapshot();
        if (candidate.Mode != BearerMode.Require || auth.TokenConfigured)
        {
            return candidate;
        }

        candidate.Dispose();
        throw new StartupRefusedException(
            "janet-mcp: --auth require needs the MCP bearer token, and there is none" +
            (auth.ProviderError is null ? string.Empty : $" ({auth.ProviderError})") + ". " +
            "Run `janet mcp-token --ensure` to create it, then start the server again. " +
            "--auth report or --auth off starts without one.");
    }
}
