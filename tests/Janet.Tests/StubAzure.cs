using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Janet.Core;

namespace Janet.Tests;

/// <summary>
/// Azure, canned: an HttpMessageHandler that answers by method and URL substring, and records
/// every request it was sent.
/// </summary>
/// <remarks>
/// No test of the Azure code touches the network. Each route holds a sequence of answers and
/// repeats the last once it runs out, which is how a long-running operation that is still
/// running on every poll is written. An unrouted request throws rather than answering 404,
/// because a stub that invents an answer is a test that passes for the wrong reason.
/// <para>
/// AzureHttp is synchronous, so Send is overridden as well as SendAsync: the base handler's
/// Send refuses outright, and HttpClient.Send goes straight to it.
/// </para>
/// </remarks>
internal sealed class StubAzure : HttpMessageHandler
{
    /// <summary>A subscription id every test passes explicitly, so nothing reads the real ~/.azure profile.</summary>
    public const string Sub = "0f1e2d3c-4b5a-4968-8778-a1b2c3d4e5f6";

    public const string ObjectId = "9a8b7c6d-5e4f-4a3b-9c2d-1e0f9a8b7c6d";

    public const string Upn = "operator@example.invalid";

    private readonly List<(HttpMethod Method, string Contains, Queue<Func<HttpResponseMessage>> Answers)> _routes = [];

    public List<Recorded> Requests { get; } = [];

    /// <summary>The scope alias each token was asked for, in order.</summary>
    public List<string> TokenScopes { get; } = [];

    public List<TimeSpan> Slept { get; } = [];

    public sealed record Recorded(HttpMethod Method, string Url, string? Body, string? Authorization);

    /// <summary>Answers requests with this method whose URL contains the text, in order; the last answer repeats.</summary>
    public StubAzure On(HttpMethod method, string urlContains, params Func<HttpResponseMessage>[] answers)
    {
        _routes.Add((method, urlContains, new Queue<Func<HttpResponseMessage>>(answers)));
        return this;
    }

    public AzureHttp Http() => new(
        this,
        scope =>
        {
            TokenScopes.Add(scope);
            return Jwt(ObjectId, Upn);
        },
        Slept.Add);

    public IEnumerable<Recorded> Sent(HttpMethod method, string urlContains) =>
        Requests.Where(r => r.Method == method && r.Url.Contains(urlContains, StringComparison.Ordinal));

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.OriginalString;
        string? body = request.Content is null ? null : new StreamReader(request.Content.ReadAsStream(cancellationToken)).ReadToEnd();
        Requests.Add(new Recorded(request.Method, url, body, request.Headers.Authorization?.ToString()));

        foreach ((HttpMethod method, string contains, Queue<Func<HttpResponseMessage>> answers) in _routes)
        {
            if (method == request.Method && url.Contains(contains, StringComparison.Ordinal))
            {
                Func<HttpResponseMessage> answer = answers.Count > 1 ? answers.Dequeue() : answers.Peek();
                return answer();
            }
        }

        throw new InvalidOperationException($"No canned answer for {request.Method} {url}");
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));

    public static Func<HttpResponseMessage> Json(int status, string json, params (string Name, string Value)[] headers) =>
        () => Response(status, json, "application/json", headers);

    public static Func<HttpResponseMessage> Json(int status, JsonNode json, params (string Name, string Value)[] headers) =>
        Json(status, json.ToJsonString(), headers);

    public static Func<HttpResponseMessage> Xml(int status, string xml, params (string Name, string Value)[] headers) =>
        () => Response(status, xml, "application/xml", headers);

    public static Func<HttpResponseMessage> Empty(int status, params (string Name, string Value)[] headers) =>
        () => Response(status, string.Empty, "text/plain", headers);

    public static Func<HttpResponseMessage> Throws(Exception exception) => () => throw exception;

    /// <summary>ARM's error dialect, as a body.</summary>
    public static Func<HttpResponseMessage> ArmError(int status, string code, string message) =>
        Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    /// <summary>
    /// A token-shaped string: base64url header and payload carrying oid and upn, and a
    /// signature nobody checks -- StorageProbe reads the claims, it does not validate them.
    /// </summary>
    public static string Jwt(string oid, string upn)
    {
        static string Encode(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return Encode("""{"alg":"none","typ":"JWT"}""") + "." +
               Encode(new JsonObject { ["oid"] = oid, ["upn"] = upn, ["tid"] = "t" }.ToJsonString()) + ".c2ln";
    }

    private static HttpResponseMessage Response(int status, string body, string mediaType, (string Name, string Value)[] headers)
    {
        HttpResponseMessage response = new((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };

        foreach ((string name, string value) in headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value))
            {
                response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }
}
