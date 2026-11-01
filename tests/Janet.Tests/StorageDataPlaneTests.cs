using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

public class StorageDataPlaneTests
{
    private const string Blob = "https://acct.blob.core.windows.net/";
    private const string Table = "https://acct.table.core.windows.net/";

    private static string Page(string blobs, string? nextMarker = null) =>
        $"<?xml version=\"1.0\"?><EnumerationResults><Blobs>{blobs}</Blobs><NextMarker>{nextMarker}</NextMarker></EnumerationResults>";

    private static string BlobXml(string name, string etag, long length, string contentType, string metadata = "") =>
        $"<Blob><Name>{name}</Name><Properties><Etag>{etag}</Etag><Content-Length>{length}</Content-Length><Content-Type>{contentType}</Content-Type></Properties><Metadata>{metadata}</Metadata></Blob>";

    [Fact]
    public void ListBlobsReadsEveryPageWithMetadata()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Get, "comp=list",
            StubAzure.Xml(200, Page(BlobXml("a.png", "\"0x1\"", 10, "image/png", "<undone>yes</undone>"), "m1")),
            StubAzure.Xml(200, Page(BlobXml("images/b.png", "\"0x2\"", 20, "image/png"))));

        IReadOnlyList<BlobItem>? blobs = new StorageDataPlane(stub.Http()).ListBlobs(Blob, "c", null);

        Assert.NotNull(blobs);
        Assert.Equal(["a.png", "images/b.png"], blobs.Select(b => b.Name));
        Assert.Equal("yes", blobs[0].Metadata["undone"]);
        Assert.Equal(10, blobs[0].Length);
        Assert.Equal("image/png", blobs[0].ContentType);
        Assert.Equal("\"0x1\"", blobs[0].ETag);
        Assert.Equal(2, stub.Requests.Count);
        Assert.All(stub.Requests, r => Assert.Contains("include=metadata", r.Url));
        Assert.DoesNotContain("marker=", stub.Requests[0].Url);
        Assert.Contains("marker=m1", stub.Requests[1].Url);
    }

    [Fact]
    public void ListBlobsReturnsNullForAMissingContainer()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Get, "comp=list",
            StubAzure.Xml(404, "<Error><Code>ContainerNotFound</Code><Message>no</Message></Error>", ("x-ms-error-code", "ContainerNotFound")));

        Assert.Null(new StorageDataPlane(stub.Http()).ListBlobs(Blob, "c", null));
    }

    [Fact]
    public void ListBlobsThrowsWhenRefused()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Get, "comp=list",
            StubAzure.Xml(403, "<Error><Code>AuthorizationPermissionMismatch</Code><Message>no</Message></Error>", ("x-ms-error-code", "AuthorizationPermissionMismatch")));

        GraphException ex = Assert.Throws<GraphException>(() => new StorageDataPlane(stub.Http()).ListBlobs(Blob, "c", "p/"));

        Assert.Contains("AuthorizationPermissionMismatch", ex.Message);
    }

    [Fact]
    public void QueryPartitionFollowsContinuation()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Get, "()?",
            StubAzure.Json(200, new JsonObject
            {
                ["value"] = new JsonArray(new JsonObject { ["odata.etag"] = "W/\"1\"", ["PartitionKey"] = "test-salon-one", ["RowKey"] = "r1" }),
            }, ("x-ms-continuation-NextPartitionKey", "p2"), ("x-ms-continuation-NextRowKey", "r2")),
            StubAzure.Json(200, new JsonObject
            {
                ["value"] = new JsonArray(new JsonObject { ["odata.etag"] = "W/\"2\"", ["PartitionKey"] = "test-salon-one", ["RowKey"] = "r3" }),
            }));

        IReadOnlyList<EntityItem>? items = new StorageDataPlane(stub.Http()).QueryPartition(Table, "t", "test-salon-one");

        Assert.NotNull(items);
        Assert.Equal(["r1", "r3"], items.Select(i => i.RowKey));
        Assert.Equal("W/\"1\"", items[0].ETag);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Contains(Uri.EscapeDataString("PartitionKey eq 'test-salon-one'"), stub.Requests[0].Url);
        Assert.DoesNotContain("NextPartitionKey", stub.Requests[0].Url);
        Assert.Contains("NextPartitionKey=p2", stub.Requests[1].Url);
        Assert.Contains("NextRowKey=r2", stub.Requests[1].Url);
        // HttpClient re-serialises Accept as a parsed media type, adding a space after the semicolon.
        Assert.All(stub.Requests, r => Assert.Equal("application/json;odata=minimalmetadata", r.Headers["Accept"].Replace(" ", "")));
    }

    [Fact]
    public void QueryPartitionReturnsNullForAMissingTable()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Get, "()?",
            StubAzure.Json(404, new JsonObject { ["odata.error"] = new JsonObject { ["code"] = "TableNotFound", ["message"] = new JsonObject { ["value"] = "no" } } }));

        Assert.Null(new StorageDataPlane(stub.Http()).QueryPartition(Table, "t", "p"));
    }

    [Fact]
    public void PutBlobFromUrlSendsTheCopyHeaders()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Put, "/c/", StubAzure.Empty(201));
        const string source = "https://live.blob.core.windows.net/c/images/a.png";

        AzureResponse response = new StorageDataPlane(stub.Http()).PutBlobFromUrl(
            Blob, "c", "images/a.png", source, "image/png", new Dictionary<string, string> { ["undone"] = "yes" });

        Assert.True(response.IsSuccess);
        StubAzure.Recorded sent = Assert.Single(stub.Requests);
        Assert.Equal("BlockBlob", sent.Headers["x-ms-blob-type"]);
        Assert.Equal(source, sent.Headers["x-ms-copy-source"]);
        Assert.Equal("Bearer " + StubAzure.Jwt(StubAzure.ObjectId, StubAzure.Upn), sent.Headers["x-ms-copy-source-authorization"]);
        Assert.Equal("image/png", sent.Headers["x-ms-blob-content-type"]);
        Assert.Equal("yes", sent.Headers["x-ms-meta-undone"]);
        Assert.False(sent.Headers.ContainsKey("x-ms-meta-title"));
        Assert.Equal("2023-11-03", sent.Headers["x-ms-version"]);
        Assert.Null(sent.Body);
        Assert.False(sent.Headers.ContainsKey("Content-Type"));
    }

    [Fact]
    public void BlobUrlEscapesEachSegment() =>
        Assert.Equal(
            "https://a.blob.core.windows.net/c/images/Spring%20Offer%20%231.png",
            StorageDataPlane.BlobUrl("https://a.blob.core.windows.net/", "c", "images/Spring Offer #1.png"));

    [Fact]
    public void DeleteBlobSendsADelete()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Delete, "/c/a.png", StubAzure.Empty(202));

        Assert.True(new StorageDataPlane(stub.Http()).DeleteBlob(Blob, "c", "a.png").IsSuccess);
        Assert.Equal("2023-11-03", Assert.Single(stub.Requests).Headers["x-ms-version"]);
    }

    [Fact]
    public void EntityUrlEscapesQuotes()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Delete, "(PartitionKey=", StubAzure.Empty(204));

        new StorageDataPlane(stub.Http()).DeleteEntity(Table, "t", "p", "a'b");

        StubAzure.Recorded sent = Assert.Single(stub.Requests);
        Assert.Contains("(PartitionKey='p',RowKey='a%27%27b')", sent.Url);
        Assert.Equal("*", sent.Headers["If-Match"]);
    }

    [Fact]
    public void ForTargetRewritesThePartitionAndStripsServiceFields()
    {
        JsonObject input = new()
        {
            ["odata.etag"] = "W/\"1\"",
            ["odata.metadata"] = "https://x/$metadata#t/@Element",
            ["PartitionKey"] = "test-salon-one",
            ["RowKey"] = "r1",
            ["Timestamp"] = "2026-01-01T00:00:00Z",
            ["Timestamp@odata.type"] = "Edm.DateTime",
            ["ApprovedAt"] = "2026-01-02T00:00:00Z",
            ["ApprovedAt@odata.type"] = "Edm.DateTime",
            ["Title"] = "x",
        };

        JsonObject output = StorageDataPlane.ForTarget(input, "staging-salon");

        Assert.Equal("staging-salon", output["PartitionKey"]!.GetValue<string>());
        Assert.Equal("r1", output["RowKey"]!.GetValue<string>());
        Assert.Equal("2026-01-02T00:00:00Z", output["ApprovedAt"]!.GetValue<string>());
        Assert.Equal("Edm.DateTime", output["ApprovedAt@odata.type"]!.GetValue<string>());
        Assert.Equal("x", output["Title"]!.GetValue<string>());
        Assert.False(output.ContainsKey("odata.etag"));
        Assert.False(output.ContainsKey("odata.metadata"));
        Assert.False(output.ContainsKey("Timestamp"));
        Assert.False(output.ContainsKey("Timestamp@odata.type"));
        Assert.Equal("test-salon-one", input["PartitionKey"]!.GetValue<string>());
    }

    [Fact]
    public void UpsertEntityPutsWithoutIfMatch()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Put, "(PartitionKey=", StubAzure.Empty(204));
        JsonObject entity = new() { ["PartitionKey"] = "p", ["RowKey"] = "r", ["Title"] = "x" };

        new StorageDataPlane(stub.Http()).UpsertEntity(Table, "t", entity);

        StubAzure.Recorded sent = Assert.Single(stub.Requests);
        Assert.Contains("/t(PartitionKey='p',RowKey='r')", sent.Url);
        Assert.False(sent.Headers.ContainsKey("If-Match"));
        Assert.Equal(entity.ToJsonString(), sent.Body);
    }
}
