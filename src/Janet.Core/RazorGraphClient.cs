using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>One graph a RazorGraph server holds: its id, what it was built from, and when.</summary>
public sealed record ServedGraph(string Id, string Source, DateTimeOffset LoadedAt);

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
/// </remarks>
public sealed class RazorGraphClient
{
    /// <summary>The .mcp.json entry name the convention keys on.</summary>
    public const string ServerName = "razorgraph";

    private readonly Uri _endpoint;
    private readonly HttpMessageHandler? _handler;

    public RazorGraphClient(Uri endpoint, HttpMessageHandler? handler = null)
    {
        _endpoint = endpoint;
        _handler = handler;
    }

    /// <summary>
    /// Null when the repository's .mcp.json declares no razorgraph server -- the convention is
    /// then simply not in force there.
    /// </summary>
    public static RazorGraphClient? FromRepository(string repositoryRoot) =>
        McpConfig.TryReadUrl(repositoryRoot, ServerName, out Uri? url) && url is not null
            ? new RazorGraphClient(url)
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

            if (id is null || source is null)
            {
                continue;
            }

            DateTimeOffset when = DateTimeOffset.TryParse(loadedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            served.Add(new ServedGraph(id, source, when));
        }

        return served;
    }

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

            (JsonNode? ready, string? session) = Post(client, initialize, null);
            if (ready is null)
            {
                return null;
            }

            JsonObject initialized = new()
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
            };
            Post(client, initialized, session);

            JsonObject call = Request(2, "tools/call", new JsonObject
            {
                ["name"] = tool,
                ["arguments"] = arguments,
            });

            (JsonNode? answer, _) = Post(client, call, session);
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

    private (JsonNode? Result, string? Session) Post(HttpClient client, JsonObject body, string? session)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (session is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        }

        using HttpResponseMessage response = client.Send(request);

        string? sessionOut = response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : session;

        if (!response.IsSuccessStatusCode)
        {
            return (null, sessionOut);
        }

        string content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        return (ParseEnvelope(content)?["result"], sessionOut);
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
