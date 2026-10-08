using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Janet.Core;

/// <summary>
/// An error as Azure reported it: ARM's { error: { code, message, details } }, a storage
/// service's x-ms-error-code, or a Graph error -- normalised to one shape.
/// </summary>
/// <remarks>
/// Carried in envelopes as DATA rather than thrown as text. A deployment that failed validation
/// is an answer to "what happens if I deploy this", and its nested details -- which resource,
/// which property -- are the part a reader needs; flattening them into an exception message is
/// how they used to arrive, as one paragraph to be re-parsed by eye.
/// </remarks>
public sealed record AzureError(string Code, string Message, string? Target, IReadOnlyList<AzureError> Details);

/// <summary>One HTTP exchange with Azure, read fully.</summary>
public sealed record AzureResponse(int Status, string Body, IReadOnlyDictionary<string, string> Headers)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;

    /// <summary>The body as a JSON object, or null when it is empty or not JSON.</summary>
    public JsonObject? Json()
    {
        if (string.IsNullOrWhiteSpace(Body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(Body) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The error this response carries, in whichever of Azure's three dialects it used, or null
    /// for a success.
    /// </summary>
    public AzureError? Error()
    {
        if (IsSuccess)
        {
            return null;
        }

        JsonObject? json = Json();

        // ARM and Graph: { "error": { "code", "message", "target", "details": [...] } }.
        // Tables: { "odata.error": { "code", "message": { "value" } } }.
        if (json?["error"] is JsonObject arm)
        {
            return AzureErrors.Read(arm);
        }

        if (json?["odata.error"] is JsonObject odata)
        {
            return new AzureError(
                odata["code"]?.GetValue<string>() ?? $"HTTP {Status}",
                odata["message"]?["value"]?.GetValue<string>() ?? string.Empty,
                null,
                []);
        }

        // Blob: the code is a header, and the body is XML with <Code> and <Message>.
        string? code = Header("x-ms-error-code");
        string message = string.Empty;

        if (Body.TrimStart().StartsWith('<'))
        {
            try
            {
                XElement root = XDocument.Parse(Body).Root!;
                code ??= root.Element("Code")?.Value;
                message = root.Element("Message")?.Value.Split('\n')[0].Trim() ?? string.Empty;
            }
            catch (System.Xml.XmlException)
            {
                // Not XML after all. The status line is still an answer.
            }
        }

        return new AzureError(code ?? $"HTTP {Status}", message, null, []);
    }
}

internal static class AzureErrors
{
    public static AzureError Read(JsonObject error) => new(
        error["code"]?.GetValue<string>() ?? "(no code)",
        error["message"]?.GetValue<string>() ?? string.Empty,
        error["target"]?.GetValue<string>(),
        [.. (error["details"] as JsonArray ?? []).OfType<JsonObject>().Select(Read)]);

    /// <summary>One line per error and nested detail, for a GraphException a caller has to read.</summary>
    public static string Describe(AzureError error, string indent = "")
    {
        StringBuilder text = new();
        text.Append(indent).Append(error.Code).Append(": ").Append(error.Message);

        foreach (AzureError detail in error.Details)
        {
            text.AppendLine().Append(Describe(detail, indent + "  "));
        }

        return text.ToString();
    }
}

/// <summary>Which subscription an operation runs against, and how that was decided.</summary>
/// <param name="Source">"explicit" when the caller named it, "az default" when it came from the Azure CLI's profile.</param>
public sealed record AzureSubscription(string Id, string? Name, string Source)
{
    /// <summary>
    /// The subscription the caller named, or the Azure CLI's default one.
    /// </summary>
    /// <remarks>
    /// The default is read from the CLI's own profile (azureProfile.json under AZURE_CONFIG_DIR
    /// or ~\.azure) rather than by running `az account show`: that is a Python process and a
    /// second or two for one string, and the token already comes from the same sign-in, so the
    /// two cannot disagree about whose subscription "default" means. A name is accepted as well
    /// as an id, matched against the same profile.
    /// </remarks>
    public static AzureSubscription Resolve(string? requested, string? configDirectory = null)
    {
        string? wanted = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();

        if (wanted is not null && Guid.TryParse(wanted, out _))
        {
            return new AzureSubscription(wanted.ToLowerInvariant(), null, "explicit");
        }

        string directory = configDirectory
            ?? (Environment.GetEnvironmentVariable("AZURE_CONFIG_DIR") is { Length: > 0 } configured
                ? configured
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".azure"));
        string profile = Path.Combine(directory, "azureProfile.json");

        if (!File.Exists(profile))
        {
            throw new GraphException(
                $"No Azure CLI profile at {profile}, so there is no default subscription to use. " +
                "Run 'az login', or pass a subscription id.");
        }

        // File.ReadAllText strips the UTF-8 byte-order mark the CLI writes; JsonNode.Parse would
        // refuse the file with it left on.
        JsonArray subscriptions = JsonNode.Parse(File.ReadAllText(profile))?["subscriptions"] as JsonArray ?? [];

        JsonNode? match = wanted is null
            ? subscriptions.FirstOrDefault(s => s?["isDefault"]?.GetValue<bool>() == true)
            : subscriptions.FirstOrDefault(s => string.Equals(s?["name"]?.GetValue<string>(), wanted, StringComparison.OrdinalIgnoreCase));

        if (match?["id"]?.GetValue<string>() is not string id)
        {
            throw new GraphException(wanted is null
                ? $"The Azure CLI profile at {profile} marks no subscription as default. Run 'az account set --subscription <id>', or pass one."
                : $"No subscription named '{wanted}' in the Azure CLI profile. Known: " +
                  string.Join(", ", subscriptions.Select(s => s?["name"]?.GetValue<string>()).Where(n => n is not null)) + ".");
        }

        return new AzureSubscription(id.ToLowerInvariant(), match["name"]?.GetValue<string>(), wanted is null ? "az default" : "explicit");
    }
}

/// <summary>
/// HTTP to Azure -- Resource Manager, the storage data plane and Graph -- with the token from the
/// Azure CLI's sign-in.
/// </summary>
/// <remarks>
/// REST rather than the Azure SDK's management libraries, deliberately. Each of those is a
/// package per resource provider (Resources, Authorization, Storage) with its own model types and
/// dependency tree, for calls that are a URL, a verb and a JSON body. Janet.Core takes package
/// dependencies only with an argument, and "saves writing a URL" is not one. The token comes from
/// <see cref="AzToken"/>, so every call reuses the cached sign-in instead of starting `az`.
///
/// Synchronous like the rest of Janet.Core: the front ends are a console app and tool methods
/// that return a string, and nothing here benefits from overlapping requests.
///
/// The handler, the token source and the sleep are all injectable, so the tests drive every path
/// -- including a long-running operation's polling -- without a network and without waiting.
/// </remarks>
public sealed class AzureHttp
{
    public const string Management = "https://management.azure.com";

    /// <summary>API versions, pinned. A version is a contract with the service; floating it is how a response shape changes under you.</summary>
    public const string ResourcesApi = "2024-03-01";
    public const string AuthorizationApi = "2022-04-01";
    public const string StorageApi = "2023-05-01";

    private static readonly Lazy<AzureHttp> SharedInstance = new(() => new AzureHttp());

    /// <summary>The process-wide instance the front ends use. Its HttpClient lives as long as the process.</summary>
    public static AzureHttp Shared => SharedInstance.Value;

    private readonly HttpClient _client;
    private readonly Func<string, string> _token;
    private readonly Action<TimeSpan> _sleep;

    public AzureHttp(HttpMessageHandler? handler = null, Func<string, string>? token = null, Action<TimeSpan>? sleep = null)
    {
        _client = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            // Declared, as every external wait in this codebase is. A request that hangs past
            // this is reported as unreachable rather than presenting as a tool that never returns.
            Timeout = TimeSpan.FromSeconds(60),
        };
        _token = token ?? (scope => AzToken.Acquire(new AzTokenRequest { Scope = scope, Raw = true }).Token!);
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>A bearer token for a scope alias, from the same source every request here uses.</summary>
    public string Token(string scope) => _token(scope);

    public void Sleep(TimeSpan duration) => _sleep(duration);

    /// <summary>
    /// One request, read fully. Network failure is NOT caught here: the storage probe needs to
    /// tell "unreachable" from "refused", so the caller decides what a failure to connect means.
    /// </summary>
    public AzureResponse Send(
        HttpMethod method,
        string url,
        string scope,
        JsonNode? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using HttpRequestMessage request = new(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token(scope));

        foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = _client.Send(request);
        using StreamReader reader = new(response.Content.ReadAsStream());

        Dictionary<string, string> collected = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IEnumerable<string> values) in response.Headers.Concat(response.Content.Headers))
        {
            collected[name] = string.Join(",", values);
        }

        return new AzureResponse((int)response.StatusCode, reader.ReadToEnd(), collected);
    }

    /// <summary>A Resource Manager request. Network failure becomes a GraphException naming ARM.</summary>
    public AzureResponse Arm(HttpMethod method, string pathAndQuery, JsonNode? body = null)
    {
        string url = pathAndQuery.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? pathAndQuery
            : Management + pathAndQuery;

        try
        {
            return Send(method, url, "arm", body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new GraphException($"Could not reach Azure Resource Manager ({method} {url}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A Resource Manager request that must succeed. A refusal becomes a GraphException carrying
    /// ARM's code, message and details, because a failed READ is the caller's problem to fix --
    /// the wrong id, a missing role -- not a result.
    /// </summary>
    public JsonObject ArmJson(HttpMethod method, string pathAndQuery, string what, JsonNode? body = null)
    {
        AzureResponse response = Arm(method, pathAndQuery, body);

        if (!response.IsSuccess)
        {
            throw new GraphException($"Azure refused to {what} (HTTP {response.Status}).{Environment.NewLine}{AzureErrors.Describe(response.Error()!)}");
        }

        return response.Json() ?? [];
    }

    /// <summary>Every item of a paged ARM list, following nextLink to the end.</summary>
    public IReadOnlyList<JsonObject> ArmList(string pathAndQuery, string what)
    {
        List<JsonObject> items = [];
        string? next = pathAndQuery;

        while (next is not null)
        {
            JsonObject page = ArmJson(HttpMethod.Get, next, what);
            items.AddRange((page["value"] as JsonArray ?? []).OfType<JsonObject>());
            next = page["nextLink"]?.GetValue<string>();
        }

        return items;
    }

    /// <summary>
    /// Follows an ARM long-running operation from its 201/202 to the end, and returns the final
    /// response.
    /// </summary>
    /// <remarks>
    /// Location is preferred over Azure-AsyncOperation because it answers with the RESULT -- for a
    /// what-if, the changes -- where the async-operation URL answers only with a status. A
    /// response that carries neither header is already final. Retry-After is honoured within
    /// [1, 30] seconds: a server asking for zero would make this a busy loop, and one asking for
    /// minutes would make it look hung.
    ///
    /// The limit is measured both in real time and in time slept, whichever runs out first, so a
    /// test with a no-op sleep is still bounded.
    /// </remarks>
    public AzureResponse AwaitOperation(AzureResponse accepted, TimeSpan limit, string what)
    {
        AzureResponse current = accepted;
        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan slept = TimeSpan.Zero;

        while (true)
        {
            string? location = current.Header("Location");
            string? asyncOperation = current.Header("Azure-AsyncOperation");

            if (current.Status != 202 && current.Status != 201)
            {
                return current;
            }

            if (location is null && asyncOperation is null)
            {
                return current;
            }

            if (location is null && asyncOperation is not null)
            {
                // Status-only polling: done when the status is terminal.
                string? status = current.Json()?["status"]?.GetValue<string>();
                if (status is "Succeeded" or "Failed" or "Canceled")
                {
                    return current;
                }
            }

            TimeSpan wait = TimeSpan.FromSeconds(Math.Clamp(
                int.TryParse(current.Header("Retry-After"), out int seconds) ? seconds : 5, 1, 30));

            if (clock.Elapsed + wait > limit || slept + wait > limit)
            {
                throw new GraphException(
                    $"Azure had not finished the {what} after {limit.TotalMinutes:0.#} minutes. It may still complete; " +
                    $"the operation is at {location ?? asyncOperation}.");
            }

            _sleep(wait);
            slept += wait;

            AzureResponse polled = Arm(HttpMethod.Get, location ?? asyncOperation!);

            // Polling an async-operation URL returns 200 with a status body until it is terminal;
            // keep the original headers so the loop knows where to poll next.
            current = location is null && polled.IsSuccess
                && polled.Json()?["status"]?.GetValue<string>() is not ("Succeeded" or "Failed" or "Canceled")
                ? polled with { Status = 202, Headers = current.Headers }
                : polled;
        }
    }

    /// <summary>The segment after a key in an ARM resource id, case-insensitively, or null.</summary>
    /// <example>Segment("/subscriptions/x/resourceGroups/rg/...", "resourceGroups") is "rg".</example>
    public static string? Segment(string resourceId, string key)
    {
        string[] parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return null;
    }
}
