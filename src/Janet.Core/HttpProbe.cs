using System.Diagnostics;
using System.Net;
using System.Text;

namespace Janet.Core;

/// <summary>What to probe, and how patiently.</summary>
public sealed class HttpProbeRequest
{
    public required string Url { get; init; }

    /// <summary>GET, HEAD or OPTIONS. Anything that could change the target is refused.</summary>
    public string Method { get; init; } = "GET";

    /// <summary>The status that counts as success. Null means any 2xx.</summary>
    public int? ExpectStatus { get; init; }

    /// <summary>Further attempts after the first, made only while the answer is still warming.</summary>
    public int Retries { get; init; }

    public int DelayMs { get; init; } = HttpProbe.DefaultDelayMs;

    /// <summary>Statuses that mean "not up yet, ask again". No HTTP answer at all always counts as warming.</summary>
    public IReadOnlyList<int> WarmingStatuses { get; init; } = HttpProbe.DefaultWarmingStatuses;

    /// <summary>Per attempt: from sending the request to the last body byte read.</summary>
    public int TimeoutMs { get; init; } = HttpProbe.DefaultTimeoutMs;

    /// <summary>Request headers, each "Name: value". Sent, never echoed and never logged.</summary>
    public IReadOnlyList<string> Headers { get; init; } = [];

    /// <summary>Response headers to return, by name. Empty means all of them, up to <see cref="HttpProbe.MaxHeaders"/>.</summary>
    public IReadOnlyList<string> ResponseHeaders { get; init; } = [];

    /// <summary>Bytes of the final response body to return.</summary>
    public int BodyCap { get; init; } = HttpProbe.DefaultBodyCap;
}

/// <summary>One response header as received; a repeated header's values joined with ", ".</summary>
public sealed record HttpProbeHeader(string Name, string Value);

/// <summary>One request and what came back.</summary>
/// <param name="Outcome">matched, warming, unexpected or noResponse -- see <see cref="HttpProbe.Outcomes"/>.</param>
/// <param name="Error">Why there was no answer (or no whole body), or null.</param>
public sealed record HttpProbeAttempt(int Attempt, int? Status, long ElapsedMs, string Outcome, string? Error);

public sealed record HttpProbeResult
{
    /// <summary>Format version. See contracts\http-probe.schema.json.</summary>
    public const int ContractVersion = 1;

    public int Contract => ContractVersion;

    public required string Url { get; init; }

    public required string Method { get; init; }

    public required int? ExpectStatus { get; init; }

    public required IReadOnlyList<int> WarmingStatuses { get; init; }

    public required bool Ok { get; init; }

    public required int? Status { get; init; }

    public required string? Reason { get; init; }

    public required long ElapsedMs { get; init; }

    public required IReadOnlyList<HttpProbeAttempt> Attempts { get; init; }

    public required IReadOnlyList<HttpProbeHeader> Headers { get; init; }

    public required bool HeadersTruncated { get; init; }

    public required string? Body { get; init; }

    public required int BodyBytes { get; init; }

    public required bool BodyTruncated { get; init; }

    public required string? Error { get; init; }
}

/// <summary>
/// Asks a live endpoint one question from this machine -- what status, which headers, what body --
/// and reports the answer as fields, retrying only while the endpoint is still warming up.
/// </summary>
/// <remarks>
/// WHY NOT WEBFETCH. Claude Code's WebFetch converts the page and answers through a small model,
/// reports no status or headers, and caches each URL for fifteen minutes; on 2026-10-09 a deploy
/// verification read a stale timestamp through it. This sends one real request per attempt, with
/// no cache, no cookie jar and no redirects followed, so a 302 is reported as a 302.
///
/// RETRIES MATCH THE DEPLOY VERIFY STEP (Invoke-BuildDeploy.ps1, a6adab3). No HTTP answer at all --
/// refused, reset, timed out -- is warming, not broken: a site mid-restart looks exactly like
/// that. A status in the warming list is warming too. A status that is neither the expected one
/// nor warming is a definitive answer and ends the probe; asking again would only repeat it. The
/// script's defaults are kept where they mean the same thing: warming [503], a 5 s delay, a 30 s
/// request timeout. Its 12 attempts are not: a probe asked once should answer once, so retries
/// default to 0.
///
/// NEVER THROWS. A transport failure is a result -- status null, error set -- and so is a request
/// that cannot be attempted (a file: URL, a POST, a malformed header), with no attempts. A caller
/// polling this never has to tell an exception from an answer.
///
/// NOTHING IS ADDED TO THE REQUEST. The only headers sent are the caller's: no Authorization,
/// no credentials, no cookies. In particular janet's own MCP bearer token is never read here and
/// never attached, even when the URL is janet's own port. The caller's headers may carry auth, so
/// they are never echoed into the envelope, and an error about one names it by position and name,
/// never by value.
///
/// READ-ONLY BY CONSTRUCTION: GET, HEAD and OPTIONS only, so the tool can be allow-listed.
/// </remarks>
public sealed class HttpProbe
{
    public const int DefaultDelayMs = 5000;
    public const int DefaultTimeoutMs = 30000;
    public const int DefaultBodyCap = 4096;

    public const int MaxRetries = 20;
    public const int MaxDelayMs = 60000;
    public const int MaxTimeoutMs = 120000;
    public const int MaxBodyCap = 65536;

    /// <summary>The most response headers returned when none were named.</summary>
    public const int MaxHeaders = 50;

    public static readonly IReadOnlyList<int> DefaultWarmingStatuses = [503];

    /// <summary>Every per-attempt outcome, in the order a reader meets them.</summary>
    public static readonly IReadOnlyList<string> Outcomes = ["matched", "warming", "unexpected", "noResponse"];

    private static readonly string[] Methods = ["GET", "HEAD", "OPTIONS"];

    private static readonly Lazy<HttpProbe> SharedInstance = new(() => new HttpProbe());

    /// <summary>The process-wide instance the front ends use. One HttpClient for the life of the process.</summary>
    public static HttpProbe Shared => SharedInstance.Value;

    private readonly HttpClient client;
    private readonly Action<TimeSpan> sleep;

    public HttpProbe(HttpMessageHandler? handler = null, Action<TimeSpan>? sleep = null)
    {
        this.client = new HttpClient(handler ?? HttpProbe.NewHandler())
        {
            // Each attempt carries its own deadline (TimeoutMs), which also covers reading the
            // body; a client-wide one would be a second, different answer to the same question.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        this.sleep = sleep ?? Thread.Sleep;
    }

    private static SocketsHttpHandler NewHandler() => new()
    {
        // A probe reports what the URL answered. Following a redirect would report what some
        // other URL answered, under this one's name.
        AllowAutoRedirect = false,

        // No cookie jar: one shared client would otherwise carry one probe's session into the next.
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public HttpProbeResult Probe(HttpProbeRequest request)
    {
        string method = (request.Method ?? "GET").Trim().ToUpperInvariant();
        IReadOnlyList<int> warming = request.WarmingStatuses ?? HttpProbe.DefaultWarmingStatuses;

        string? refusal = HttpProbe.Refusal(request, method, warming, out Uri? uri, out List<KeyValuePair<string, string>> headers);
        if (refusal is not null)
        {
            return HttpProbe.Refused(request, method, warming, refusal);
        }

        Stopwatch total = Stopwatch.StartNew();
        List<HttpProbeAttempt> attempts = [];
        Exchange last = Exchange.None;

        for (int attempt = 1; attempt <= request.Retries + 1; attempt++)
        {
            if (attempt > 1)
            {
                this.sleep(TimeSpan.FromMilliseconds(request.DelayMs));
            }

            last = this.Send(new HttpMethod(method), uri!, headers, request);
            string outcome = HttpProbe.Classify(last.Status, request.ExpectStatus, warming);
            attempts.Add(new HttpProbeAttempt(attempt, last.Status, last.ElapsedMs, outcome, last.Error));

            if (outcome is "matched" or "unexpected")
            {
                break;
            }
        }

        return new HttpProbeResult
        {
            Url = request.Url,
            Method = method,
            ExpectStatus = request.ExpectStatus,
            WarmingStatuses = warming,
            Ok = last.Status is int status && HttpProbe.Matches(status, request.ExpectStatus) && last.Error is null,
            Status = last.Status,
            Reason = last.Reason,
            ElapsedMs = total.ElapsedMilliseconds,
            Attempts = attempts,
            Headers = last.Headers,
            HeadersTruncated = last.HeadersTruncated,
            Body = last.Body,
            BodyBytes = last.BodyBytes,
            BodyTruncated = last.BodyTruncated,
            Error = last.Error,
        };
    }

    /// <summary>The expected status, or any 2xx when none was named.</summary>
    public static bool Matches(int status, int? expect) =>
        expect is int wanted ? status == wanted : status is >= 200 and < 300;

    /// <summary>What one attempt's answer means for the probe: stop, or ask again.</summary>
    public static string Classify(int? status, int? expect, IReadOnlyList<int> warming)
    {
        if (status is not int code)
        {
            return "noResponse";
        }

        if (HttpProbe.Matches(code, expect))
        {
            return "matched";
        }

        return warming.Contains(code) ? "warming" : "unexpected";
    }

    /// <summary>One request, read to the body cap. Every failure becomes an Exchange with an error.</summary>
    private Exchange Send(HttpMethod method, Uri uri, List<KeyValuePair<string, string>> headers, HttpProbeRequest request)
    {
        Stopwatch watch = Stopwatch.StartNew();
        using CancellationTokenSource deadline = new(TimeSpan.FromMilliseconds(request.TimeoutMs));
        using HttpRequestMessage message = new(method, uri);

        foreach (KeyValuePair<string, string> header in headers)
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        int? status = null;
        string? reason = null;
        IReadOnlyList<HttpProbeHeader> collected = [];
        bool headersTruncated = false;
        HttpResponseMessage? response = null;
        BodyRead body = new([], false);

        try
        {
            response = this.client.Send(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            status = (int)response.StatusCode;
            reason = response.ReasonPhrase;
            collected = HttpProbe.Collect(response, request.ResponseHeaders, out headersTruncated);

            // The deadline covers the body too. A synchronous read does not observe a token, so
            // the deadline disposes the response instead, which aborts the read under it.
            using CancellationTokenRegistration abort = deadline.Token.Register(
                static state => ((HttpResponseMessage)state!).Dispose(), response);
            body = HttpProbe.ReadCapped(response.Content.ReadAsStream(deadline.Token), request.BodyCap);
            deadline.Token.ThrowIfCancellationRequested();

            return new Exchange(status, reason, collected, headersTruncated, HttpProbe.Decode(body.Bytes), body.Bytes.Length, body.Truncated, null, watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Deliberately broad: the contract is that a probe never throws, and anything that
            // goes wrong between sending and the last byte -- refused, reset, DNS, TLS, a timeout,
            // a stream disposed under a read -- is an answer about the endpoint, not about janet.
            string error = deadline.IsCancellationRequested
                ? $"timed out after {request.TimeoutMs} ms" + (status is null ? " waiting for a response" : " reading the body")
                : HttpProbe.Describe(ex);

            return new Exchange(
                status,
                reason,
                collected,
                headersTruncated,
                status is null ? null : HttpProbe.Decode(body.Bytes),
                body.Bytes.Length,
                body.Truncated,
                error,
                watch.ElapsedMilliseconds);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static IReadOnlyList<HttpProbeHeader> Collect(HttpResponseMessage response, IReadOnlyList<string> wanted, out bool truncated)
    {
        List<HttpProbeHeader> all = response.Headers
            .Concat(response.Content.Headers)
            .Select(header => new HttpProbeHeader(header.Key, string.Join(", ", header.Value)))
            .ToList();

        if (wanted.Count > 0)
        {
            HashSet<string> names = new(wanted, StringComparer.OrdinalIgnoreCase);
            truncated = false;
            return all.Where(header => names.Contains(header.Name)).ToList();
        }

        truncated = all.Count > HttpProbe.MaxHeaders;
        return all.Take(HttpProbe.MaxHeaders).ToList();
    }

    /// <summary>Reads at most cap bytes, and one more to learn whether there was more.</summary>
    private static BodyRead ReadCapped(Stream stream, int cap)
    {
        byte[] buffer = new byte[cap + 1];
        int filled = 0;

        while (filled < buffer.Length)
        {
            int read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        bool truncated = filled > cap;
        return new BodyRead(buffer[..Math.Min(filled, cap)], truncated);
    }

    /// <summary>UTF-8, with anything undecodable -- a cut mid-character, a binary body -- as U+FFFD.</summary>
    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>The exception and what it wraps, which is where the useful part usually is.</summary>
    private static string Describe(Exception ex)
    {
        List<string> parts = [];
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            // An inner message the outer one already quotes adds only noise: a refused connect
            // arrives as "... refused it. (host:port)" wrapping the same sentence without the port.
            if (!parts.Any(part => part.Contains(current.Message, StringComparison.Ordinal)))
            {
                parts.Add(current.Message);
            }
        }

        return string.Join(" -- ", parts);
    }

    /// <summary>Why the request cannot be attempted, or null. Never quotes a header's value.</summary>
    private static string? Refusal(
        HttpProbeRequest request,
        string method,
        IReadOnlyList<int> warming,
        out Uri? uri,
        out List<KeyValuePair<string, string>> headers)
    {
        headers = [];

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out uri))
        {
            return "the url is not an absolute URL";
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return $"only http and https are probed, not '{uri.Scheme}:'";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "credentials in the URL are refused -- pass an Authorization header instead";
        }

        if (!HttpProbe.Methods.Contains(method))
        {
            return $"method '{method}' is refused: the probe is read-only, so only {string.Join(", ", HttpProbe.Methods)}";
        }

        string? range =
            HttpProbe.OutOfRange("retries", request.Retries, 0, HttpProbe.MaxRetries)
            ?? HttpProbe.OutOfRange("delayMs", request.DelayMs, 0, HttpProbe.MaxDelayMs)
            ?? HttpProbe.OutOfRange("timeoutMs", request.TimeoutMs, 1, HttpProbe.MaxTimeoutMs)
            ?? HttpProbe.OutOfRange("bodyCap", request.BodyCap, 0, HttpProbe.MaxBodyCap)
            ?? (request.ExpectStatus is int expect ? HttpProbe.OutOfRange("expectStatus", expect, 100, 599) : null)
            ?? warming.Select(code => HttpProbe.OutOfRange("warmingStatuses", code, 100, 599)).FirstOrDefault(found => found is not null);
        if (range is not null)
        {
            return range;
        }

        // Each header is tried on a throwaway request, so one that HttpClient would refuse --
        // a content header on a GET, a name with a space -- is refused here, before any attempt.
        using HttpRequestMessage trial = new(HttpMethod.Get, uri);
        IReadOnlyList<string> given = request.Headers ?? [];
        for (int i = 0; i < given.Count; i++)
        {
            string line = given[i] ?? string.Empty;
            int colon = line.IndexOf(':');
            string name = colon > 0 ? line[..colon].Trim() : string.Empty;

            if (name.Length == 0)
            {
                return $"header {i + 1} is not 'Name: value'";
            }

            string value = line[(colon + 1)..].Trim();
            if (!trial.Headers.TryAddWithoutValidation(name, value) || value.Contains('\r') || value.Contains('\n'))
            {
                return $"header {i + 1} ('{name}') cannot be sent on a request";
            }

            headers.Add(new KeyValuePair<string, string>(name, value));
        }

        return null;
    }

    private static string? OutOfRange(string name, int value, int min, int max) =>
        value < min || value > max ? $"{name} {value} is outside {min}..{max}" : null;

    private static HttpProbeResult Refused(HttpProbeRequest request, string method, IReadOnlyList<int> warming, string why) => new()
    {
        Url = request.Url ?? string.Empty,
        Method = method,
        ExpectStatus = request.ExpectStatus,
        WarmingStatuses = warming,
        Ok = false,
        Status = null,
        Reason = null,
        ElapsedMs = 0,
        Attempts = [],
        Headers = [],
        HeadersTruncated = false,
        Body = null,
        BodyBytes = 0,
        BodyTruncated = false,
        Error = $"refused: {why}",
    };

    private sealed record BodyRead(byte[] Bytes, bool Truncated);

    private sealed record Exchange(
        int? Status,
        string? Reason,
        IReadOnlyList<HttpProbeHeader> Headers,
        bool HeadersTruncated,
        string? Body,
        int BodyBytes,
        bool BodyTruncated,
        string? Error,
        long ElapsedMs)
    {
        public static readonly Exchange None = new(null, null, [], false, null, 0, false, null, 0);
    }
}
