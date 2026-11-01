using System.Text.Json.Nodes;
using System.Xml.Linq;
using Janet.Core;

namespace Janet.Tests;

/// <summary>
/// Two storage accounts, canned: a StubAzure that answers ARM, blob listings and table queries from
/// state the test edits, so a test says what the world holds and not which XML to send.
/// </summary>
/// <remarks>
/// The accounts are "src" and "tgt" (hosts src.blob.core.windows.net and so on). Answers are built
/// when the request arrives, not when the world is declared, so <see cref="SetETag"/> after a plan
/// changes what the next plan sees. A listing returns EVERYTHING in the container or table whatever
/// prefix or partition the request asked for -- the stub cannot see the request -- so the code under
/// test must hold its own scope; a test that wants a blob outside the scope just adds it.
/// <para>
/// WRITES: nothing here answers a PUT or DELETE yet. Task 4 (apply) registers its answers through
/// <see cref="OnWrite"/> and, if it wants the stored state to change, edits the lists below from the
/// answer lambda.
/// </para>
/// </remarks>
internal sealed class StorageCopyWorld
{
    public enum Side
    {
        Source,
        Target,
    }

    public const string SourceBlobEndpoint = "https://src.blob.core.windows.net/";
    public const string TargetBlobEndpoint = "https://tgt.blob.core.windows.net/";
    public const string SourceTableEndpoint = "https://src.table.core.windows.net/";
    public const string TargetTableEndpoint = "https://tgt.table.core.windows.net/";

    private readonly Dictionary<(Side, string), List<BlobItem>> _containers = [];
    private readonly Dictionary<(Side, string), List<EntityItem>> _tables = [];
    private readonly HashSet<(Side, string)> _missingContainers = [];
    private readonly HashSet<(Side, string)> _missingTables = [];
    private int _etag;

    public StorageCopyWorld()
    {
        Stub = new StubAzure()
            .On(HttpMethod.Get, "/storageAccounts?", ArmList())
            .On(HttpMethod.Get, "/storageAccounts/src?", Arm("src"))
            .On(HttpMethod.Get, "/storageAccounts/tgt?", Arm("tgt"));
    }

    public StubAzure Stub { get; }

    /// <summary>A world holding the containers and tables the neelam fixture names, empty, on both sides.</summary>
    public static StorageCopyWorld Neelam()
    {
        StorageCopyWorld world = new();
        foreach (Side side in new[] { Side.Source, Side.Target })
        {
            string client = side == Side.Source ? "test-salon-one" : "staging-salon";
            world.Container(side, client).Container(side, "settings").Table(side, "knownItems").Table(side, "approvals");
        }

        return world;
    }

    /// <summary>Declares a container (empty) and routes its listings.</summary>
    public StorageCopyWorld Container(Side side, string container)
    {
        if (_containers.TryAdd((side, container), []))
        {
            string host = side == Side.Source ? SourceBlobEndpoint : TargetBlobEndpoint;
            Stub.On(HttpMethod.Get, $"{host}{container}?", () => List(side, container));
        }

        return this;
    }

    /// <summary>Declares a table (empty) and routes its queries.</summary>
    public StorageCopyWorld Table(Side side, string table)
    {
        if (_tables.TryAdd((side, table), []))
        {
            string host = side == Side.Source ? SourceTableEndpoint : TargetTableEndpoint;
            Stub.On(HttpMethod.Get, $"{host}{table}()?", () => Query(side, table));
        }

        return this;
    }

    public StorageCopyWorld Blob(
        Side side,
        string container,
        string name,
        string? etag = null,
        long length = 10,
        string? contentType = "application/json",
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        Container(side, container);
        _containers[(side, container)].Add(new BlobItem(name, etag ?? NextETag(), length, contentType, metadata ?? new Dictionary<string, string>()));
        return this;
    }

    /// <summary>A blob carrying the metadata key "undone", as a deleted save does.</summary>
    public StorageCopyWorld UndoneBlob(Side side, string container, string name) =>
        Blob(side, container, name, metadata: new Dictionary<string, string> { ["undone"] = "yes" });

    public StorageCopyWorld Entity(Side side, string table, string partitionKey, string rowKey, string? etag = null, JsonObject? extra = null)
    {
        Table(side, table);
        JsonObject body = extra is null ? [] : (JsonObject)extra.DeepClone();
        body["odata.etag"] = etag ?? NextETag();
        body["PartitionKey"] = partitionKey;
        body["RowKey"] = rowKey;
        _tables[(side, table)].Add(new EntityItem(partitionKey, rowKey, (string)body["odata.etag"]!, body));
        return this;
    }

    /// <summary>The container answers 404 from now on.</summary>
    public StorageCopyWorld MissingContainer(Side side, string container)
    {
        Container(side, container);
        _missingContainers.Add((side, container));
        return this;
    }

    public StorageCopyWorld MissingTable(Side side, string table)
    {
        Table(side, table);
        _missingTables.Add((side, table));
        return this;
    }

    /// <summary>Changes one blob's ETag, as a write between a plan and its apply would.</summary>
    public StorageCopyWorld SetETag(Side side, string container, string name, string etag)
    {
        List<BlobItem> blobs = _containers[(side, container)];
        int at = blobs.FindIndex(b => b.Name == name);
        blobs[at] = blobs[at] with { ETag = etag };
        return this;
    }

    /// <summary>
    /// The seam for Task 4: scripted answers for a write (PUT or DELETE) whose URL contains the text.
    /// Registered after the reads, so a write URL never collides with a listing route.
    /// </summary>
    public StorageCopyWorld OnWrite(HttpMethod method, string urlContains, params Func<HttpResponseMessage>[] answers)
    {
        Stub.On(method, urlContains, answers);
        return this;
    }

    /// <summary>
    /// Every write not scripted by <see cref="OnWrite"/> succeeds on the TARGET account only, so a write
    /// sent to the source finds no route and throws from the stub, failing the test. Call it AFTER the
    /// scripted answers: the first matching route wins.
    /// </summary>
    public StorageCopyWorld AcceptWrites()
    {
        Stub.On(HttpMethod.Put, "tgt.blob.", StubAzure.Empty(201))
            .On(HttpMethod.Put, "tgt.table.", StubAzure.Empty(204))
            .On(HttpMethod.Delete, "tgt.blob.", StubAzure.Empty(202))
            .On(HttpMethod.Delete, "tgt.table.", StubAzure.Empty(204));

        return this;
    }

    public StorageCopy Copier() => new(Stub.Http());

    private string NextETag() => $"\"0x{++_etag}\"";

    private Func<HttpResponseMessage> ArmList() => StubAzure.Json(200, new JsonObject
    {
        ["value"] = new JsonArray(AccountJson("src"), AccountJson("tgt")),
    });

    private Func<HttpResponseMessage> Arm(string name) => StubAzure.Json(200, AccountJson(name));

    private static JsonObject AccountJson(string name) => new()
    {
        ["id"] = $"/subscriptions/{StubAzure.Sub}/resourceGroups/rg-test/providers/Microsoft.Storage/storageAccounts/{name}",
        ["name"] = name,
        ["properties"] = new JsonObject
        {
            ["primaryEndpoints"] = new JsonObject
            {
                ["blob"] = $"https://{name}.blob.core.windows.net/",
                ["table"] = $"https://{name}.table.core.windows.net/",
            },
        },
    };

    private HttpResponseMessage List(Side side, string container)
    {
        if (_missingContainers.Contains((side, container)))
        {
            return StubAzure.Xml(404, "<Error><Code>ContainerNotFound</Code><Message>no</Message></Error>", ("x-ms-error-code", "ContainerNotFound"))();
        }

        XElement blobs = new("Blobs");
        foreach (BlobItem blob in _containers[(side, container)])
        {
            blobs.Add(new XElement(
                "Blob",
                new XElement("Name", blob.Name),
                new XElement(
                    "Properties",
                    new XElement("Etag", blob.ETag),
                    new XElement("Content-Length", blob.Length),
                    new XElement("Content-Type", blob.ContentType)),
                new XElement("Metadata", blob.Metadata.Select(m => new XElement(m.Key, m.Value)))));
        }

        return StubAzure.Xml(200, new XElement("EnumerationResults", blobs, new XElement("NextMarker")).ToString())();
    }

    private HttpResponseMessage Query(Side side, string table)
    {
        if (_missingTables.Contains((side, table)))
        {
            return StubAzure.Json(404, new JsonObject { ["odata.error"] = new JsonObject { ["code"] = "TableNotFound", ["message"] = new JsonObject { ["value"] = "no" } } })();
        }

        JsonArray rows = [];
        foreach (EntityItem entity in _tables[(side, table)])
        {
            rows.Add(entity.Body.DeepClone());
        }

        return StubAzure.Json(200, new JsonObject { ["value"] = rows })();
    }
}
