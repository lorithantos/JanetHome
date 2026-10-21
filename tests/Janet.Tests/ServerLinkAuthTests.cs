using Janet.Core;
using Janet.Mcp.Security;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// ServerLink against a real janet-mcp pipeline that wants a bearer token. The token comes from a
/// reader the test owns, never HKCU\Software\Janet. The request is /az/token for a scope the
/// handler refuses locally, so a request that gets through answers 400 from the handler and
/// nothing reaches Azure.
/// </summary>
public sealed class ServerLinkAuthTests : IAsyncLifetime
{
    private const string Request = "az/token?scope=not-a-scope&raw=false&refresh=false";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "janet-tests", "link-" + Guid.NewGuid().ToString("n")[..8]);
    private PipelineServer? _server;

    private string GraphPath => Path.Combine(_dir, "research.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    [Fact]
    public async Task A401IsLoudAndIsNotTheSilentLocalFallback()
    {
        await StartAsync(BearerMode.Require);

        GraphException refused = Assert.Throws<GraphException>(
            () => ServerLink.TryFetch(GraphPath, Request, allowStart: false, NoToken));

        Assert.Contains("401", refused.Message);
        Assert.Contains("janet mcp-token --ensure", refused.Message);
        Assert.Contains("does not fall back", refused.Message);
    }

    [Fact]
    public async Task ARefusedTokenIsLoudAndNeverEchoed()
    {
        await StartAsync(BearerMode.Require);
        const string wrong = "wrong-token-3c9e1f";

        GraphException refused = Assert.Throws<GraphException>(
            () => ServerLink.TryFetch(GraphPath, Request, allowStart: false, Reader(wrong)));

        Assert.Contains("sent and refused", refused.Message);
        Assert.DoesNotContain(wrong, refused.Message);
        Assert.DoesNotContain(PipelineServer.Token, refused.Message);
    }

    [Fact]
    public async Task TheTokenIsSentAsABearerHeader()
    {
        await StartAsync(BearerMode.Require);

        LinkOutcome outcome = ServerLink.TryFetch(GraphPath, Request, allowStart: false, Reader(PipelineServer.Token));

        // Served: the request got past the bearer check to the handler, whose own refusal of the
        // unknown scope is forwarded as the body.
        Assert.Equal(ServedBy.Server, outcome.ServedBy);
        Assert.Contains("error", outcome.Body);
    }

    [Fact]
    public async Task UnderReportNoTokenIsStillServed()
    {
        await StartAsync(BearerMode.Report);

        LinkOutcome outcome = ServerLink.TryFetch(GraphPath, Request, allowStart: false, NoToken);

        Assert.Equal(ServedBy.Server, outcome.ServedBy);
    }

    private static string? NoToken(out string? error)
    {
        error = "no MCP token at HKCU\\Software\\Janet.Tests\\none\\McpToken";
        return null;
    }

    private static TokenReader Reader(string token) => (out string? error) =>
    {
        error = null;
        return token;
    };

    private async Task StartAsync(BearerMode mode)
    {
        _server = new PipelineServer(mode, PipelineServer.Token, GraphPath);
        await _server.StartAsync();

        // The port ServerLink dials comes from .mcp.json, as it does for the real CLI.
        File.WriteAllText(Path.Combine(_dir, ".mcp.json"),
            $$"""{ "mcpServers": { "janet": { "type": "http", "url": "http://127.0.0.1:{{_server.Base.Port}}/" } } }""");
    }
}
