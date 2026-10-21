using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Janet.Core;
using Janet.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The HTTP transport answers only loopback clients talking to loopback names. MEASURED
/// 2026-10-21 before the guard: a forged Host and Origin got 200 from MCP initialize and a bearer
/// token from /az/token. These hold each of the three decisions to its cases.
/// </summary>
public class LoopbackGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:7717")]
    [InlineData("localhost")]
    [InlineData("localhost:7717")]
    [InlineData("LOCALHOST:7717")]
    [InlineData("LocalHost")]
    [InlineData("[::1]")]
    [InlineData("[::1]:7717")]
    public void LoopbackHostsAreAccepted(string host) =>
        Assert.True(LoopbackGuard.IsLoopbackHost(host));

    [Theory]
    [InlineData("rebind.evil.example")]
    [InlineData("localhost.evil.example")]
    [InlineData("localhost.evil.example:7717")]
    [InlineData("127.0.0.1.nip.io")]
    [InlineData("127.0.0.1.nip.io:7717")]
    [InlineData("127.0.0.2")]
    [InlineData("0.0.0.0")]
    [InlineData("[::2]:7717")]
    [InlineData("::1")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("localhost:")]
    [InlineData("localhost:77a")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsNotALoopbackHost(string? host) =>
        Assert.False(LoopbackGuard.IsLoopbackHost(host));

    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost:7717")]
    [InlineData("HTTP://LOCALHOST")]
    [InlineData("http://127.0.0.1:7717")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://localhost:443")]
    [InlineData("http://[::1]:7717")]
    public void NoOriginOrALoopbackOriginIsAllowed(string? origin) =>
        Assert.True(LoopbackGuard.IsAllowedOrigin(origin));

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("http://rebind.evil.example")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://127.0.0.1.nip.io")]
    [InlineData("http://127.0.0.2")]
    [InlineData("http://localhost@evil.example")]
    [InlineData("http://evil.example@localhost")]
    [InlineData("http://localhost/path")]
    [InlineData("ws://localhost:7717")]
    [InlineData("file://")]
    [InlineData("localhost")]
    public void ACrossSiteOrMalformedOriginIsRefused(string origin) =>
        Assert.False(LoopbackGuard.IsAllowedOrigin(origin));

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON;charset=UTF-8")]
    public void JsonContentTypesAreAccepted(string contentType) =>
        Assert.True(LoopbackGuard.IsJsonContentType(contentType));

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/jsonp")]
    [InlineData("text/plain; x=application/json")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherContentTypesAreNotJson(string? contentType) =>
        Assert.False(LoopbackGuard.IsJsonContentType(contentType));

    [Fact]
    public void AForgedHostIsRefusedWith400NamingIt()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check("GET", "rebind.evil.example", StringValues.Empty, null);

        Assert.NotNull(refusal);
        Assert.Equal(400, refusal.Value.Status);
        Assert.Contains("rebind.evil.example", refusal.Value.Message);
    }

    [Fact]
    public void AnEmptyHostIsRefused()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check("GET", "", StringValues.Empty, null);

        Assert.Equal(400, refusal?.Status);
    }

    [Fact]
    public void ANullOriginIsRefusedWith403NamingIt()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check("GET", "127.0.0.1:7717", new StringValues("null"), null);

        Assert.NotNull(refusal);
        Assert.Equal(403, refusal.Value.Status);
        Assert.Contains("'null'", refusal.Value.Message);
    }

    [Fact]
    public void TwoOriginHeadersAreRefusedEvenIfBothAreLoopback()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check(
            "GET", "localhost", new StringValues(["http://localhost", "http://127.0.0.1"]), null);

        Assert.Equal(403, refusal?.Status);
    }

    [Fact]
    public void ALoopbackGetWithNoOriginIsLetThrough() =>
        Assert.Null(LoopbackGuard.Check("GET", "127.0.0.1:7717", StringValues.Empty, null));

    [Fact]
    public void AnHttpsLoopbackOriginIsLetThrough() =>
        Assert.Null(LoopbackGuard.Check("GET", "localhost:7717", new StringValues("https://localhost:7717"), null));

    [Fact]
    public void APostWithNoContentTypeIsRefusedWith415()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check("POST", "127.0.0.1:7717", StringValues.Empty, null);

        Assert.Equal(415, refusal?.Status);
    }

    [Fact]
    public void ATextPlainPostIsRefusedWith415NamingIt()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check("post", "127.0.0.1:7717", StringValues.Empty, "text/plain");

        Assert.NotNull(refusal);
        Assert.Equal(415, refusal.Value.Status);
        Assert.Contains("text/plain", refusal.Value.Message);
    }

    [Fact]
    public void AJsonPostWithACharsetIsLetThrough() =>
        Assert.Null(LoopbackGuard.Check("POST", "[::1]:7717", StringValues.Empty, "application/json; charset=utf-8"));

    [Fact]
    public void TheHostIsJudgedBeforeTheOrigin()
    {
        LoopbackGuard.Refusal? refusal = LoopbackGuard.Check(
            "POST", "rebind.evil.example", new StringValues("http://rebind.evil.example"), "text/plain");

        Assert.Equal(400, refusal?.Status);
    }
}

/// <summary>
/// The guard in the real pipeline: <see cref="HttpServer.Build"/> is what Program.cs serves, so
/// these prove the ORDER -- that no endpoint, /az/token included, answers before the guard does.
/// Each test starts its own server on an OS-chosen loopback port. Nothing here reaches Azure, even
/// with the guard broken: the /az/token request names a scope the handler refuses locally, and the
/// loopback requests go to /health and MCP initialize.
/// </summary>
public sealed class LoopbackGuardPipelineTests : IAsyncLifetime
{
    private WebApplication? _app;
    private readonly HttpClient _client = new();
    private Uri _base = null!;

    public async Task InitializeAsync()
    {
        string graph = Path.Combine(Path.GetTempPath(), "janet-guard-" + Guid.NewGuid().ToString("n")[..8] + ".json");
        _app = HttpServer.Build(new GraphContext(graph), 0);
        await _app.StartAsync();
        _base = new Uri(Assert.Single(_app.Urls).TrimEnd('/') + "/");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task AForgedHostIsRefusedOnAzTokenBeforeTheHandlerRuns()
    {
        // An unknown scope, so that if the guard ever lets this through the handler refuses it
        // locally (its own 400, a different body) instead of fetching a real token. A mutation
        // run with scope=arm did exactly that, and this test must never reach Azure.
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_base, "az/token?scope=not-a-scope"));
        request.Headers.Host = "rebind.evil.example";
        request.Headers.Add("Origin", "http://rebind.evil.example");

        using HttpResponseMessage response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("Refused: Host 'rebind.evil.example'", body);
    }

    [Fact]
    public async Task ACrossSiteOriginIsRefusedOnHealth()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_base, "health"));
        request.Headers.Add("Origin", "http://rebind.evil.example");

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("rebind.evil.example", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ALoopbackRequestReachesHealth()
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri(_base, "health"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Health.Name, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ATextPlainPostIsRefusedBeforeAnyEndpointSeesIt()
    {
        // At /health rather than the MCP endpoint: the MCP SDK already answers a text/plain POST
        // with its own 415, so a test there passed with the guard's check broken (mutation run
        // 2026-10-21). /health is GET-only, so without the guard this is a 405, not a 415.
        using HttpResponseMessage response = await _client.PostAsync(
            new Uri(_base, "health"), new StringContent(InitializeBody, Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.StartsWith("Refused: a POST here must be Content-Type application/json", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ALoopbackJsonInitializeStillReachesMcp()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, _base)
        {
            Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private const string InitializeBody =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"LoopbackGuardPipelineTests","version":"1"}}}""";
}
