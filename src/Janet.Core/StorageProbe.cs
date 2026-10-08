using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Janet.Core;

/// <summary>What to probe.</summary>
public sealed class StorageProbeRequest
{
    public required string Account { get; init; }

    /// <summary>Speeds up the lookup and disambiguates nothing -- account names are global. Optional.</summary>
    public string? ResourceGroup { get; init; }

    public string? Subscription { get; init; }

    /// <summary>Containers to probe. Empty means every container the management plane lists.</summary>
    public IReadOnlyList<string> Containers { get; init; } = [];

    /// <summary>Tables to probe. Empty means every table the management plane lists.</summary>
    public IReadOnlyList<string> Tables { get; init; } = [];
}

/// <summary>The account as the management plane describes it: the settings that decide who can reach the data.</summary>
/// <param name="AllowSharedKeyAccess">Null means unset, which Azure treats as allowed.</param>
/// <param name="PublicNetworkAccess">Null means unset, which Azure treats as Enabled.</param>
public sealed record StorageAccountSettings(
    string Id,
    string ResourceGroup,
    string? Location,
    string? Kind,
    string? Sku,
    string? ProvisioningState,
    string? StatusOfPrimary,
    bool? AllowSharedKeyAccess,
    string? PublicNetworkAccess,
    string? NetworkDefaultAction,
    string? NetworkBypass,
    int IpRules,
    int VirtualNetworkRules,
    int PrivateEndpoints,
    string? MinimumTlsVersion,
    string BlobEndpoint,
    string TableEndpoint);

/// <summary>One container or table, reached (or not) over the data plane with an Entra token.</summary>
/// <param name="Status">ok, forbidden, notFound, unreachable or error.</param>
/// <param name="ErrorCode">The service's own code: AuthorizationPermissionMismatch means no data role; AuthorizationFailure usually means a network rule refused the caller.</param>
/// <param name="ItemsSeen">How many blobs or entities the probe read, up to <see cref="StorageProbe.SampleSize"/>. Null unless ok.</param>
/// <param name="More">Whether there were more than that. Null unless ok.</param>
public sealed record StorageTarget(
    string Service,
    string Name,
    string Status,
    int? HttpStatus,
    string? ErrorCode,
    string? Message,
    int? ItemsSeen,
    bool? More);

/// <summary>Who the probe ran as, read from the token's own claims.</summary>
public sealed record StorageCaller(string? ObjectId, string? Name);

public sealed record StorageProbeResult
{
    /// <summary>Format version. See contracts\storage-probe.schema.json.</summary>
    public const int ContractVersion = 1;

    public int Contract => ContractVersion;

    public required string Account { get; init; }

    public required AzureSubscription Subscription { get; init; }

    public required StorageCaller Caller { get; init; }

    public required StorageAccountSettings Settings { get; init; }

    public required IReadOnlyList<StorageTarget> Targets { get; init; }

    /// <summary>Count per status, over every target.</summary>
    public required IReadOnlyDictionary<string, int> Summary { get; init; }
}

/// <summary>
/// Answers "can THIS machine, signed in as THIS person, read this storage account" -- from the
/// account's settings and from an actual read of each container and table.
/// </summary>
/// <remarks>
/// TWO PLANES, BECAUSE THEY FAIL DIFFERENTLY. Owner of a subscription can list a storage
/// account's containers through Resource Manager and still be refused every blob, because data
/// access is a separate set of roles. Reading only the management plane would report an account
/// that looks fine to a person locked out of it; so each target is actually read, with the same
/// Entra token an application signed in as this person would use.
///
/// COUNTS, NEVER NAMES. A probe reads at most <see cref="SampleSize"/> items per target and
/// reports how many it saw. It never returns a blob name or an entity, so it can be pointed at
/// an account holding someone else's data without copying any of it into a transcript.
///
/// The distinctions a caller acts on are kept apart: forbidden with
/// AuthorizationPermissionMismatch (grant a data role), forbidden with AuthorizationFailure
/// (usually a firewall or network rule), unreachable (DNS or TCP never got a response), and
/// notFound.
/// </remarks>
public sealed class StorageProbe(AzureHttp http)
{
    public const int SampleSize = 5;

    /// <summary>Blob and table service versions, pinned.</summary>
    public const string BlobApi = "2023-11-03";
    public const string TableApi = "2019-02-02";

    private static readonly string[] Statuses = ["ok", "forbidden", "notFound", "unreachable", "error"];

    public static StorageProbe Shared { get; } = new(AzureHttp.Shared);

    public StorageProbeResult Probe(StorageProbeRequest request)
    {
        AzureSubscription subscription = AzureSubscription.Resolve(request.Subscription);
        JsonObject account = FindAccount(request, subscription);
        StorageAccountSettings settings = Settings(account);

        IReadOnlyList<string> containers = request.Containers.Count > 0
            ? request.Containers
            : Names($"{settings.Id}/blobServices/default/containers?api-version={AzureHttp.StorageApi}", "list containers");

        IReadOnlyList<string> tables = request.Tables.Count > 0
            ? request.Tables
            : Names($"{settings.Id}/tableServices/default/tables?api-version={AzureHttp.StorageApi}", "list tables");

        List<StorageTarget> targets = [];
        targets.AddRange(containers.Select(container => ProbeContainer(settings.BlobEndpoint, container)));
        targets.AddRange(tables.Select(table => ProbeTable(settings.TableEndpoint, table)));

        return new StorageProbeResult
        {
            Account = request.Account,
            Subscription = subscription,
            Caller = Caller(),
            Settings = settings,
            Targets = targets,
            Summary = Statuses.ToDictionary(status => status, status => targets.Count(t => t.Status == status), StringComparer.Ordinal),
        };
    }

    private JsonObject FindAccount(StorageProbeRequest request, AzureSubscription subscription)
    {
        if (!string.IsNullOrWhiteSpace(request.ResourceGroup))
        {
            AzureResponse response = http.Arm(
                HttpMethod.Get,
                $"/subscriptions/{subscription.Id}/resourceGroups/{Uri.EscapeDataString(request.ResourceGroup)}/providers/Microsoft.Storage/storageAccounts/{Uri.EscapeDataString(request.Account)}?api-version={AzureHttp.StorageApi}");

            if (response.IsSuccess)
            {
                return response.Json()!;
            }

            throw new GraphException(response.Status == 404
                ? $"No storage account '{request.Account}' in resource group '{request.ResourceGroup}' of subscription {subscription.Id}."
                : $"Azure refused to read storage account '{request.Account}' (HTTP {response.Status}).{Environment.NewLine}{AzureErrors.Describe(response.Error()!)}");
        }

        return http.ArmList(
                $"/subscriptions/{subscription.Id}/providers/Microsoft.Storage/storageAccounts?api-version={AzureHttp.StorageApi}",
                "list storage accounts")
            .FirstOrDefault(a => string.Equals(a["name"]?.GetValue<string>(), request.Account, StringComparison.OrdinalIgnoreCase))
            ?? throw new GraphException($"No storage account '{request.Account}' in subscription {subscription.Id} ({subscription.Name ?? subscription.Source}).");
    }

    /// <summary>The account's access-relevant settings. Exposed for the tests.</summary>
    public static StorageAccountSettings Settings(JsonObject account)
    {
        JsonNode? p = account["properties"];
        JsonNode? network = p?["networkAcls"];
        string id = account["id"]?.GetValue<string>() ?? string.Empty;
        string name = account["name"]?.GetValue<string>() ?? string.Empty;

        return new StorageAccountSettings(
            id,
            AzureHttp.Segment(id, "resourceGroups") ?? string.Empty,
            account["location"]?.GetValue<string>(),
            account["kind"]?.GetValue<string>(),
            account["sku"]?["name"]?.GetValue<string>(),
            p?["provisioningState"]?.GetValue<string>(),
            p?["statusOfPrimary"]?.GetValue<string>(),
            p?["allowSharedKeyAccess"]?.GetValue<bool>(),
            p?["publicNetworkAccess"]?.GetValue<string>(),
            network?["defaultAction"]?.GetValue<string>(),
            network?["bypass"]?.GetValue<string>(),
            (network?["ipRules"] as JsonArray)?.Count ?? 0,
            (network?["virtualNetworkRules"] as JsonArray)?.Count ?? 0,
            (p?["privateEndpointConnections"] as JsonArray)?.Count ?? 0,
            p?["minimumTlsVersion"]?.GetValue<string>(),
            p?["primaryEndpoints"]?["blob"]?.GetValue<string>() ?? $"https://{name}.blob.core.windows.net/",
            p?["primaryEndpoints"]?["table"]?.GetValue<string>() ?? $"https://{name}.table.core.windows.net/");
    }

    private IReadOnlyList<string> Names(string pathAndQuery, string what) =>
        [.. http.ArmList(pathAndQuery, what).Select(item => item["name"]?.GetValue<string>()).OfType<string>().Order(StringComparer.Ordinal)];

    private StorageTarget ProbeContainer(string endpoint, string container) =>
        Read("blob", container, $"{endpoint.TrimEnd('/')}/{Uri.EscapeDataString(container)}?restype=container&comp=list&maxresults={SampleSize}",
            new Dictionary<string, string> { ["x-ms-version"] = BlobApi },
            response =>
            {
                XElement root = XDocument.Parse(response.Body).Root!;
                int seen = root.Element("Blobs")?.Elements("Blob").Count() ?? 0;
                bool more = !string.IsNullOrEmpty(root.Element("NextMarker")?.Value);
                return (seen, more);
            });

    private StorageTarget ProbeTable(string endpoint, string table) =>
        Read("table", table, $"{endpoint.TrimEnd('/')}/{Uri.EscapeDataString(table)}()?$top={SampleSize}",
            new Dictionary<string, string>
            {
                ["x-ms-version"] = TableApi,
                ["Accept"] = "application/json;odata=nometadata",
            },
            response =>
            {
                int seen = (response.Json()?["value"] as JsonArray)?.Count ?? 0;
                bool more = response.Header("x-ms-continuation-NextPartitionKey") is not null;
                return (seen, more);
            });

    private StorageTarget Read(
        string service,
        string name,
        string url,
        IReadOnlyDictionary<string, string> headers,
        Func<AzureResponse, (int Seen, bool More)> count)
    {
        AzureResponse response;
        try
        {
            response = http.Send(HttpMethod.Get, url, "storage", headers: headers);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // No HTTP answer at all: DNS, TCP or TLS. A firewall that REFUSES answers 403; one
            // that silently drops, or a private endpoint with no route from here, lands here.
            return new StorageTarget(service, name, "unreachable", null, null, ex.InnerException?.Message ?? ex.Message, null, null);
        }

        if (response.IsSuccess)
        {
            try
            {
                (int seen, bool more) = count(response);
                return new StorageTarget(service, name, "ok", response.Status, null, null, seen, more);
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or System.Text.Json.JsonException)
            {
                return new StorageTarget(service, name, "error", response.Status, null, $"The service answered {response.Status} with a body that did not parse: {ex.Message}", null, null);
            }
        }

        AzureError error = response.Error()!;
        string status = response.Status switch
        {
            403 => "forbidden",
            404 => "notFound",
            _ => "error",
        };

        return new StorageTarget(service, name, status, response.Status, error.Code, error.Message.Length > 0 ? error.Message : null, null, null);
    }

    /// <summary>
    /// The identity in the storage token's claims. Read, not validated: the token came from this
    /// process's own sign-in, and the point is to say whose access was tested.
    /// </summary>
    private StorageCaller Caller()
    {
        try
        {
            string[] parts = http.Token("storage").Split('.');
            if (parts.Length < 2)
            {
                return new StorageCaller(null, null);
            }

            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            JsonNode? claims = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));

            return new StorageCaller(
                claims?["oid"]?.GetValue<string>(),
                claims?["upn"]?.GetValue<string>() ?? claims?["unique_name"]?.GetValue<string>() ?? claims?["appid"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new StorageCaller(null, null);
        }
    }
}
