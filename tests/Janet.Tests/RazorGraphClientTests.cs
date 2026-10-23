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
    private sealed class ScriptedServer(Func<string, string, string?> answer, HttpStatusCode? refuseWith = null) : HttpMessageHandler
    {
        public List<(string Method, string? Session)> Seen { get; } = [];

        /// <summary>Each request's Authorization header as sent, null where there was none.</summary>
        public List<string?> Authorizations { get; } = [];

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
            Authorizations.Add(request.Headers.Authorization?.ToString());

            if (refuseWith is HttpStatusCode refused)
            {
                // What Janet.Mcp.Security answers under --auth require: no session, a short body.
                return new HttpResponseMessage(refused) { Content = new StringContent("missing bearer token") };
            }

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

        IReadOnlyList<ServedGraph>? served = new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server, NoToken).TryListGraphs();

        ServedGraph graph = Assert.Single(served!);
        Assert.Equal("janet", graph.Id);
        Assert.EndsWith("JanetHome.slnx", graph.Source);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), graph.LoadedAt);
        Assert.Null(graph.BuiltAt);
        Assert.Equal(graph.LoadedAt, graph.Stamp);
    }

    [Fact]
    public void TheBuildStampIsReadBesideTheLoadTime()
    {
        // A carried-over graph: built at 06:00, loaded by the rotation at 08:00.
        const string carried = """{"returned":1,"graphs":[{"graphId":"janet","source":"C:\\repos\\JanetHome\\JanetHome.slnx","builtAt":"2026-10-12T06:00:00+00:00","loadedAt":"2026-10-12T08:00:00+00:00"}]}""";
        ScriptedServer server = new((method, _) => method switch
        {
            "initialize" => Initialized,
            "tools/call" => ToolResult(carried),
            _ => null,
        });

        ServedGraph graph = Assert.Single(new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server, NoToken).TryListGraphs()!);

        DateTimeOffset built = new(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        Assert.Equal(built, graph.BuiltAt);
        Assert.Equal(built, graph.Stamp);
    }

    [Fact]
    public void AGraphLoadedAfterAnEditItNeverSawIsStale()
    {
        // 2026-10-12: the rotation reloaded graphs built two hours earlier. Judged by load time,
        // an edit between the build and the load read as already seen.
        ServedGraph carried = new(
            "janet",
            @"C:\nowhere\JanetHome.slnx",
            LoadedAt: new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero),
            BuiltAt: new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero));
        DateTime edited = new(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc);

        GraphState state = CodeGraph.DescribeServed(carried, edited);

        Assert.Equal("stale", state.Status);
        Assert.Equal("2026-10-12T06:00:00.0000000Z", state.BuiltAt);
    }

    [Fact]
    public void AGraphWithNoBuildStampIsJudgedByItsLoadTime()
    {
        ServedGraph unstamped = new("janet", @"C:\nowhere\JanetHome.slnx", new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));

        GraphState state = CodeGraph.DescribeServed(unstamped, new DateTime(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc));

        Assert.Equal("current", state.Status);
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

        Assert.Single(new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server, NoToken).TryListGraphs()!);
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

        new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server, NoToken).TryListGraphs();

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

        DateTimeOffset? rebuilt = new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server, NoToken)
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

        Assert.Null(new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server, NoToken).TryListGraphs());
    }

    [Fact]
    public void AServerThatIsNotThereIsNullNotAThrow()
    {
        // Port 1 on loopback: nothing listens, and the client must say so quietly.
        RazorGraphClient client = new(new Uri("http://127.0.0.1:1/"), readToken: NoToken);

        Assert.Null(client.TryListGraphs());
        Assert.Null(client.Refusal);
    }

    // ---- the bearer token (thread item 82) ----------------------------------------------------

    /// <summary>A fake: the real store is never read by a test.</summary>
    private const string FakeToken = "fake-token-for-razorgraph-client-tests";

    private static string? WithToken(out string? error)
    {
        error = null;
        return FakeToken;
    }

    private static string? NoToken(out string? error)
    {
        error = "no MCP token at HKCU\\Software\\Janet (test)";
        return null;
    }

    private static ScriptedServer Answering() => new((method, _) => method switch
    {
        "initialize" => Initialized,
        "tools/call" => ToolResult(Graphs),
        _ => null,
    });

    [Fact]
    public void EveryRequestCarriesTheTokenJanetReads()
    {
        // 2026-10-21 onward both servers run --auth require, and this client sent nothing: every
        // janet check was refused and its graph read "absent".
        ScriptedServer server = Answering();

        Assert.Single(new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server, WithToken).TryListGraphs()!);

        Assert.Equal(3, server.Authorizations.Count);
        Assert.All(server.Authorizations, sent => Assert.Equal($"Bearer {FakeToken}", sent));
    }

    [Fact]
    public void WithNoTokenNoHeaderIsSentAndAnOpenServerStillAnswers()
    {
        ScriptedServer server = Answering();

        Assert.Single(new RazorGraphClient(new Uri("http://127.0.0.1:1/"), server, NoToken).TryListGraphs()!);
        Assert.All(server.Authorizations, Assert.Null);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    public void ARefusalIsNamedAndNeverCarriesTheToken(HttpStatusCode status, string code)
    {
        ScriptedServer server = new((_, _) => null, refuseWith: status);
        RazorGraphClient client = new(new Uri("http://127.0.0.1:1/"), server, WithToken);

        Assert.Null(client.TryListGraphs());
        Assert.NotNull(client.Refusal);
        Assert.Contains(code, client.Refusal);
        Assert.Contains("was sent and refused", client.Refusal);
        Assert.DoesNotContain(FakeToken, client.Refusal);
    }

    [Fact]
    public void ARefusalWithNoTokenSaysNoneWasSentAndWhy()
    {
        ScriptedServer server = new((_, _) => null, refuseWith: HttpStatusCode.Unauthorized);
        RazorGraphClient client = new(new Uri("http://127.0.0.1:1/"), server, NoToken);

        Assert.Null(client.TryListGraphs());
        Assert.Contains("No Authorization header was sent (no MCP token at HKCU\\Software\\Janet (test))", client.Refusal);
        Assert.Contains("janet mcp-token --ensure", client.Refusal);
    }

    [Fact]
    public void ARefusedGraphReadsUnauthorizedNotAbsent()
    {
        DirectoryInfo repository = Directory.CreateTempSubdirectory("janet-rgc-");
        try
        {
            ScriptedServer server = new((_, _) => null, refuseWith: HttpStatusCode.Unauthorized);
            RazorGraphClient client = new(new Uri("http://127.0.0.1:1/"), server, WithToken);

            GraphState state = CodeGraph.ReadServed(client, repository.FullName, null);

            Assert.Equal("unauthorized", state.Status);
            Assert.Equal(client.Refusal, state.Detail);
            Assert.False(state.CanRefresh);
            Assert.Null(state.GraphId);

            string envelope = DotnetCheckJson.Serialize(new CheckResult(
                @"D:\Repos\Sample\App.slnx", "Debug", true,
                new BuildReport(true, 0.1, [], [], 0, null, null, null, null), null, state));
            Assert.Contains("\"status\":\"unauthorized\"", envelope);
            Assert.DoesNotContain(FakeToken, envelope);
        }
        finally
        {
            repository.Delete(recursive: true);
        }
    }

    [Fact]
    public void AnAnsweringServerStillReadsAbsentWhenItHoldsNoGraphHere()
    {
        // The other side of the line: 'absent' keeps its meaning -- the server answered and holds
        // nothing under this repository -- and carries no detail.
        DirectoryInfo repository = Directory.CreateTempSubdirectory("janet-rgc-");
        try
        {
            RazorGraphClient client = new(new Uri("http://127.0.0.1:1/"), Answering(), WithToken);

            GraphState state = CodeGraph.ReadServed(client, repository.FullName, null);

            Assert.Equal("absent", state.Status);
            Assert.Null(state.Detail);
        }
        finally
        {
            repository.Delete(recursive: true);
        }
    }

    [Fact]
    public void OnlyBuildableSourcesCanBeRebuilt()
    {
        Assert.False(RazorGraphClient.CanRebuild(@"C:\nowhere\saved.graph.json"));
        Assert.False(RazorGraphClient.CanRebuild(@"C:\nowhere\Missing.slnx"));
    }
}
