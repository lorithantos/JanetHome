using System.Text;
using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The transport under every Azure envelope: Azure's three error dialects read into one shape,
/// paged lists followed to the end, and long-running operations polled -- all without a network
/// and without waiting.
/// </summary>
public class AzureHttpTests
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private static AzureResponse Accepted(int status, string body = "", params (string Name, string Value)[] headers) =>
        new(status, body, headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void AnArmErrorKeepsItsCodeTargetAndNestedDetails()
    {
        AzureResponse response = new(400, """
            {"error":{"code":"InvalidTemplateDeployment","message":"The template deployment is not valid.",
              "details":[{"code":"PreflightValidationCheckFailed","message":"Preflight validation failed.",
                "target":"stneelam","details":[{"code":"StorageAccountAlreadyTaken","message":"taken"}]}]}}
            """, NoHeaders);

        AzureError error = response.Error()!;

        Assert.Equal("InvalidTemplateDeployment", error.Code);
        Assert.Null(error.Target);
        AzureError detail = Assert.Single(error.Details);
        Assert.Equal("PreflightValidationCheckFailed", detail.Code);
        Assert.Equal("stneelam", detail.Target);
        Assert.Equal("StorageAccountAlreadyTaken", Assert.Single(detail.Details).Code);
    }

    [Fact]
    public void ATableErrorIsReadFromItsODataDialect()
    {
        AzureResponse response = new(403, """
            {"odata.error":{"code":"AuthorizationFailure","message":{"lang":"en-US","value":"This request is not authorized to perform this operation."}}}
            """, NoHeaders);

        AzureError error = response.Error()!;

        Assert.Equal("AuthorizationFailure", error.Code);
        Assert.Equal("This request is not authorized to perform this operation.", error.Message);
        Assert.Empty(error.Details);
    }

    [Fact]
    public void ABlobErrorTakesItsCodeFromTheHeaderAndTheFirstLineOfTheXmlMessage()
    {
        AzureResponse response = new(403, """
            <?xml version="1.0" encoding="utf-8"?><Error><Code>SomethingElse</Code><Message>This request is not authorized to perform this operation using this permission.
            RequestId:0b1c
            Time:2026-10-08T10:00:00.0000000Z</Message></Error>
            """, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-error-code"] = "AuthorizationPermissionMismatch" });

        AzureError error = response.Error()!;

        // The header wins over the body: it is what the service documents as the code.
        Assert.Equal("AuthorizationPermissionMismatch", error.Code);
        Assert.Equal("This request is not authorized to perform this operation using this permission.", error.Message);
    }

    [Fact]
    public void ABlobErrorWithoutTheHeaderTakesItsCodeFromTheXml()
    {
        AzureResponse response = new(404, "<Error><Code>ContainerNotFound</Code><Message>The specified container does not exist.</Message></Error>", NoHeaders);

        Assert.Equal("ContainerNotFound", response.Error()!.Code);
    }

    [Fact]
    public void AnErrorInNoDialectStillAnswersWithTheStatus()
    {
        AzureResponse response = new(502, "<html>Bad gateway", NoHeaders);

        AzureError error = response.Error()!;

        Assert.Equal("HTTP 502", error.Code);
        Assert.Equal(string.Empty, error.Message);
    }

    [Fact]
    public void ASuccessCarriesNoError()
    {
        Assert.Null(new AzureResponse(200, """{"error":{"code":"looks like one"}}""", NoHeaders).Error());
    }

    [Fact]
    public void EveryRequestCarriesABearerTokenForItsOwnScope()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Get, "/subscriptions/x", StubAzure.Json(200, "{}"));

        stub.Http().Arm(HttpMethod.Get, "/subscriptions/x?api-version=1");

        Assert.Equal(["arm"], stub.TokenScopes);
        Assert.StartsWith("Bearer ", Assert.Single(stub.Requests).Authorization);
        Assert.Equal("https://management.azure.com/subscriptions/x?api-version=1", stub.Requests[0].Url);
    }

    [Fact]
    public void AnArmListFollowsNextLinkToTheEnd()
    {
        StubAzure stub = new StubAzure()
            .On(HttpMethod.Get, "/things?", StubAzure.Json(200, """{"value":[{"name":"a"},{"name":"b"}],"nextLink":"https://management.azure.com/things-page-2?token=x"}"""))
            .On(HttpMethod.Get, "/things-page-2", StubAzure.Json(200, """{"value":[{"name":"c"}]}"""));

        IReadOnlyList<JsonObject> items = stub.Http().ArmList("/things?api-version=1", "list things");

        Assert.Equal(["a", "b", "c"], items.Select(i => i["name"]!.GetValue<string>()));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public void ARefusedArmReadThrowsWithTheServiceCode()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Get, "/things", StubAzure.ArmError(403, "AuthorizationFailed", "no"));

        GraphException ex = Assert.Throws<GraphException>(() => stub.Http().ArmJson(HttpMethod.Get, "/things", "read things"));

        Assert.Contains("HTTP 403", ex.Message);
        Assert.Contains("AuthorizationFailed: no", ex.Message);
    }

    [Fact]
    public void AnArmRequestThatCannotConnectBecomesAGraphExceptionNamingArm()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Get, "/things", StubAzure.Throws(new HttpRequestException("No such host is known.")));

        GraphException ex = Assert.Throws<GraphException>(() => stub.Http().Arm(HttpMethod.Get, "/things"));

        Assert.Contains("Could not reach Azure Resource Manager", ex.Message);
    }

    [Fact]
    public void AnOperationIsPolledAtItsLocationUntilItAnswersWithTheResult()
    {
        const string location = "https://management.azure.com/operationResults/op1?api-version=1";
        StubAzure stub = new StubAzure().On(
            HttpMethod.Get,
            "/operationResults/op1",
            StubAzure.Empty(202, ("Location", location), ("Retry-After", "120")),
            StubAzure.Json(200, """{"status":"Succeeded","properties":{"changes":[]}}"""));

        AzureResponse final = stub.Http().AwaitOperation(Accepted(202, "", ("Location", location), ("Retry-After", "0")), TimeSpan.FromMinutes(10), "what-if");

        Assert.Equal(200, final.Status);
        Assert.Equal("Succeeded", final.Json()!["status"]!.GetValue<string>());
        Assert.Equal(2, stub.Requests.Count);

        // Retry-After is clamped to [1, 30]: zero would be a busy loop, two minutes looks hung.
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)], stub.Slept);
    }

    [Fact]
    public void LocationIsPreferredOverTheAsyncOperationUrl()
    {
        StubAzure stub = new StubAzure()
            .On(HttpMethod.Get, "/location", StubAzure.Json(200, """{"result":"from location"}"""))
            .On(HttpMethod.Get, "/async", StubAzure.Json(200, """{"status":"Succeeded"}"""));

        AzureResponse final = stub.Http().AwaitOperation(
            Accepted(202, "", ("Location", "https://management.azure.com/location"), ("Azure-AsyncOperation", "https://management.azure.com/async")),
            TimeSpan.FromMinutes(1),
            "what-if");

        Assert.Equal("from location", final.Json()!["result"]!.GetValue<string>());
        Assert.Empty(stub.Sent(HttpMethod.Get, "/async"));
    }

    [Fact]
    public void AnAsyncOperationUrlIsPolledUntilItsStatusIsTerminal()
    {
        StubAzure stub = new StubAzure().On(
            HttpMethod.Get,
            "/async",
            StubAzure.Json(200, """{"status":"Running"}"""),
            StubAzure.Json(200, """{"status":"Succeeded"}"""));

        AzureResponse final = stub.Http().AwaitOperation(
            Accepted(201, """{"properties":{"provisioningState":"Accepted"}}""", ("Azure-AsyncOperation", "https://management.azure.com/async")),
            TimeSpan.FromMinutes(1),
            "deployment");

        Assert.Equal(200, final.Status);
        Assert.Equal("Succeeded", final.Json()!["status"]!.GetValue<string>());
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public void AnAcceptedResponseWithNowhereToPollIsAlreadyFinal()
    {
        StubAzure stub = new();

        AzureResponse final = stub.Http().AwaitOperation(Accepted(202, "{}"), TimeSpan.FromMinutes(1), "what-if");

        Assert.Equal(202, final.Status);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void AnOperationThatNeverFinishesThrowsOnceTheLimitIsSpentInSleep()
    {
        // The sleep is a no-op, so real time barely moves: the limit has to be counted in time
        // slept as well, or this loop would poll for the full limit in wall-clock time.
        const string location = "https://management.azure.com/operationResults/stuck";
        StubAzure stub = new StubAzure().On(HttpMethod.Get, "/operationResults/stuck", StubAzure.Empty(202, ("Location", location), ("Retry-After", "30")));

        GraphException ex = Assert.Throws<GraphException>(() => stub.Http().AwaitOperation(
            Accepted(202, "", ("Location", location), ("Retry-After", "30")),
            TimeSpan.FromMinutes(1),
            "what-if"));

        Assert.Contains("had not finished the what-if after 1 minutes", ex.Message);
        Assert.Contains(location, ex.Message);
        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)], stub.Slept);
    }

    [Theory]
    [InlineData("resourceGroups", "rg-Neelam")]
    [InlineData("RESOURCEGROUPS", "rg-Neelam")]
    [InlineData("subscriptions", "abc")]
    [InlineData("storageAccounts", null)]
    public void ASegmentIsFoundCaseInsensitively(string key, string? expected)
    {
        Assert.Equal(expected, AzureHttp.Segment("/subscriptions/abc/resourcegroups/rg-Neelam/providers/Microsoft.Web/sites/app", key));
    }
}

/// <summary>
/// Which subscription an operation runs against, read from a profile written to a temporary
/// directory -- never the machine's own ~/.azure.
/// </summary>
public sealed class AzureSubscriptionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "janet-tests", "azprofile-" + Guid.NewGuid().ToString("n")[..8]);

    public AzureSubscriptionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
    }

    /// <summary>Written the way the Azure CLI writes it: UTF-8 WITH a byte-order mark.</summary>
    private void WriteProfile(bool withDefault = true)
    {
        string json = $$"""
            {"installationId":"x","subscriptions":[
              {"id":"AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA","name":"Neelam Production","isDefault":false,"state":"Enabled"},
              {"id":"BBBBBBBB-2222-4222-8222-BBBBBBBBBBBB","name":"Personal","isDefault":{{(withDefault ? "true" : "false")}},"state":"Enabled"}]}
            """;
        File.WriteAllText(Path.Combine(_directory, "azureProfile.json"), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    [Fact]
    public void TheProfileIsWrittenWithAByteOrderMarkAndStillReads()
    {
        WriteProfile();

        byte[] head = File.ReadAllBytes(Path.Combine(_directory, "azureProfile.json"))[..3];
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, head);

        Assert.Equal("az default", AzureSubscription.Resolve(null, _directory).Source);
    }

    [Fact]
    public void NoSubscriptionMeansTheProfilesDefault()
    {
        WriteProfile();

        AzureSubscription resolved = AzureSubscription.Resolve("  ", _directory);

        Assert.Equal(new AzureSubscription("bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb", "Personal", "az default"), resolved);
    }

    [Fact]
    public void ANameIsMatchedCaseInsensitivelyAndCountsAsExplicit()
    {
        WriteProfile();

        AzureSubscription resolved = AzureSubscription.Resolve("neelam production", _directory);

        Assert.Equal(new AzureSubscription("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa", "Neelam Production", "explicit"), resolved);
    }

    [Fact]
    public void AnIdPassesThroughWithoutReadingAnyProfile()
    {
        // The directory does not exist: an id must never need it.
        AzureSubscription resolved = AzureSubscription.Resolve("CCCCCCCC-3333-4333-8333-CCCCCCCCCCCC", Path.Combine(_directory, "nowhere"));

        Assert.Equal(new AzureSubscription("cccccccc-3333-4333-8333-cccccccccccc", null, "explicit"), resolved);
    }

    [Fact]
    public void AMissingProfileIsRefusedWithTheWayOut()
    {
        GraphException ex = Assert.Throws<GraphException>(() => AzureSubscription.Resolve(null, _directory));

        Assert.Contains("az login", ex.Message);
    }

    [Fact]
    public void AProfileWithNoDefaultIsRefused()
    {
        WriteProfile(withDefault: false);

        GraphException ex = Assert.Throws<GraphException>(() => AzureSubscription.Resolve(null, _directory));

        Assert.Contains("marks no subscription as default", ex.Message);
    }

    [Fact]
    public void AnUnknownNameIsRefusedListingTheKnownOnes()
    {
        WriteProfile();

        GraphException ex = Assert.Throws<GraphException>(() => AzureSubscription.Resolve("Staging", _directory));

        Assert.Contains("Neelam Production, Personal", ex.Message);
    }
}
