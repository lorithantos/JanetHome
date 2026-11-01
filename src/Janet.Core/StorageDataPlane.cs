using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Janet.Core;

/// <summary>One blob as a container listing describes it.</summary>
public sealed record BlobItem(string Name, string ETag, long Length, string? ContentType, IReadOnlyDictionary<string, string> Metadata);

/// <summary>One table entity: its keys, the ETag the service gave it, and the whole body.</summary>
public sealed record EntityItem(string PartitionKey, string RowKey, string ETag, JsonObject Body);

/// <summary>
/// The storage data plane, thinly: list, copy and delete blobs and entities with the Entra token
/// <see cref="AzureHttp"/> already holds for the "storage" scope.
/// </summary>
/// <remarks>
/// READS THROW, WRITES RETURN. A listing that is refused is the caller's problem to fix (a
/// missing data role), so it becomes a GraphException carrying the service's code; the one
/// exception is 404, which is an answer (null: no such container or table). A write returns its
/// <see cref="AzureResponse"/> untouched, and a network failure propagates, because the caller
/// that is copying or deleting many items decides what one failure means for the rest.
/// </remarks>
public sealed class StorageDataPlane(AzureHttp http)
{
    public static StorageDataPlane Shared { get; } = new(AzureHttp.Shared);

    private static readonly IReadOnlyDictionary<string, string> BlobHeaders =
        new Dictionary<string, string> { ["x-ms-version"] = StorageProbe.BlobApi };

    /// <summary>Every blob under the prefix, all pages, with metadata. Null when the container does not exist.</summary>
    public IReadOnlyList<BlobItem>? ListBlobs(string blobEndpoint, string container, string? prefix)
    {
        List<BlobItem> items = [];
        string? marker = null;

        do
        {
            string url = $"{blobEndpoint.TrimEnd('/')}/{Uri.EscapeDataString(container)}?restype=container&comp=list&include=metadata";
            if (!string.IsNullOrEmpty(prefix))
            {
                url += "&prefix=" + Uri.EscapeDataString(prefix);
            }

            if (marker is not null)
            {
                url += "&marker=" + Uri.EscapeDataString(marker);
            }

            AzureResponse response = http.Send(HttpMethod.Get, url, "storage", headers: BlobHeaders);
            if (response.Status == 404)
            {
                return null;
            }

            Require(response, $"list blobs in container '{container}'");

            XElement root = XDocument.Parse(response.Body).Root!;
            foreach (XElement blob in root.Element("Blobs")?.Elements("Blob") ?? [])
            {
                XElement? properties = blob.Element("Properties");
                Dictionary<string, string> metadata = new(StringComparer.Ordinal);
                foreach (XElement entry in blob.Element("Metadata")?.Elements() ?? [])
                {
                    metadata[entry.Name.LocalName] = entry.Value;
                }

                items.Add(new BlobItem(
                    blob.Element("Name")?.Value ?? string.Empty,
                    properties?.Element("Etag")?.Value ?? string.Empty,
                    long.TryParse(properties?.Element("Content-Length")?.Value, out long length) ? length : 0,
                    properties?.Element("Content-Type")?.Value,
                    metadata));
            }

            string? next = root.Element("NextMarker")?.Value;
            marker = string.IsNullOrEmpty(next) ? null : next;
        }
        while (marker is not null);

        return items;
    }

    /// <summary>Every entity in one partition, following continuation tokens. Null when the table does not exist.</summary>
    public IReadOnlyList<EntityItem>? QueryPartition(string tableEndpoint, string table, string partitionKey)
    {
        List<EntityItem> items = [];
        string filter = Uri.EscapeDataString($"PartitionKey eq '{partitionKey.Replace("'", "''")}'");
        string? nextPartition = null;
        string? nextRow = null;
        Dictionary<string, string> headers = new()
        {
            ["x-ms-version"] = StorageProbe.TableApi,
            ["Accept"] = "application/json;odata=minimalmetadata",
        };

        do
        {
            string url = $"{tableEndpoint.TrimEnd('/')}/{Uri.EscapeDataString(table)}()?$filter={filter}";
            if (nextPartition is not null)
            {
                url += "&NextPartitionKey=" + Uri.EscapeDataString(nextPartition);
                if (nextRow is not null)
                {
                    url += "&NextRowKey=" + Uri.EscapeDataString(nextRow);
                }
            }

            AzureResponse response = http.Send(HttpMethod.Get, url, "storage", headers: headers);
            if (response.Status == 404)
            {
                return null;
            }

            Require(response, $"query table '{table}'");

            foreach (JsonObject entity in (response.Json()?["value"] as JsonArray ?? []).OfType<JsonObject>())
            {
                items.Add(new EntityItem(
                    entity["PartitionKey"]?.GetValue<string>() ?? string.Empty,
                    entity["RowKey"]?.GetValue<string>() ?? string.Empty,
                    entity["odata.etag"]?.GetValue<string>() ?? string.Empty,
                    entity));
            }

            nextPartition = response.Header("x-ms-continuation-NextPartitionKey");
            nextRow = response.Header("x-ms-continuation-NextRowKey");
        }
        while (nextPartition is not null);

        return items;
    }

    /// <summary>
    /// Put Blob From URL: the service reads the source itself, so no bytes pass through here. No
    /// body is sent; the source is authorised with the same Entra token.
    /// </summary>
    public AzureResponse PutBlobFromUrl(
        string blobEndpoint,
        string container,
        string name,
        string sourceUrl,
        string? contentType,
        IReadOnlyDictionary<string, string> metadata)
    {
        Dictionary<string, string> headers = new()
        {
            ["x-ms-version"] = StorageProbe.BlobApi,
            ["x-ms-blob-type"] = "BlockBlob",
            ["x-ms-copy-source"] = sourceUrl,
            ["x-ms-copy-source-authorization"] = "Bearer " + http.Token("storage"),
        };

        if (contentType is not null)
        {
            headers["x-ms-blob-content-type"] = contentType;
        }

        foreach ((string key, string value) in metadata)
        {
            headers["x-ms-meta-" + key] = value;
        }

        return http.Send(HttpMethod.Put, BlobUrl(blobEndpoint, container, name), "storage", headers: headers);
    }

    public AzureResponse DeleteBlob(string blobEndpoint, string container, string name) =>
        http.Send(HttpMethod.Delete, BlobUrl(blobEndpoint, container, name), "storage", headers: BlobHeaders);

    /// <summary>Insert Or Replace: a PUT with no If-Match.</summary>
    public AzureResponse UpsertEntity(string tableEndpoint, string table, JsonObject entity) =>
        http.Send(
            HttpMethod.Put,
            EntityUrl(
                tableEndpoint,
                table,
                entity["PartitionKey"]?.GetValue<string>() ?? throw new ArgumentException("The entity has no PartitionKey.", nameof(entity)),
                entity["RowKey"]?.GetValue<string>() ?? throw new ArgumentException("The entity has no RowKey.", nameof(entity))),
            "storage",
            entity,
            TableHeaders());

    public AzureResponse DeleteEntity(string tableEndpoint, string table, string partitionKey, string rowKey)
    {
        Dictionary<string, string> headers = TableHeaders();
        headers["If-Match"] = "*";
        return http.Send(HttpMethod.Delete, EntityUrl(tableEndpoint, table, partitionKey, rowKey), "storage", headers: headers);
    }

    /// <summary>Endpoint, container and name, each name segment escaped on its own so the slashes stay.</summary>
    public static string BlobUrl(string blobEndpoint, string container, string name) =>
        $"{blobEndpoint.TrimEnd('/')}/{Uri.EscapeDataString(container)}/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}";

    /// <summary>
    /// A copy of the entity for another partition: the service-owned fields (ETag, metadata URL,
    /// Timestamp) dropped, the rest -- including type annotations such as ApprovedAt@odata.type --
    /// kept as it was.
    /// </summary>
    public static JsonObject ForTarget(JsonObject entity, string targetPartitionKey)
    {
        JsonObject copy = (JsonObject)entity.DeepClone();
        foreach (string field in new[] { "odata.etag", "odata.metadata", "Timestamp", "Timestamp@odata.type" })
        {
            copy.Remove(field);
        }

        copy["PartitionKey"] = targetPartitionKey;
        return copy;
    }

    private static Dictionary<string, string> TableHeaders() => new()
    {
        ["x-ms-version"] = StorageProbe.TableApi,
        ["Accept"] = "application/json;odata=minimalmetadata",
    };

    private static string EntityUrl(string tableEndpoint, string table, string partitionKey, string rowKey) =>
        $"{tableEndpoint.TrimEnd('/')}/{Uri.EscapeDataString(table)}(PartitionKey='{Key(partitionKey)}',RowKey='{Key(rowKey)}')";

    /// <summary>OData doubles a quote inside a key; the whole thing is then percent-encoded.</summary>
    private static string Key(string key) => Uri.EscapeDataString(key.Replace("'", "''"));

    private static void Require(AzureResponse response, string what)
    {
        if (!response.IsSuccess)
        {
            throw new GraphException($"Azure refused to {what} (HTTP {response.Status}).{Environment.NewLine}{AzureErrors.Describe(response.Error()!)}");
        }
    }
}
