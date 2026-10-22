using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Janet.Core;
using Janet.Mcp;
using Janet.Mcp.Security;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The guard and the bearer check in the real pipeline: <see cref="HttpServer.Build"/> is what
/// Program.cs serves, so these prove the ORDER -- that no endpoint, /az/token included, answers
/// before the guard does. Each test starts its own server on an OS-chosen loopback port. Nothing
/// here reaches Azure, even with the guard broken: the /az/token request names a scope the handler
/// refuses locally, and the loopback requests go to /health and MCP initialize.
/// </summary>
/// <remarks>
/// The guard's pure decisions (Host, Origin, Content-Type cases) moved with the code to
/// Janet.Shared's Janet.Mcp.Security tests in 0.7.0. What stays here is this server's wiring of
/// UseMcpSecurity. The server runs under Require with a test token and these requests carry
/// none, so a refusal they see from the guard (400, 403, 415) proves the guard answered BEFORE
/// the bearer check, whose answer would have been 401.
/// </remarks>
public sealed class LoopbackGuardPipelineTests : IAsyncLifetime
{
    private readonly PipelineServer _server = new(BearerMode.Require, PipelineServer.Token);

    public Task InitializeAsync() => _server.StartAsync();

    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    [Fact]
    public async Task AForgedHostIsRefusedOnAzTokenBeforeTheHandlerRuns()
    {
        // An unknown scope, so that if the guard ever lets this through the handler refuses it
        // locally (its own 400, a different body) instead of fetching a real token. A mutation
        // run with scope=arm did exactly that, and this test must never reach Azure.
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_server.Base, "az/token?scope=not-a-scope"));
        request.Headers.Host = "rebind.evil.example";
        request.Headers.Add("Origin", "http://rebind.evil.example");

        using HttpResponseMessage response = await _server.Client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("Refused: Host 'rebind.evil.example'", body);
    }

    [Fact]
    public async Task ACrossSiteOriginIsRefusedOnHealth()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_server.Base, "health"));
        request.Headers.Add("Origin", "http://rebind.evil.example");

        using HttpResponseMessage response = await _server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("rebind.evil.example", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ALoopbackRequestReachesHealth()
    {
        using HttpResponseMessage response = await _server.Client.GetAsync(new Uri(_server.Base, "health"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Health.Name, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ATextPlainPostIsRefusedBeforeAnyEndpointSeesIt()
    {
        // At /health rather than the MCP endpoint: the MCP SDK already answers a text/plain POST
        // with its own 415, so a test there passed with the guard's check broken (mutation run
        // 2026-10-21). /health is GET-only, so without the guard this is a 405, not a 415.
        using HttpResponseMessage response = await _server.Client.PostAsync(
            new Uri(_server.Base, "health"), new StringContent(PipelineServer.InitializeBody, Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.StartsWith("Refused: a POST here must be Content-Type application/json", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ALoopbackJsonInitializeStillReachesMcp()
    {
        using HttpResponseMessage response = await _server.InitializeAsync(PipelineServer.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// The bearer check's three modes through <see cref="HttpServer.Build"/>, with a token provider
/// the test owns -- the real HKCU\Software\Janet store is never read or written here.
/// </summary>
public sealed class BearerPipelineTests
{
    [Fact]
    public async Task RequireRefusesARequestWithNoTokenWith401NamingTheFix()
    {
        await using PipelineServer server = new(BearerMode.Require, PipelineServer.Token);
        await server.StartAsync();

        using HttpResponseMessage response = await server.InitializeAsync(token: null);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("janet mcp-headers", body);
        Assert.DoesNotContain(PipelineServer.Token, body);
    }

    [Fact]
    public async Task RequireRefusesAWrongTokenAndAdmitsTheRightOne()
    {
        await using PipelineServer server = new(BearerMode.Require, PipelineServer.Token);
        await server.StartAsync();

        using HttpResponseMessage wrong = await server.InitializeAsync("not-the-token");
        using HttpResponseMessage right = await server.InitializeAsync(PipelineServer.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task RequireStillAnswersHealthWithNoToken()
    {
        await using PipelineServer server = new(BearerMode.Require, PipelineServer.Token);
        await server.StartAsync();

        using HttpResponseMessage response = await server.GetHealthAsync(token: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RequireRefusesAzTokenWithNoTokenBeforeTheHandlerRuns()
    {
        await using PipelineServer server = new(BearerMode.Require, PipelineServer.Token);
        await server.StartAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(server.Base, "az/token?scope=not-a-scope"));
        using HttpResponseMessage response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void RequireWithNoTokenRefusesToStartAndSaysToEnsureOne()
    {
        using BearerAuthenticator bearer = new(BearerMode.Require, () => null);
        GraphContext context = new(PipelineServer.GraphPath());

        StartupRefusedException refused = Assert.Throws<StartupRefusedException>(() => HttpServer.Build(context, 0, bearer));

        Assert.Contains("janet mcp-token --ensure", refused.Message);
    }

    [Fact]
    public void RequireNamesTheProviderErrorWhenTheTokenCannotBeRead()
    {
        using BearerAuthenticator bearer = new(
            BearerMode.Require, () => throw new InvalidOperationException("HKCU\\Software\\Janet.Tests\\x\\McpToken holds an MCP token that cannot be read"));

        StartupRefusedException refused = Assert.Throws<StartupRefusedException>(
            () => HttpServer.Build(new GraphContext(PipelineServer.GraphPath()), 0, bearer));

        Assert.Contains("cannot be read", refused.Message);
    }

    [Theory]
    [InlineData(BearerMode.Report)]
    [InlineData(BearerMode.Off)]
    public async Task ReportAndOffStartWithNoTokenAndLetARequestWithoutOneThrough(BearerMode mode)
    {
        await using PipelineServer server = new(mode, token: null);
        await server.StartAsync();

        using HttpResponseMessage response = await server.InitializeAsync(token: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReportLetsAWrongTokenThroughAndCountsIt()
    {
        await using PipelineServer server = new(BearerMode.Report, PipelineServer.Token);
        await server.StartAsync();

        using HttpResponseMessage wrong = await server.InitializeAsync("not-the-token");
        using HttpResponseMessage health = await server.GetHealthAsync(token: null);
        HealthReport? report = await health.Content.ReadFromJsonAsync<HealthReport>();

        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Equal("Report", report!.Auth!.Mode);
        Assert.Equal(1L, report.Auth.Wrong);
    }

    [Fact]
    public async Task OffReportsItsModeOnHealthAndCountsNothing()
    {
        await using PipelineServer server = new(BearerMode.Off, PipelineServer.Token);
        await server.StartAsync();

        using HttpResponseMessage first = await server.InitializeAsync(token: null);
        using HttpResponseMessage health = await server.GetHealthAsync(token: null);
        HealthReport? report = await health.Content.ReadFromJsonAsync<HealthReport>();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("Off", report!.Auth!.Mode);
        Assert.Equal(0L, report.Auth.Missing);
    }

    [Fact]
    public async Task HealthCarriesTheCountersAndNeverTheToken()
    {
        await using PipelineServer server = new(BearerMode.Require, PipelineServer.Token);
        await server.StartAsync();

        using (await server.InitializeAsync(PipelineServer.Token)) { }
        using (await server.InitializeAsync(token: null)) { }
        using (await server.InitializeAsync("not-the-token")) { }

        // Sent WITH the token, so a /health that echoed request headers would leak it too.
        using HttpResponseMessage health = await server.GetHealthAsync(PipelineServer.Token);
        string body = await health.Content.ReadAsStringAsync();
        HealthReport? report = System.Text.Json.JsonSerializer.Deserialize<HealthReport>(
            body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Require", report!.Auth!.Mode);
        Assert.True(report.Auth.TokenConfigured);
        Assert.Equal(1L, report.Auth.Ok);
        Assert.Equal(1L, report.Auth.Missing);
        Assert.Equal(1L, report.Auth.Wrong);
        Assert.DoesNotContain(PipelineServer.Token, body);
    }
}

/// <summary>One janet-mcp HTTP application on an OS-chosen port, over a test-owned token.</summary>
internal sealed class PipelineServer : IAsyncDisposable
{
    /// <summary>A token no real store holds, distinctive enough that finding it in a body means a leak.</summary>
    internal const string Token = "pipeline-test-token-6f1d0c2a9b8e4d7f";

    internal const string InitializeBody =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"PipelineServer","version":"1"}}}""";

    private readonly BearerAuthenticator _bearer;
    private readonly string _graphPath;
    private WebApplication? _app;

    internal PipelineServer(BearerMode mode, string? token, string? graphPath = null)
    {
        _bearer = new BearerAuthenticator(mode, () => token, headerHint: McpAuth.HeaderHint);
        _graphPath = graphPath ?? GraphPath();
    }

    internal HttpClient Client { get; } = new();

    internal Uri Base { get; private set; } = null!;

    internal static string GraphPath() =>
        Path.Combine(Path.GetTempPath(), "janet-guard-" + Guid.NewGuid().ToString("n")[..8] + ".json");

    internal async Task StartAsync()
    {
        _app = HttpServer.Build(new GraphContext(_graphPath), 0, _bearer);
        await _app.StartAsync();
        Base = new Uri(Assert.Single(_app.Urls).TrimEnd('/') + "/");
    }

    internal Task<HttpResponseMessage> InitializeAsync(string? token)
    {
        HttpRequestMessage request = new(HttpMethod.Post, Base)
        {
            Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        Authorize(request, token);
        return Client.SendAsync(request);
    }

    internal Task<HttpResponseMessage> GetHealthAsync(string? token)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(Base, "health"));
        Authorize(request, token);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _bearer.Dispose();
    }

    private static void Authorize(HttpRequestMessage request, string? token)
    {
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}
