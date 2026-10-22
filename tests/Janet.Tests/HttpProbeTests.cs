using System.Net;
using System.Net.Sockets;
using System.Text;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// http_probe's behaviour: when it stops, when it asks again, what it reports, and what it refuses.
/// </summary>
/// <remarks>
/// No test here reaches the internet. Most drive the probe through <see cref="StubHttp"/>; the two
/// at the end use a real HttpClient against a loopback socket, because "nothing is added to the
/// request" and "a refused port is an answer" are claims about the real handler, not a stub.
/// </remarks>
public class HttpProbeTests
{
    private const string Url = "https://site.example.invalid/healthz";

    private static HttpProbeResult Probe(StubHttp stub, HttpProbeRequest request) =>
        new HttpProbe(stub, stub.Slept.Add).Probe(request);

    private static HttpProbeResult Probe(StubHttp stub, int retries = 0, int? expectStatus = null, int bodyCap = HttpProbe.DefaultBodyCap) =>
        HttpProbeTests.Probe(stub, new HttpProbeRequest
        {
            Url = HttpProbeTests.Url,
            Retries = retries,
            DelayMs = 250,
            ExpectStatus = expectStatus,
            BodyCap = bodyCap,
        });

    // ---- when it stops ---------------------------------------------------------------------

    [Fact]
    public void A2xxIsOkAtTheFirstAttemptWhenNoStatusWasNamed()
    {
        StubHttp stub = new(StubHttp.Status(204), StubHttp.Status(500));

        HttpProbeResult result = HttpProbeTests.Probe(stub, retries: 3);

        Assert.True(result.Ok);
        Assert.Equal(204, result.Status);
        Assert.Equal("No Content", result.Reason);
        HttpProbeAttempt only = Assert.Single(result.Attempts);
        Assert.Equal("matched", only.Outcome);
        Assert.Single(stub.Requests);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ANamedStatusIsTheOnlyOneThatCountsEvenOverA2xx()
    {
        HttpProbeResult notFoundWanted = HttpProbeTests.Probe(new StubHttp(StubHttp.Status(404)), expectStatus: 404);
        HttpProbeResult okNotWanted = HttpProbeTests.Probe(new StubHttp(StubHttp.Status(200)), expectStatus: 404);

        Assert.True(notFoundWanted.Ok);
        Assert.Equal("matched", notFoundWanted.Attempts[0].Outcome);

        Assert.False(okNotWanted.Ok);
        Assert.Equal(200, okNotWanted.Status);
        Assert.Equal("unexpected", okNotWanted.Attempts[0].Outcome);
    }

    [Fact]
    public void AStatusThatIsNeitherWantedNorWarmingEndsTheProbeAtOnce()
    {
        StubHttp stub = new(StubHttp.Status(500), StubHttp.Status(200));

        HttpProbeResult result = HttpProbeTests.Probe(stub, retries: 5);

        Assert.False(result.Ok);
        Assert.Equal(500, result.Status);
        Assert.Equal("unexpected", Assert.Single(result.Attempts).Outcome);
        Assert.Single(stub.Requests);
        Assert.Empty(stub.Slept);
    }

    // ---- when it asks again ----------------------------------------------------------------

    [Fact]
    public void AWarmingStatusIsAskedAgainDelayApartUntilTheAnswerArrives()
    {
        StubHttp stub = new(StubHttp.Status(503), StubHttp.Status(503), StubHttp.Status(200, "up"));

        HttpProbeResult result = HttpProbeTests.Probe(stub, retries: 5);

        Assert.True(result.Ok);
        Assert.Equal(["warming", "warming", "matched"], result.Attempts.Select(a => a.Outcome));
        Assert.Equal([1, 2, 3], result.Attempts.Select(a => a.Attempt));
        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)], stub.Slept);
        Assert.Equal("up", result.Body);
    }

    [Fact]
    public void TheCallersWarmingListReplacesTheDefault()
    {
        StubHttp stub = new(StubHttp.Status(404), StubHttp.Status(502), StubHttp.Status(503));

        HttpProbeResult result = HttpProbeTests.Probe(stub, new HttpProbeRequest
        {
            Url = HttpProbeTests.Url,
            Retries = 5,
            DelayMs = 0,
            WarmingStatuses = [404, 502],
        });

        // 404 and 502 were warming because the caller said so; 503 was not on their list.
        Assert.Equal(["warming", "warming", "unexpected"], result.Attempts.Select(a => a.Outcome));
        Assert.Equal([404, 502], result.WarmingStatuses);
    }

    [Fact]
    public void NoHttpAnswerAtAllIsWarmingNotBroken()
    {
        // The deploy verify step's lesson (a6adab3): a site mid-restart refuses connections, and
        // treating that as a failure ended the poll on a deploy that had landed.
        StubHttp stub = new(StubHttp.Refused(), StubHttp.Status(200));

        HttpProbeResult result = HttpProbeTests.Probe(stub, retries: 3);

        Assert.True(result.Ok);
        Assert.Equal(["noResponse", "matched"], result.Attempts.Select(a => a.Outcome));
        Assert.Null(result.Attempts[0].Status);
        Assert.Contains("actively refused", result.Attempts[0].Error);
        Assert.Null(result.Error);
    }

    [Fact]
    public void RetriesThatRunOutReportTheLastAnswer()
    {
        StubHttp stub = new(StubHttp.Status(503, "warming up"));

        HttpProbeResult result = HttpProbeTests.Probe(stub, retries: 2);

        Assert.False(result.Ok);
        Assert.Equal(503, result.Status);
        Assert.Equal(3, result.Attempts.Count);
        Assert.All(result.Attempts, a => Assert.Equal("warming", a.Outcome));
        Assert.Equal("warming up", result.Body);
        Assert.Equal(2, stub.Slept.Count);
    }

    // ---- what it reports -------------------------------------------------------------------

    [Fact]
    public void ATransportFailureIsAResultRatherThanAnException()
    {
        StubHttp stub = new(StubHttp.Refused("Name or service not known"));

        HttpProbeResult result = HttpProbeTests.Probe(stub, retries: 1);

        Assert.False(result.Ok);
        Assert.Null(result.Status);
        Assert.Null(result.Reason);
        Assert.Null(result.Body);
        Assert.Equal("Name or service not known", result.Error);
        Assert.Equal(2, result.Attempts.Count);
    }

    [Fact]
    public void TheErrorKeepsWhatAnInnerExceptionAddsAndDropsWhatItRepeats()
    {
        StubHttp tls = new((_, _) => throw new HttpRequestException(
            "The SSL connection could not be established.", new IOException("The remote certificate is invalid.")));
        StubHttp refused = new((_, _) => throw new HttpRequestException(
            "Connection refused. (127.0.0.1:1)", new IOException("Connection refused.")));

        Assert.Equal("The SSL connection could not be established. -- The remote certificate is invalid.", HttpProbeTests.Probe(tls).Error);
        Assert.Equal("Connection refused. (127.0.0.1:1)", HttpProbeTests.Probe(refused).Error);
    }

    [Fact]
    public void AHungServerIsCutOffAtTheTimeoutAndSaysWhere()
    {
        StubHttp stub = new(StubHttp.Hangs());

        HttpProbeResult result = HttpProbeTests.Probe(stub, new HttpProbeRequest { Url = HttpProbeTests.Url, TimeoutMs = 100 });

        Assert.False(result.Ok);
        Assert.Null(result.Status);
        Assert.Equal("timed out after 100 ms waiting for a response", result.Error);
        Assert.True(result.Attempts[0].ElapsedMs < 10000, $"took {result.Attempts[0].ElapsedMs} ms");
    }

    [Fact]
    public void TheBodyIsCutAtTheCapAndSaysSo()
    {
        string body = new('x', 10000);

        HttpProbeResult capped = HttpProbeTests.Probe(new StubHttp(StubHttp.Status(200, body)), bodyCap: 100);
        HttpProbeResult exact = HttpProbeTests.Probe(new StubHttp(StubHttp.Status(200, new string('y', 100))), bodyCap: 100);

        Assert.Equal(new string('x', 100), capped.Body);
        Assert.Equal(100, capped.BodyBytes);
        Assert.True(capped.BodyTruncated);

        // Exactly the cap is not truncated: the probe reads one byte past it to know.
        Assert.Equal(100, exact.BodyBytes);
        Assert.False(exact.BodyTruncated);
    }

    [Fact]
    public void TheDefaultCapIsAFewKilobytes()
    {
        HttpProbeResult result = HttpProbeTests.Probe(new StubHttp(StubHttp.Status(200, new string('z', 20000))));

        Assert.Equal(HttpProbe.DefaultBodyCap, result.BodyBytes);
        Assert.Equal(4096, result.Body!.Length);
        Assert.True(result.BodyTruncated);
    }

    [Fact]
    public void NamedResponseHeadersAreTheOnlyOnesReturned()
    {
        StubHttp stub = new(StubHttp.Status(200, "", "X-Build: 42", "X-Other: no", "Cache-Control: no-store"));

        HttpProbeResult result = HttpProbeTests.Probe(stub, new HttpProbeRequest
        {
            Url = HttpProbeTests.Url,
            ResponseHeaders = ["x-build", "cache-control"],
        });

        Assert.Equal(["X-Build", "Cache-Control"], result.Headers.Select(h => h.Name));
        Assert.Equal("42", result.Headers[0].Value);
        Assert.False(result.HeadersTruncated);
    }

    [Fact]
    public void AllHeadersStopAtTheCapAndSaySo()
    {
        string[] many = Enumerable.Range(1, HttpProbe.MaxHeaders + 10).Select(i => $"X-H{i}: {i}").ToArray();

        HttpProbeResult result = HttpProbeTests.Probe(new StubHttp(StubHttp.Status(200, "", many)));

        Assert.Equal(HttpProbe.MaxHeaders, result.Headers.Count);
        Assert.True(result.HeadersTruncated);
    }

    // ---- what it refuses -------------------------------------------------------------------

    [Theory]
    [InlineData("file:///C:/Windows/win.ini", "only http and https are probed, not 'file:'")]
    [InlineData("ftp://example.invalid/x", "only http and https are probed, not 'ftp:'")]
    [InlineData("/relative/path", "the url is not an absolute URL")]
    [InlineData("https://user:secret@example.invalid/", "credentials in the URL are refused -- pass an Authorization header instead")]
    public void AUrlThatIsNotPlainHttpIsRefusedWithoutSendingAnything(string url, string why)
    {
        StubHttp stub = new(StubHttp.Status(200));

        HttpProbeResult result = HttpProbeTests.Probe(stub, new HttpProbeRequest { Url = url });

        Assert.Equal($"refused: {why}", result.Error);
        Assert.False(result.Ok);
        Assert.Empty(result.Attempts);
        Assert.Empty(stub.Requests);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("delete")]
    [InlineData("PUT")]
    public void AMethodThatCouldChangeSomethingIsRefused(string method)
    {
        StubHttp stub = new(StubHttp.Status(200));

        HttpProbeResult result = HttpProbeTests.Probe(stub, new HttpProbeRequest { Url = HttpProbeTests.Url, Method = method });

        Assert.StartsWith($"refused: method '{method.ToUpperInvariant()}' is refused", result.Error);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void HeadAndOptionsAreSentAsAsked()
    {
        StubHttp stub = new(StubHttp.Status(200));

        HttpProbeTests.Probe(stub, new HttpProbeRequest { Url = HttpProbeTests.Url, Method = "head" });
        HttpProbeTests.Probe(stub, new HttpProbeRequest { Url = HttpProbeTests.Url, Method = "OPTIONS" });

        Assert.Equal([HttpMethod.Head, HttpMethod.Options], stub.Requests.Select(r => r.Method));
    }

    [Theory]
    [InlineData(21, 0, 1, 0, "retries 21 is outside 0..20")]
    [InlineData(0, -1, 1, 0, "delayMs -1 is outside 0..60000")]
    [InlineData(0, 0, 0, 0, "timeoutMs 0 is outside 1..120000")]
    [InlineData(0, 0, 1, 65537, "bodyCap 65537 is outside 0..65536")]
    public void AValueOutOfRangeIsRefusedRatherThanClamped(int retries, int delayMs, int timeoutMs, int bodyCap, string why)
    {
        StubHttp stub = new(StubHttp.Status(200));

        HttpProbeResult result = HttpProbeTests.Probe(stub, new HttpProbeRequest
        {
            Url = HttpProbeTests.Url,
            Retries = retries,
            DelayMs = delayMs,
            TimeoutMs = timeoutMs,
            BodyCap = bodyCap,
        });

        Assert.Equal($"refused: {why}", result.Error);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void TheCallersHeadersAreSentAndNeverEchoed()
    {
        StubHttp stub = new(StubHttp.Status(200));

        HttpProbeResult result = HttpProbeTests.Probe(stub, new HttpProbeRequest
        {
            Url = HttpProbeTests.Url,
            Headers = ["Authorization: Bearer secret-token-xyz", "X-Trace:  abc "],
        });

        StubHttp.Sent sent = Assert.Single(stub.Requests);
        Assert.Equal("Bearer secret-token-xyz", sent.Headers["Authorization"]);
        Assert.Equal("abc", sent.Headers["X-Trace"]);

        string envelope = HttpProbeJson.Serialize(result) + HttpProbeJson.Render(result);
        Assert.DoesNotContain("secret-token-xyz", envelope);
    }

    [Theory]
    [InlineData("Bearer secret-token-xyz", "header 1 is not 'Name: value'")]
    [InlineData("X-Bad Name: secret-token-xyz", "header 1 ('X-Bad Name') cannot be sent on a request")]
    [InlineData("X-Split: secret-token-xyz\r\nInjected: 1", "header 1 ('X-Split') cannot be sent on a request")]
    public void AHeaderThatCannotBeSentIsRefusedByNameNeverByValue(string header, string why)
    {
        StubHttp stub = new(StubHttp.Status(200));

        HttpProbeResult result = HttpProbeTests.Probe(stub, new HttpProbeRequest { Url = HttpProbeTests.Url, Headers = [header] });

        Assert.Equal($"refused: {why}", result.Error);
        Assert.DoesNotContain("secret-token-xyz", HttpProbeJson.Serialize(result));
        Assert.Empty(stub.Requests);
    }

    // ---- the real handler, on loopback -----------------------------------------------------

    /// <summary>A one-shot HTTP/1.1 server on a loopback port: records the request head, answers once.</summary>
    private sealed class OneShotServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Task<string> served;

        public OneShotServer(string response)
        {
            this.listener.Start();
            this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
            this.served = Task.Run(() => this.Serve(response));
        }

        public int Port { get; }

        /// <summary>The request line and headers exactly as they arrived on the socket.</summary>
        public string RequestHead => this.served.Wait(TimeSpan.FromSeconds(10)) ? this.served.Result : throw new TimeoutException("no request arrived");

        private string Serve(string response)
        {
            using TcpClient client = this.listener.AcceptTcpClient();
            using NetworkStream stream = client.GetStream();
            StringBuilder head = new();
            byte[] buffer = new byte[4096];

            while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            byte[] answer = Encoding.ASCII.GetBytes(response);
            stream.Write(answer, 0, answer.Length);
            return head.ToString();
        }

        public void Dispose() => this.listener.Stop();
    }

    [Fact]
    public void NothingIsAddedToTheRequestEvenOnLoopback()
    {
        // The MCP server listens on 127.0.0.1 with a bearer token. A probe of loopback must carry
        // only what the caller passed: no Authorization, no cookie, no credentials of janet's.
        using OneShotServer server = new("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nX-Served: yes\r\nConnection: close\r\n\r\nhi");

        HttpProbeResult result = new HttpProbe().Probe(new HttpProbeRequest
        {
            Url = $"http://127.0.0.1:{server.Port}/health",
            TimeoutMs = 10000,
        });

        string head = server.RequestHead;
        Assert.StartsWith("GET /health HTTP/1.1", head);
        Assert.DoesNotContain("authorization:", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie:", head, StringComparison.OrdinalIgnoreCase);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("hi", result.Body);
        Assert.Contains(result.Headers, h => h.Name == "X-Served" && h.Value == "yes");
    }

    [Fact]
    public void ARefusedPortIsAnAnswerWithNoStatus()
    {
        TcpListener reserved = new(IPAddress.Loopback, 0);
        reserved.Start();
        int port = ((IPEndPoint)reserved.LocalEndpoint).Port;
        reserved.Stop();

        HttpProbeResult result = new HttpProbe().Probe(new HttpProbeRequest
        {
            Url = $"http://127.0.0.1:{port}/",
            TimeoutMs = 10000,
        });

        Assert.False(result.Ok);
        Assert.Null(result.Status);
        Assert.Equal("noResponse", Assert.Single(result.Attempts).Outcome);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }
}
