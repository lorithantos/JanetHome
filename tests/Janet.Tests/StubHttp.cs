using System.Net;
using System.Text;

namespace Janet.Tests;

/// <summary>
/// An endpoint, canned: an HttpMessageHandler that answers each request with the next answer in
/// its list, repeating the last, and records what it was sent.
/// </summary>
/// <remarks>
/// For HttpProbe, which must never reach the network in a test. Unlike StubAzure it does not route
/// by URL: a probe sends the same request every attempt, so the sequence IS the scenario. Send is
/// overridden as well as SendAsync because HttpClient.Send goes straight to it.
/// </remarks>
internal sealed class StubHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>> answers;

    public StubHttp(params Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>[] answers)
    {
        this.answers = new Queue<Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>>(answers);
    }

    public List<Sent> Requests { get; } = [];

    public List<TimeSpan> Slept { get; } = [];

    /// <summary>One request as it left the client: every header, as name and joined value.</summary>
    public sealed record Sent(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers);

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        this.Requests.Add(new Sent(
            request.Method,
            request.RequestUri!,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase)));

        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> answer =
            this.answers.Count > 1 ? this.answers.Dequeue() : this.answers.Peek();
        return answer(request, cancellationToken);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(this.Send(request, cancellationToken));

    public static Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> Status(int status, string body = "", params string[] headers) =>
        (_, _) =>
        {
            HttpResponseMessage response = new((HttpStatusCode)status)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
            };

            foreach (string header in headers)
            {
                int colon = header.IndexOf(':');
                response.Headers.TryAddWithoutValidation(header[..colon], header[(colon + 1)..].Trim());
            }

            return response;
        };

    /// <summary>No HTTP answer at all: what a refused or reset connection looks like to the client.</summary>
    public static Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> Refused(string message = "No connection could be made because the target machine actively refused it.") =>
        (_, _) => throw new HttpRequestException(message);

    /// <summary>Never answers: waits for the caller's deadline, as a hung server would.</summary>
    public static Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> Hangs() =>
        (_, token) =>
        {
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("the deadline never fired");
        };
}
