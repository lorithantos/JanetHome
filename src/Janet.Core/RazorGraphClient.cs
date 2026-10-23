using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>One graph a RazorGraph server holds: its id, what it was built from, and when.</summary>
/// <remarks>
/// BuiltAt is null on a graph saved before the server stamped it. LoadedAt alone is not a build
/// time: a graph carried over a rotation is loaded minutes after it was built, sometimes after an
/// edit it never saw.
/// </remarks>
public sealed record ServedGraph(string Id, string Source, DateTimeOffset LoadedAt, DateTimeOffset? BuiltAt = null)
{
    /// <summary>When the graph last saw the source: its build stamp, else when it was loaded.</summary>
    public DateTimeOffset Stamp => BuiltAt ?? LoadedAt;
}

/// <summary>
/// The smallest MCP client that can ask a RazorGraph HTTP server what it holds and have it
/// rebuild one graph.
/// </summary>
/// <remarks>
/// Speaks Streamable HTTP by hand -- initialize, the initialized notification, tools/call -- the
/// same three requests Ensure-McpServer.ps1 makes in PowerShell. Pulling in an MCP client library
/// for two calls would drag a host framework into Janet.Core, which the project rule forbids.
///
/// Every failure is a null answer, never an exception: this runs inside a build check, and a
/// graph is a convenience the check must not fail over. Callers report "absent" or "failed" and
/// move on, which is what the code-graph refresh has always done for its script convention.
///
/// ONE FAILURE IS NAMED, since the bearer token (2026-10-21): a 401 or 403. Every request carries
/// <c>Authorization: Bearer</c> whenever janet's own token store holds one, read through the same
/// <see cref="TokenReader"/> seam <see cref="ServerLink"/> uses. Before this, every janet check
/// after the cutover was refused by a razorgraph under --auth require and reported the refusal
/// as "absent" -- no graph -- which was confidently wrong about the repository and silent about
/// the configuration. A refusal is still a null answer, so the check still does not fail over
/// it, but <see cref="Refusal"/> says what happened, and the code graph reports "unauthorized"
/// rather than "absent". The token itself is never put in it.
/// </remarks>
public sealed class RazorGraphClient
{
    /// <summary>The .mcp.json entry name the convention keys on.</summary>
    public const string ServerName = "razorgraph";

    private readonly Uri _endpoint;
    private readonly HttpMessageHandler? _handler;
    private readonly TokenReader _readToken;

    /// <param name="endpoint">The server's MCP endpoint.</param>
    /// <param name="handler">The transport; tests script one.</param>
    /// <param name="readToken">
    /// Where the bearer token comes from; <see cref="McpAuth.TryReadToken"/> (HKCU\Software\Janet)
    /// when null. Tests pass their own. With no token the requests go without an Authorization
    /// header, which a server under --auth report or off still answers.
    /// </param>
    public RazorGraphClient(Uri endpoint, HttpMessageHandler? handler = null, TokenReader? readToken = null)
    {
        _endpoint = endpoint;
        _handler = handler;
        _readToken = readToken ?? McpAuth.TryReadToken;
    }

    /// <summary>
    /// Why the last call's server refused it (401 or 403), or null when it did not. Names the
    /// status, the endpoint and whether a token was sent; never the token.
    /// </summary>
    public string? Refusal { get; private set; }

    /// <summary>
    /// Null when the repository's .mcp.json declares no razorgraph server -- the convention is
    /// then simply not in force there.
    /// </summary>
    public static RazorGraphClient? FromRepository(string repositoryRoot, TokenReader? readToken = null) =>
        McpConfig.TryReadUrl(repositoryRoot, ServerName, out Uri? url) && url is not null
            ? new RazorGraphClient(url, readToken: readToken)
            : null;

    /// <summary>Whether a graph's source is something build_solution or build_graph can rebuild.</summary>
    public static bool CanRebuild(string source)
    {
        string extension = Path.GetExtension(source);

        return extension is ".sln" or ".slnx" or ".csproj" && File.Exists(source);
    }

    /// <summary>Every graph the server holds, or null when it did not answer.</summary>
    public IReadOnlyList<ServedGraph>? TryListGraphs()
    {
        JsonNode? result = TryCall("list_graphs", new JsonObject(), TimeSpan.FromSeconds(5));
        if (result?["graphs"] is not JsonArray graphs)
        {
            return null;
        }

        List<ServedGraph> served = [];
        foreach (JsonNode? graph in graphs)
        {
            string? id = graph?["graphId"]?.GetValue<string>();
            string? source = graph?["source"]?.GetValue<string>();
            string? loadedAt = graph?["loadedAt"]?.GetValue<string>();
            string? builtAt = graph?["builtAt"]?.GetValue<string>();

            if (id is null || source is null)
            {
                continue;
            }

            served.Add(new ServedGraph(id, source, ParseStamp(loadedAt) ?? DateTimeOffset.MinValue, ParseStamp(builtAt)));
        }

        return served;
    }

    private static DateTimeOffset? ParseStamp(string? value) =>
        DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : null;

    /// <summary>
    /// Rebuilds a graph under the same id, so the server updates it in place. Returns when the
    /// rebuild finished, or null when it did not happen.
    /// </summary>
    /// <remarks>
    /// A solution graph compiles every project, so the budget is generous. The check loop that
    /// calls this already hands back a handle when it outlasts a client, and the CLI simply waits.
    /// </remarks>
    public DateTimeOffset? TryRebuild(string graphId, string source)
    {
        string tool = Path.GetExtension(source).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            ? "build_graph"
            : "build_solution";

        JsonObject arguments = new()
        {
            ["path"] = source,
            ["graphId"] = graphId,
        };

        JsonNode? result = TryCall(tool, arguments, TimeSpan.FromMinutes(10));

        return result is null ? null : DateTimeOffset.UtcNow;
    }

    /// <summary>The tool's own JSON answer, or null for any failure at any layer.</summary>
    private JsonNode? TryCall(string tool, JsonObject arguments, TimeSpan budget)
    {
        Refusal = null;

        // Read once per call, not per request: the three requests of one exchange carry the same
        // token, and a rotated one is picked up by the next call.
        string? token = _readToken(out string? tokenError);
        Credential credential = new(token, tokenError);

        try
        {
            using HttpClient client = _handler is null
                ? new HttpClient()
                : new HttpClient(_handler, disposeHandler: false);
            client.Timeout = budget;

            JsonObject initialize = Request(1, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "janet", ["version"] = "1" },
            });

            (JsonNode? ready, string? session) = Post(client, initialize, null, credential);
            if (ready is null)
            {
                return null;
            }

            JsonObject initialized = new()
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
            };
            Post(client, initialized, session, credential);

            JsonObject call = Request(2, "tools/call", new JsonObject
            {
                ["name"] = tool,
                ["arguments"] = arguments,
            });

            (JsonNode? answer, _) = Post(client, call, session, credential);
            if (answer is null || answer["isError"]?.GetValue<bool>() == true)
            {
                return null;
            }

            string? text = answer["content"]?[0]?["text"]?.GetValue<string>();

            return text is null ? null : JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static JsonObject Request(int id, string method, JsonObject parameters) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters,
    };

    /// <summary>The token for one exchange, or why there is none. Never rendered.</summary>
    private readonly record struct Credential(string? Token, string? Error);

    private (JsonNode? Result, string? Session) Post(HttpClient client, JsonObject body, string? session, Credential credential)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (credential.Token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        }

        if (session is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        }

        using HttpResponseMessage response = client.Send(request);

        string? sessionOut = response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : session;

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            // Recorded on the first refusal and kept: the exchange stops at initialize anyway,
            // and a later notification's refusal would only repeat it.
            Refusal ??= DescribeRefusal((int)response.StatusCode, response.ReasonPhrase, credential);
        }

        if (!response.IsSuccessStatusCode)
        {
            return (null, sessionOut);
        }

        string content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        return (ParseEnvelope(content)?["result"], sessionOut);
    }

    /// <summary>
    /// One sentence naming a refusal and what to do about it. Built from the status, the endpoint
    /// and the token store's own error -- never from the token, and not from the response body,
    /// so nothing the server echoes can carry a credential into the envelope.
    /// </summary>
    private string DescribeRefusal(int status, string? reason, Credential credential)
    {
        string what = $"razorgraph at {_endpoint} refused janet with {status} {reason}".TrimEnd() + ". ";

        return what + (credential.Token is null
            ? $"No Authorization header was sent ({credential.Error ?? "no token"}). Run `janet mcp-token --ensure` to create the token; the server reads it within 30 seconds."
            : "The token from HKCU\\Software\\Janet\\McpToken was sent and refused: the server holds a different one, or has none. Check GET /health's 'auth', and restart the server if the token was rotated while it could not read it.");
    }

    /// <summary>
    /// The JSON-RPC envelope out of either framing the transport allows: a plain JSON body, or a
    /// server-sent event stream whose 'data:' lines carry it.
    /// </summary>
    public static JsonNode? ParseEnvelope(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        string trimmed = content.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            return JsonNode.Parse(trimmed);
        }

        foreach (string line in content.Split('\n'))
        {
            string candidate = line.TrimEnd('\r');
            if (!candidate.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            JsonNode? parsed = JsonNode.Parse(candidate["data:".Length..].Trim());
            if (parsed?["result"] is not null || parsed?["error"] is not null)
            {
                return parsed;
            }
        }

        return null;
    }
}
