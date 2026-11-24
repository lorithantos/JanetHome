using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Janet.Core;
using Janet.Mcp;
using Janet.Mcp.Security;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// janet-mcp binds FIRST and finishes starting after: between the two, /health answers 503
/// "starting" and everything else 503 with Retry-After, never 401. Each test holds the startup
/// work -- the token read -- on a gate the test owns, so the window is as long as the test needs.
/// Every server here is on an OS-chosen loopback port, and every token is a test token.
/// </summary>
public sealed class StartupGateTests : IAsyncLifetime
{
    private readonly ManualResetEventSlim _hold = new(initialState: false);
    private PipelineServer _server = null!;
    private Func<string?> _token = () => PipelineServer.Token;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Released before disposal: a held startup would otherwise block a pool thread forever.
        _hold.Set();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        _hold.Dispose();
    }

    [Fact]
    public async Task WhileStartingHealthAnswers503StartingWithTheNormalBody()
    {
        await BindHeldAsync(BearerMode.Require);

        using HttpResponseMessage response = await _server.GetHealthAsync(token: null);
        string body = await response.Content.ReadAsStringAsync();
        JsonElement json = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("janet-mcp", json.GetProperty("server").GetString());
        Assert.Equal(_server.GraphFile, json.GetProperty("graph").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("version").GetString()));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("auth").ValueKind);
        Assert.Equal("starting", json.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("POST", "")]
    [InlineData("GET", "az/token?scope=not-a-scope")]
    [InlineData("GET", "no-such-route")]
    public async Task WhileStartingEveryOtherRouteAnswers503WithRetryAfterAndNot401(string method, string path)
    {
        await BindHeldAsync(BearerMode.Require);

        // No token: under Require a ready server would answer 401. Starting answers first.
        using HttpResponseMessage response = method == "POST"
            ? await _server.InitializeAsync(token: null)
            : await _server.Client.GetAsync(new Uri(_server.Base, path));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Contains("starting", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WhileStartingTheLoopbackGuardStillAnswersFirst()
    {
        await BindHeldAsync(BearerMode.Require);

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_server.Base, "health"));
        request.Headers.Add("Origin", "http://rebind.evil.example");
        using HttpResponseMessage response = await _server.Client.SendAsync(request);

        // The guard's 403, not the starting body: a rebound page learns nothing during startup.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("\"state\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheStartupProbeCountsAStartingJanetAsServing()
    {
        await BindHeldAsync(BearerMode.Require);

        (Health.PortState mine, _) = await Health.ProbeAsync(_server.Base.Port, _server.GraphFile);
        (Health.PortState other, string? otherGraph) = await Health.ProbeAsync(_server.Base.Port, PipelineServer.GraphPath());
        (_, _, bool starting) = await Health.ProbeReadinessAsync(_server.Base.Port, _server.GraphFile);

        Assert.Equal(Health.PortState.AlreadyServing, mine);
        Assert.Equal(Health.PortState.ServingAnotherGraph, other);
        Assert.Equal(_server.GraphFile, otherGraph);
        Assert.True(starting);
    }

    [Fact]
    public async Task HttpProbeReportsAStartingJanetAsWarmingNotOk()
    {
        await BindHeldAsync(BearerMode.Require);

        HttpProbeResult result = new HttpProbe().Probe(new HttpProbeRequest { Url = new Uri(_server.Base, "health").ToString() });

        Assert.False(result.Ok);
        Assert.Equal("warming", Assert.Single(result.Attempts).Outcome);
    }

    [Fact]
    public async Task AfterReleaseHealthIs200ReadyAndAnAuthorisedRequestPasses()
    {
        await BindHeldAsync(BearerMode.Require);
        Task<BearerAuthenticator> finishing = _server.FinishStartingAsync();
        Assert.False(_server.Gate.IsReady);

        _hold.Set();
        await finishing;

        using HttpResponseMessage health = await _server.GetHealthAsync(token: null);
        HealthReport? report = await health.Content.ReadFromJsonAsync<HealthReport>();
        using HttpResponseMessage authorised = await _server.InitializeAsync(PipelineServer.Token);
        using HttpResponseMessage anonymous = await _server.InitializeAsync(token: null);
        (_, _, bool starting) = await Health.ProbeReadinessAsync(_server.Base.Port, _server.GraphFile);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ready", report!.State);
        Assert.Equal("Require", report.Auth!.Mode);
        Assert.True(report.Auth.TokenConfigured);
        Assert.Equal(HttpStatusCode.OK, authorised.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.False(starting);
    }

    [Fact]
    public async Task AFailingTokenReadStillReachesReadyAndIsReportedOnHealth()
    {
        // Report mode started anyway over an unreadable token before the bind moved; it still does.
        _token = () => throw new InvalidOperationException("HKCU\\Software\\Janet.Tests\\x\\McpToken cannot be read");
        await BindHeldAsync(BearerMode.Report);
        Task<BearerAuthenticator> finishing = _server.FinishStartingAsync();

        _hold.Set();
        await finishing;

        using HttpResponseMessage health = await _server.GetHealthAsync(token: null);
        HealthReport? report = await health.Content.ReadFromJsonAsync<HealthReport>();
        using HttpResponseMessage request = await _server.InitializeAsync(token: null);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ready", report!.State);
        Assert.False(report.Auth!.TokenConfigured);
        Assert.Contains("cannot be read", report.Auth.ProviderError);
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);
    }

    [Fact]
    public async Task RequireWithNoTokenIsStillRefusedAfterTheBindAndNeverOpens()
    {
        _token = () => null;
        await BindHeldAsync(BearerMode.Require);
        Task<BearerAuthenticator> finishing = _server.FinishStartingAsync();

        _hold.Set();
        StartupRefusedException refused = await Assert.ThrowsAsync<StartupRefusedException>(() => finishing);
        using HttpResponseMessage health = await _server.GetHealthAsync(token: null);

        Assert.Contains("janet mcp-token --ensure", refused.Message);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.False(_server.Gate.IsReady);
    }

    [Fact]
    public async Task ServerLinkWaitsForAStartingServerInsteadOfFallingBack()
    {
        string directory = Path.Combine(Path.GetTempPath(), "janet-tests", "starting-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            string graph = Path.Combine(directory, "research.json");
            await BindHeldAsync(BearerMode.Require, graph);
            File.WriteAllText(Path.Combine(directory, ".mcp.json"),
                $$"""{ "mcpServers": { "janet": { "type": "http", "url": "http://127.0.0.1:{{_server.Base.Port}}/" } } }""");

            // Finished a moment after the CLI has found the server starting.
            Task finishing = Task.Run(async () =>
            {
                await Task.Delay(400);
                _hold.Set();
                await _server.FinishStartingAsync();
            });

            LinkOutcome outcome = ServerLink.TryFetch(
                graph, "az/token?scope=not-a-scope&raw=false&refresh=false", allowStart: false, Reader);
            await finishing;

            // Served: past the bearer check to the handler, whose refusal of the scope is the body.
            Assert.Equal(ServedBy.Server, outcome.ServedBy);
            Assert.Contains("error", outcome.Body);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
        }
    }

    private static string? Reader(out string? error)
    {
        error = null;
        return PipelineServer.Token;
    }

    /// <summary>Binds a server whose token read waits on <see cref="_hold"/>.</summary>
    private async Task BindHeldAsync(BearerMode mode, string? graphPath = null)
    {
        Func<string?> token = _token;
        _server = new PipelineServer(mode, () =>
        {
            _hold.Wait();
            return token();
        }, graphPath);
        await _server.BindAsync();
    }
}
