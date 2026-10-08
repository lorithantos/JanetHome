using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The storage probe against canned management- and data-plane answers: one target per status a
/// caller acts on, and the promise that it reports counts and never names.
/// </summary>
public class StorageProbeTests
{
    private const string AccountName = "neelamdata";

    /// <summary>A blob name that must never leave the probe.</summary>
    private const string SecretBlob = "drafts/ayesha-khan-consultation-notes.json";

    private const string SecretEntity = "ayesha-khan-private-row";

    private static readonly string AccountId =
        $"/subscriptions/{StubAzure.Sub}/resourceGroups/rg-neelam/providers/Microsoft.Storage/storageAccounts/{AccountName}";

    private static JsonObject AccountJson() => new()
    {
        ["id"] = AccountId,
        ["name"] = AccountName,
        ["location"] = "westeurope",
        ["kind"] = "StorageV2",
        ["sku"] = new JsonObject { ["name"] = "Standard_LRS", ["tier"] = "Standard" },
        ["properties"] = new JsonObject
        {
            ["provisioningState"] = "Succeeded",
            ["statusOfPrimary"] = "available",
            ["allowSharedKeyAccess"] = false,
            ["publicNetworkAccess"] = "Enabled",
            ["minimumTlsVersion"] = "TLS1_2",
            ["networkAcls"] = new JsonObject
            {
                ["defaultAction"] = "Deny",
                ["bypass"] = "AzureServices",
                ["ipRules"] = new JsonArray(new JsonObject { ["value"] = "203.0.113.7", ["action"] = "Allow" }),
                ["virtualNetworkRules"] = new JsonArray(),
            },
            ["privateEndpointConnections"] = new JsonArray(),
            ["primaryEndpoints"] = new JsonObject
            {
                ["blob"] = $"https://{AccountName}.blob.core.windows.net/",
                ["table"] = $"https://{AccountName}.table.core.windows.net/",
            },
        },
    };

    private const string Blob = $"https://{AccountName}.blob.core.windows.net/";
    private const string Table = $"https://{AccountName}.table.core.windows.net/";

    private static StubAzure Stub() => new StubAzure()
        .On(HttpMethod.Get, $"/storageAccounts/{AccountName}?", StubAzure.Json(200, AccountJson()))
        .On(HttpMethod.Get, Blob + "drafts?", StubAzure.Xml(200, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <EnumerationResults ServiceEndpoint="{Blob}" ContainerName="drafts"><MaxResults>5</MaxResults>
            <Blobs><Blob><Name>{SecretBlob}</Name><Properties /></Blob><Blob><Name>drafts/second.json</Name><Properties /></Blob></Blobs>
            <NextMarker>2!88!MDAwMDQ1</NextMarker></EnumerationResults>
            """))
        .On(HttpMethod.Get, Blob + "quiet?", StubAzure.Xml(200, """
            <?xml version="1.0" encoding="utf-8"?><EnumerationResults><Blobs /><NextMarker /></EnumerationResults>
            """))
        .On(HttpMethod.Get, Blob + "locked?", StubAzure.Xml(403, """
            <?xml version="1.0" encoding="utf-8"?><Error><Code>AuthorizationPermissionMismatch</Code><Message>This request is not authorized to perform this operation using this permission.
            RequestId:0b1c</Message></Error>
            """, ("x-ms-error-code", "AuthorizationPermissionMismatch")))
        .On(HttpMethod.Get, Blob + "gone?", StubAzure.Xml(404, """
            <?xml version="1.0" encoding="utf-8"?><Error><Code>ContainerNotFound</Code><Message>The specified container does not exist.</Message></Error>
            """, ("x-ms-error-code", "ContainerNotFound")))
        .On(HttpMethod.Get, Blob + "private?", StubAzure.Throws(
            new HttpRequestException("An error occurred while sending the request.", new IOException("No such host is known."))))
        .On(HttpMethod.Get, Table + "catalog()", StubAzure.Json(200, $$"""
            {"value":[{"PartitionKey":"{{SecretEntity}}","RowKey":"1"},{"PartitionKey":"p","RowKey":"2"},{"PartitionKey":"p","RowKey":"3"}]}
            """, ("x-ms-continuation-NextPartitionKey", "1!8!cA--")))
        .On(HttpMethod.Get, Table + "fenced()", StubAzure.Json(403, """
            {"odata.error":{"code":"AuthorizationFailure","message":{"lang":"en-US","value":"This request is not authorized to perform this operation."}}}
            """));

    private static readonly StorageProbeRequest Request = new()
    {
        Account = AccountName,
        ResourceGroup = "rg-neelam",
        Subscription = StubAzure.Sub,
        Containers = ["drafts", "quiet", "locked", "gone", "private"],
        Tables = ["catalog", "fenced"],
    };

    private static StorageProbeResult Probe(StubAzure stub) => new StorageProbe(stub.Http()).Probe(Request);

    private static StorageTarget Target(StorageProbeResult result, string name) => Assert.Single(result.Targets, t => t.Name == name);

    [Fact]
    public void AReadableContainerReportsHowManyBlobsItSawAndThatThereWereMore()
    {
        Assert.Equal(new StorageTarget("blob", "drafts", "ok", 200, null, null, 2, true), Target(Probe(Stub()), "drafts"));
    }

    [Fact]
    public void AnEmptyContainerWithAnEmptyMarkerHasNoMore()
    {
        Assert.Equal(new StorageTarget("blob", "quiet", "ok", 200, null, null, 0, false), Target(Probe(Stub()), "quiet"));
    }

    [Fact]
    public void AReadableTableReportsItsEntitiesAndTheContinuationHeader()
    {
        Assert.Equal(new StorageTarget("table", "catalog", "ok", 200, null, null, 3, true), Target(Probe(Stub()), "catalog"));
    }

    [Fact]
    public void AMissingDataRoleIsForbiddenWithAuthorizationPermissionMismatch()
    {
        Assert.Equal(
            new StorageTarget("blob", "locked", "forbidden", 403, "AuthorizationPermissionMismatch",
                "This request is not authorized to perform this operation using this permission.", null, null),
            Target(Probe(Stub()), "locked"));
    }

    [Fact]
    public void ANetworkRuleIsForbiddenWithAuthorizationFailure()
    {
        Assert.Equal(
            new StorageTarget("table", "fenced", "forbidden", 403, "AuthorizationFailure",
                "This request is not authorized to perform this operation.", null, null),
            Target(Probe(Stub()), "fenced"));
    }

    [Fact]
    public void AMissingContainerIsNotFound()
    {
        StorageTarget target = Target(Probe(Stub()), "gone");

        Assert.Equal("notFound", target.Status);
        Assert.Equal(404, target.HttpStatus);
        Assert.Equal("ContainerNotFound", target.ErrorCode);
    }

    [Fact]
    public void NoHttpAnswerAtAllIsUnreachableNotForbidden()
    {
        Assert.Equal(
            new StorageTarget("blob", "private", "unreachable", null, null, "No such host is known.", null, null),
            Target(Probe(Stub()), "private"));
    }

    [Fact]
    public void TheSummaryCountsEveryStatusEvenAtZero()
    {
        StorageProbeResult result = Probe(Stub());

        Assert.Equal(
            ["ok=3", "forbidden=2", "notFound=1", "unreachable=1", "error=0"],
            result.Summary.Select(kv => $"{kv.Key}={kv.Value}"));
        Assert.Equal(["blob", "blob", "blob", "blob", "blob", "table", "table"], result.Targets.Select(t => t.Service));
    }

    [Fact]
    public void TheCallerIsReadFromTheStorageTokensClaims()
    {
        StubAzure stub = Stub();

        StorageProbeResult result = Probe(stub);

        Assert.Equal(new StorageCaller(StubAzure.ObjectId, StubAzure.Upn), result.Caller);

        // Data-plane reads carry a storage token, not the ARM one.
        Assert.All(stub.Requests.Where(r => r.Url.StartsWith(Blob, StringComparison.Ordinal) || r.Url.StartsWith(Table, StringComparison.Ordinal)),
            r => Assert.StartsWith("Bearer ", r.Authorization));
        // Seven reads, plus the one the caller's identity was read from.
        Assert.Equal(8, stub.TokenScopes.Count(s => s == "storage"));
    }

    [Fact]
    public void NoBlobNameOrEntityEverReachesTheEnvelope()
    {
        // The whole point of counting: the probe can be pointed at a client's account without
        // copying any of the client's data into a transcript.
        StorageProbeResult result = Probe(Stub());

        foreach (string text in new[] { AzureJson.Serialize(result), AzureJson.Serialize(result, pretty: true), AzureJson.Render(result) })
        {
            Assert.DoesNotContain("ayesha", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("second.json", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ContainersAndTablesNotNamedAreListedFromTheManagementPlaneInNameOrder()
    {
        StubAzure stub = Stub()
            .On(HttpMethod.Get, "/blobServices/default/containers?", StubAzure.Json(200, """{"value":[{"name":"quiet"},{"name":"drafts"}]}"""))
            .On(HttpMethod.Get, "/tableServices/default/tables?", StubAzure.Json(200, """{"value":[]}"""));

        StorageProbeResult result = new StorageProbe(stub.Http()).Probe(new StorageProbeRequest
        {
            Account = AccountName,
            ResourceGroup = "rg-neelam",
            Subscription = StubAzure.Sub,
        });

        Assert.Equal(["drafts", "quiet"], result.Targets.Select(t => t.Name));
    }

    [Fact]
    public void TheSettingsAreTheOnesThatDecideWhoCanReachTheData()
    {
        StorageAccountSettings settings = StorageProbe.Settings(AccountJson());

        Assert.Equal("rg-neelam", settings.ResourceGroup);
        Assert.Equal("Standard_LRS", settings.Sku);
        Assert.False(settings.AllowSharedKeyAccess);
        Assert.Equal("Deny", settings.NetworkDefaultAction);
        Assert.Equal(1, settings.IpRules);
        Assert.Equal(0, settings.VirtualNetworkRules);
        Assert.Equal(0, settings.PrivateEndpoints);
    }

    [Fact]
    public void UnsetSettingsStayNullAndEndpointsFallBackToTheAccountName()
    {
        StorageAccountSettings settings = StorageProbe.Settings(new JsonObject { ["id"] = AccountId, ["name"] = AccountName });

        Assert.Null(settings.AllowSharedKeyAccess);
        Assert.Null(settings.PublicNetworkAccess);
        Assert.Equal(0, settings.IpRules);
        Assert.Equal(Blob, settings.BlobEndpoint);
        Assert.Equal(Table, settings.TableEndpoint);
    }
}
