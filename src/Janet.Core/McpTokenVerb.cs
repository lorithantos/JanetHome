using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Janet.Mcp.Security;

namespace Janet.Core;

/// <summary>
/// `janet mcp-token` and `janet mcp-headers`: the only way anything on this machine reads the MCP
/// bearer token (decision record: thread item 65).
/// </summary>
/// <remarks>
/// Here rather than in Janet.Cli for the reason BicepVerb gives: nothing references Janet.Cli, so
/// a verb written there has no test. Program.cs picks the flags and passes
/// <see cref="McpTokenStore.Default"/>; the tests pass a store on a throwaway key under
/// HKCU\Software\Janet.Tests, so no test ever touches the real token.
///
/// THE TOKEN GOES TO STDOUT IN EXACTLY TWO PLACES: --print (the bare token, for
/// razorgraph-mcp's --auth-token-command) and mcp-headers (the header object a Claude Code
/// headersHelper must emit). Every status line, error and message names the store's location and
/// never the value. A token that cannot be read is never replaced here except by an explicit
/// --rotate -- the store refuses to, and so does this.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class McpTokenVerb
{
    /// <summary>
    /// mcp-token: exactly one of --ensure, --print or --rotate. Returns the exit code: 0 done,
    /// 1 no token or an unreadable one, 2 a usage error.
    /// </summary>
    public static int Token(bool ensure, bool print, bool rotate, McpTokenStore store, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(store);

        int chosen = (ensure ? 1 : 0) + (print ? 1 : 0) + (rotate ? 1 : 0);
        if (chosen != 1)
        {
            error.WriteLine("mcp-token needs exactly one of --ensure, --print or --rotate.");
            return 2;
        }

        if (print)
        {
            return Print(store, output, error);
        }

        string action = ensure ? "ensure" : "rotate";
        try
        {
            if (ensure)
            {
                bool existed = store.Read() is not null;
                if (!existed)
                {
                    store.Ensure();
                }

                output.WriteLine(Status(action, store, ok: true, created: !existed, note: existed
                    ? "A token already exists; it was left unchanged."
                    : "Created a new token. Clients fetch the header with: janet mcp-headers"));
                return 0;
            }

            store.Rotate();
            output.WriteLine(Status(action, store, ok: true, created: true, note:
                $"Rotated. Running servers pick the new token up within {BearerAuthenticator.RefreshInterval.TotalSeconds:0} " +
                "seconds. A client holding the old header must fetch it again (janet mcp-headers; " +
                "Claude Code re-runs its headersHelper on reconnect)."));
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            // An unreadable value: the store refuses to overwrite it on --ensure, and says why.
            output.WriteLine(Status(action, store, ok: false, created: false, note: null, failure: ex.Message));
            error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// mcp-headers: writes exactly <c>{"Authorization":"Bearer &lt;token&gt;"}</c> to
    /// <paramref name="output"/>, which is what a Claude Code headersHelper must print. With no
    /// token, or an unreadable one, writes nothing there and returns 1 with the reason on
    /// <paramref name="error"/>.
    /// </summary>
    public static int Headers(McpTokenStore store, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (Read(store, error, "mcp-headers") is not string token)
        {
            return 1;
        }

        output.Write(new JsonObject { ["Authorization"] = "Bearer " + token }.ToJsonString());
        return 0;
    }

    private static int Print(McpTokenStore store, TextWriter output, TextWriter error)
    {
        if (Read(store, error, "mcp-token --print") is not string token)
        {
            return 1;
        }

        output.WriteLine(token);
        return 0;
    }

    private static string? Read(McpTokenStore store, TextWriter error, string verb)
    {
        try
        {
            if (store.Read() is string token)
            {
                return token;
            }

            error.WriteLine(
                $"{verb}: there is no MCP token at {store.Location}. Run `janet mcp-token --ensure` to create one.");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            error.WriteLine($"{verb}: {ex.Message}");
            return null;
        }
    }

    private static string Status(string action, McpTokenStore store, bool ok, bool created, string? note, string? failure = null) =>
        new JsonObject
        {
            ["ok"] = ok,
            ["action"] = action,
            [action == "rotate" ? "rotated" : "created"] = created,
            ["location"] = store.Location,
            ["note"] = note,
            ["error"] = failure,
        }.ToJsonString();
}
