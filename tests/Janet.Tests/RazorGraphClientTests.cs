using System.Net;
using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The hand-rolled MCP client against a scripted server: both framings the transport allows,
/// the session header round trip, and the failure posture (null, never a throw).
/// </summary>
public class RazorGraphClientTests
{
    /// <summary>Answers each JSON-RPC request from a script, recording what it was sent.</summary>
    private sealed class ScriptedServer(Func<string, string, string?> answer) : HttpMessageHandler
    {
        public List<(string Method, string? Session)> Seen { get; } = [];

        // The client uses HttpClient.Send, the synchronous path, so this is the override that
        // matters; the async one exists only so the handler is complete.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            string method = JsonNode.Parse(body)?["method"]?.GetValue<string>() ?? "";
            string? session = request.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? values) ? values.First() : null;
            Seen.Add((method, session));

            string? reply = answer(method, body);
            HttpResponseMessage response = new(reply is null ? HttpStatusCode.Accepted : HttpStatusCode.OK)
            {
                Content = new StringContent(reply ?? ""),
            };
            response.Headers.Add("Mcp-Session-Id", "session-1");

            return response;
        }
    }

    private static string ToolResult(string innerJson) =>
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"content\":[{\"type\":\"text\",\"text\":"
        + JsonValue.Create(innerJson)!.ToJsonString()
        + "}],\"isError\":false}}";

    private const string Initialized = """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"razorgraph"}}}""";

    private const string Graphs = """{"returned":1,"current":"janet","graphs":[{"graphId":"janet","source":"C:\\repos\\JanetHome\\JanetHome.slnx","loadedAt":"2026-09-01T10:00:00+00:00","nodes":10,"edges":20,"isCurrent":true}]}""";

    [Fact]
    public void ReadsGraphsOutOfAServerSentEventStream()
    {
        ScriptedServer server = new((method, _) => method switch
        {
            "initialize" => $"event: message\ndata: {Initialized}\n\n",
            "tools/call" => $"event: message\ndata: {ToolResult(Graphs)}\n\n",
            _ => null,
        });

        IReadOnlyList<ServedGraph>? served = new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server).TryListGraphs();

        ServedGraph graph = Assert.Single(served!);
        Assert.Equal("janet", graph.Id);
        Assert.EndsWith("JanetHome.slnx", graph.Source);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), graph.LoadedAt);
    }

    [Fact]
    public void ReadsGraphsOutOfAPlainJsonBodyToo()
    {
        ScriptedServer server = new((method, _) => method switch
        {
            "initialize" => Initialized,
            "tools/call" => ToolResult(Graphs),
            _ => null,
        });

        Assert.Single(new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server).TryListGraphs()!);
    }

    [Fact]
    public void TheSessionHeaderIsCarriedAfterInitialize()
    {
        ScriptedServer server = new((method, _) => method switch
        {
            "initialize" => Initialized,
            "tools/call" => ToolResult(Graphs),
            _ => null,
        });

        new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server).TryListGraphs();

        Assert.Equal(["initialize", "notifications/initialized", "tools/call"], server.Seen.Select(s => s.Method));
        Assert.Null(server.Seen[0].Session);
        Assert.All(server.Seen.Skip(1), s => Assert.Equal("session-1", s.Session));
    }

    [Fact]
    public void RebuildSendsTheSameIdSoTheServerReplacesInPlace()
    {
        string? sentArguments = null;
        ScriptedServer server = new((method, body) =>
        {
            if (method == "tools/call")
            {
                sentArguments = JsonNode.Parse(body)?["params"]?.ToJsonString();
                return ToolResult("""{"graphId":"janet","nodes":1,"edges":1}""");
            }

            return method == "initialize" ? Initialized : null;
        });

        DateTimeOffset? rebuilt = new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server)
            .TryRebuild("janet", @"C:\repos\JanetHome\JanetHome.slnx");

        Assert.NotNull(rebuilt);
        Assert.Contains("\"name\":\"build_solution\"", sentArguments);
        Assert.Contains("\"graphId\":\"janet\"", sentArguments);
    }

    [Fact]
    public void AToolErrorIsNullNotAThrow()
    {
        ScriptedServer server = new((method, _) => method switch
        {
            "initialize" => Initialized,
            "tools/call" => """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"boom"}],"isError":true}}""",
            _ => null,
        });

        Assert.Null(new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server).TryListGraphs());
    }

    [Fact]
    public void AServerThatIsNotThereIsNullNotAThrow()
    {
        // Port 1 on loopback: nothing listens, and the client must say so quietly.
        Assert.Null(new RazorGraphClient(new Uri("http://127.0.0.1:1/")).TryListGraphs());
    }

    [Fact]
    public void OnlyBuildableSourcesCanBeRebuilt()
    {
        Assert.False(RazorGraphClient.CanRebuild(@"C:\nowhere\saved.graph.json"));
        Assert.False(RazorGraphClient.CanRebuild(@"C:\nowhere\Missing.slnx"));
    }
}
