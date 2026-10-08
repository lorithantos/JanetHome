using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// What-if, deploy and deployment status, driven through canned ARM answers and a compile
/// function that stands in for the Bicep CLI.
/// </summary>
public class AzureDeploymentsTests
{
    private const string Rg = "rg-neelam";
    private const string FilePath = @"C:\Fixture\main.bicep";

    private static readonly string RgId = $"/subscriptions/{StubAzure.Sub}/resourceGroups/{Rg}";

    private static readonly BicepDiagnostic Warning = new("warning", "no-unused-params", "Parameter \"unused\" is declared but never used.", FilePath, 3, 7);

    /// <summary>A fresh compile every call: the template is re-parented into a request body, and a JsonNode has one parent.</summary>
    private static Func<string, IReadOnlyList<string>, BicepCompiled> Compiles(bool succeeded = true) =>
        (path, _) => succeeded
            ? new BicepCompiled(FilePath, true, [Warning], new JsonObject { ["resources"] = new JsonArray() }, new JsonObject { ["location"] = new JsonObject { ["value"] = "westeurope" } })
            : new BicepCompiled(FilePath, false, [new BicepDiagnostic("error", "BCP057", "The name \"locaton\" does not exist in the current context.", FilePath, 5, 23)], null, []);

    private static DeploymentRequest Request(string name) => new()
    {
        Path = FilePath,
        ResourceGroup = Rg,
        Subscription = StubAzure.Sub,
        Name = name,
    };

    // ---- what-if ---------------------------------------------------------------------------

    private const string OperationUrl = "https://management.azure.com/subscriptions/x/providers/Microsoft.Resources/locations/westeurope/operationResults/wi1?api-version=2024-03-01";

    /// <summary>A recorded-shape what-if answer: a create, a nested modify, and a noChange.</summary>
    /// <remarks>RGID and SUB are replaced rather than interpolated: the JSON's own brace runs would need four dollar signs.</remarks>
    private static string WhatIfAnswer() => """
        {"status":"Succeeded","properties":{"changes":[
          {"resourceId":"RGID/providers/Microsoft.Storage/storageAccounts/stneelam/providers/Microsoft.Authorization/roleAssignments/ra1",
           "changeType":"Create",
           "after":{"name":"ra1","properties":{
             "roleDefinitionId":"/subscriptions/SUB/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe",
             "principalId":"[reference(resourceId('Microsoft.Web/sites', 'app-neelam'), '2023-12-01', 'full').identity.principalId]",
             "principalType":"ServicePrincipal",
             "description":"[[not an expression]"}}},
          {"resourceId":"RGID/providers/Microsoft.Web/sites/app-neelam",
           "changeType":"Modify",
           "delta":[
             {"path":"properties","propertyChangeType":"Modify","children":[
               {"path":"siteConfig","propertyChangeType":"Modify","children":[
                 {"path":"minTlsVersion","propertyChangeType":"Modify","before":"1.0","after":"1.2"}]}]},
             {"path":"properties.ipSecurityRestrictions","propertyChangeType":"Array","children":[
               {"path":"0","propertyChangeType":"Delete","before":{"ipAddress":"203.0.113.7/32","action":"Allow"}}]}]},
          {"resourceId":"RGID/providers/Microsoft.Storage/storageAccounts/stneelam","changeType":"NoChange"}]}}
        """.Replace("RGID", RgId, StringComparison.Ordinal).Replace("SUB", StubAzure.Sub, StringComparison.Ordinal);

    private static StubAzure WhatIfStub() => new StubAzure()
        .On(HttpMethod.Post, "/deployments/wi/whatIf", StubAzure.Empty(202, ("Location", OperationUrl), ("Retry-After", "1")))
        .On(HttpMethod.Get, "/operationResults/wi1", StubAzure.Json(200, WhatIfAnswer()));

    [Fact]
    public void AWhatIfOfAFileThatDoesNotCompileSendsNothing()
    {
        StubAzure stub = new();

        WhatIfResult result = new AzureDeployments(stub.Http(), Compiles(succeeded: false)).WhatIf(Request("wi"));

        Assert.Equal("notCompiled", result.Outcome);
        Assert.Equal("BCP057", Assert.Single(result.Diagnostics).Code);
        Assert.Empty(result.Changes);
        Assert.Null(result.Error);
        Assert.All(result.Summary.Values, count => Assert.Equal(0, count));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void AWhatIfArmRefusesIsRejectedWithItsErrorAsData()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Post, "/whatIf", StubAzure.Json(400, """
            {"error":{"code":"InvalidTemplate","message":"Deployment template validation failed.","target":"/resources/0","details":[]}}
            """));

        WhatIfResult result = new AzureDeployments(stub.Http(), Compiles()).WhatIf(Request("wi"));

        Assert.Equal("rejected", result.Outcome);
        Assert.Equal("InvalidTemplate", result.Error!.Code);
        Assert.Equal("/resources/0", result.Error.Target);
        Assert.Empty(result.Changes);
        Assert.Equal(Warning, Assert.Single(result.Diagnostics));
    }

    [Fact]
    public void AWhatIfPostsAnIncrementalFullPayloadComparison()
    {
        StubAzure stub = WhatIfStub();

        new AzureDeployments(stub.Http(), Compiles()).WhatIf(Request("wi"));

        StubAzure.Recorded post = Assert.Single(stub.Sent(HttpMethod.Post, "/whatIf"));
        Assert.Equal(
            $"https://management.azure.com/subscriptions/{StubAzure.Sub}/resourcegroups/{Rg}/providers/Microsoft.Resources/deployments/wi/whatIf?api-version=2024-03-01",
            post.Url);

        JsonNode body = JsonNode.Parse(post.Body!)!;
        Assert.Equal("Incremental", body["properties"]!["mode"]!.GetValue<string>());
        Assert.Equal("FullResourcePayloads", body["properties"]!["whatIfSettings"]!["resultFormat"]!.GetValue<string>());
        Assert.Equal("westeurope", body["properties"]!["parameters"]!["location"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void AComparedWhatIfCountsEveryResourceButListsOnlyTheChanges()
    {
        WhatIfResult result = new AzureDeployments(WhatIfStub().Http(), Compiles()).WhatIf(Request("wi"));

        Assert.Equal("compared", result.Outcome);
        Assert.Null(result.Error);
        Assert.False(result.IncludesUnchanged);
        Assert.Equal(1, result.Summary["create"]);
        Assert.Equal(1, result.Summary["modify"]);
        Assert.Equal(1, result.Summary["noChange"]);
        Assert.Equal(0, result.Summary["delete"]);
        Assert.Equal(["create", "modify"], result.Changes.Select(c => c.ChangeType));
    }

    [Fact]
    public void IncludeUnchangedListsTheNoChangeResourcesToo()
    {
        WhatIfResult result = new AzureDeployments(WhatIfStub().Http(), Compiles()).WhatIf(Request("wi"), includeUnchanged: true);

        Assert.True(result.IncludesUnchanged);
        Assert.Equal(["create", "modify", "noChange"], result.Changes.Select(c => c.ChangeType));
        Assert.Equal("Microsoft.Storage/storageAccounts/stneelam", result.Changes[2].Resource);
    }

    [Fact]
    public void ACreatedRoleAssignmentWhosePrincipalIsAReferenceIsUnresolved()
    {
        WhatIfResult result = new AzureDeployments(WhatIfStub().Http(), Compiles()).WhatIf(Request("wi"));

        WhatIfChange create = result.Changes[0];
        Assert.True(create.Unresolved);
        Assert.Equal(1, result.Unresolved);
        Assert.Equal("Microsoft.Storage/storageAccounts/stneelam/providers/Microsoft.Authorization/roleAssignments/ra1", create.Resource);
        Assert.Empty(create.Deltas);

        WhatIfField principal = Assert.Single(create.Fields, f => f.Path == "properties.principalId");
        Assert.True(principal.Unresolved);
        Assert.StartsWith("[reference(", principal.Value);

        // "[[" is ARM's escape for a literal bracket, not an expression.
        WhatIfField description = Assert.Single(create.Fields, f => f.Path == "properties.description");
        Assert.False(description.Unresolved);

        Assert.False(Assert.Single(create.Fields, f => f.Path == "properties.principalType").Unresolved);
    }

    [Fact]
    public void AModifyFlattensNestedChildrenAndWritesArrayIndexesAsBrackets()
    {
        WhatIfResult result = new AzureDeployments(WhatIfStub().Http(), Compiles()).WhatIf(Request("wi"));

        WhatIfChange modify = result.Changes[1];
        Assert.False(modify.Unresolved);
        Assert.Empty(modify.Fields);
        Assert.Equal(
            [
                new WhatIfDelta("properties.siteConfig.minTlsVersion", "modify", "1.0", "1.2", false),
                new WhatIfDelta("properties.ipSecurityRestrictions[0]", "delete", """{"ipAddress":"203.0.113.7/32","action":"Allow"}""", null, false),
            ],
            modify.Deltas);
    }

    [Fact]
    public void ACreateListsAtMostTheFieldCapAndSaysItCut()
    {
        JsonObject properties = [];
        for (int i = 0; i < AzureDeployments.FieldCap + 5; i++)
        {
            properties[$"p{i:00}"] = i;
        }

        WhatIfChange change = AzureDeployments.Change(new JsonObject
        {
            ["resourceId"] = $"{RgId}/providers/Microsoft.Web/sites/big",
            ["changeType"] = "Create",
            ["after"] = new JsonObject { ["properties"] = properties },
        });

        Assert.Equal(AzureDeployments.FieldCap, change.Fields.Count);
        Assert.True(change.FieldsTruncated);
        Assert.Equal("properties.p00", change.Fields[0].Path);
        Assert.Equal("0", change.Fields[0].Value);
    }

    [Fact]
    public void ADeleteListsTheFieldsItHadBeforeAndCapsLongValues()
    {
        WhatIfChange change = AzureDeployments.Change(new JsonObject
        {
            ["resourceId"] = "/subscriptions/x/providers/Microsoft.Authorization/roleDefinitions/custom",
            ["changeType"] = "Delete",
            ["before"] = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["policy"] = new string('x', 300),
                    ["scopes"] = new JsonArray("/a", "/b"),
                },
            },
        });

        Assert.Equal("delete", change.ChangeType);
        Assert.False(change.FieldsTruncated);

        // Outside a resource group the full id is the readable one.
        Assert.Equal("/subscriptions/x/providers/Microsoft.Authorization/roleDefinitions/custom", change.Resource);
        Assert.Equal(["properties.policy", "properties.scopes[0]", "properties.scopes[1]"], change.Fields.Select(f => f.Path));
        Assert.Equal(new string('x', AzureDeployments.ValueCap) + "...", change.Fields[0].Value);
    }

    [Fact]
    public void ADefaultNameIsTheStemAndAUtcStampInCharactersArmAllows()
    {
        string name = AzureDeployments.DefaultName(@"C:\infra\main app+test.bicepparam");

        Assert.Matches(new Regex(@"^main-app-test-\d{8}-\d{6}$"), name);
        Assert.True(AzureDeployments.DefaultName(@"C:\" + new string('a', 80) + ".bicep").Length <= 64);
    }

    // ---- deploy and status -----------------------------------------------------------------

    private const string Name = "deploy-test";

    private static readonly string DeploymentUrl = $"/deployments/{Name}?api-version";

    private static Func<HttpResponseMessage> State(string state, string extra = "") =>
        StubAzure.Json(200, "{\"name\":\"" + Name + "\",\"properties\":{\"provisioningState\":\"" + state + "\"" + extra + "}}");

    [Fact]
    public void ADeploymentThatSucceedsInsideTheWaitIsDeployedWithItsOutputs()
    {
        StubAzure stub = new StubAzure()
            .On(HttpMethod.Put, DeploymentUrl, StubAzure.Json(201, """{"properties":{"provisioningState":"Accepted"}}"""))
            .On(HttpMethod.Get, DeploymentUrl,
                State("Running"),
                State("Succeeded", ""","timestamp":"2026-10-08T10:00:41Z","duration":"PT41.2S","correlationId":"c0rr","outputs":{"endpoint":{"type":"String","value":"https://app-neelam.azurewebsites.net"},"count":{"type":"Int","value":3}}"""));

        DeploymentResult result = new AzureDeployments(stub.Http(), Compiles()).Deploy(Request(Name));

        Assert.Equal("complete", result.Status);
        Assert.Equal("deployed", result.Outcome);
        Assert.Equal("Succeeded", result.ProvisioningState);
        Assert.Equal("PT41.2S", result.Duration);
        Assert.Equal("c0rr", result.CorrelationId);
        Assert.Equal(FilePath, result.Path);
        Assert.Equal("https://app-neelam.azurewebsites.net", result.Outputs["endpoint"]!.GetValue<string>());
        Assert.Equal(3, result.Outputs["count"]!.GetValue<int>());
        Assert.Null(result.Error);
        Assert.Empty(result.FailedOperations);
        Assert.Equal(Warning, Assert.Single(result.Diagnostics));

        JsonNode body = JsonNode.Parse(Assert.Single(stub.Sent(HttpMethod.Put, DeploymentUrl)).Body!)!;
        Assert.Equal("Incremental", body["properties"]!["mode"]!.GetValue<string>());
    }

    [Fact]
    public void ADeploymentStillRunningWhenTheWaitEndsComesBackRunning()
    {
        StubAzure stub = new StubAzure()
            .On(HttpMethod.Put, DeploymentUrl, StubAzure.Json(201, """{"properties":{"provisioningState":"Accepted"}}"""))
            .On(HttpMethod.Get, DeploymentUrl, State("Running"));

        DeploymentResult result = new AzureDeployments(stub.Http(), Compiles()).Deploy(Request(Name), TimeSpan.FromSeconds(15));

        Assert.Equal("running", result.Status);
        Assert.Equal("running", result.Outcome);
        Assert.Equal("Running", result.ProvisioningState);
        Assert.Empty(result.Outputs);

        // Polled every five seconds, slept rather than waited.
        Assert.Equal(3, stub.Sent(HttpMethod.Get, DeploymentUrl).Count());
        Assert.Equal(TimeSpan.FromSeconds(15), stub.Slept.Aggregate(TimeSpan.Zero, (sum, t) => sum + t));
    }

    [Fact]
    public void AFailedDeploymentReadsItsFailedOperationsAcrossPages()
    {
        StubAzure stub = new StubAzure()
            .On(HttpMethod.Put, DeploymentUrl, StubAzure.Json(201, """{"properties":{"provisioningState":"Accepted"}}"""))
            .On(HttpMethod.Get, $"/deployments/{Name}/operations", StubAzure.Json(200, """
                {"value":[
                  {"properties":{"provisioningState":"Succeeded","targetResource":{"resourceType":"Microsoft.Web/serverfarms","resourceName":"plan"}}},
                  {"properties":{"provisioningState":"Failed","targetResource":{"resourceType":"Microsoft.Storage/storageAccounts","resourceName":"stneelam"},
                    "statusMessage":{"error":{"code":"StorageAccountAlreadyTaken","message":"The storage account named stneelam is already taken."}}}}],
                 "nextLink":"https://management.azure.com/ops-page-2"}
                """))
            .On(HttpMethod.Get, "/ops-page-2", StubAzure.Json(200, """
                {"value":[
                  {"properties":{"provisioningState":"Failed","statusCode":"BadRequest","statusMessage":{"status":"Failed","message":"plain text reason"}}},
                  {"properties":{"provisioningState":"Failed","statusCode":"Conflict","statusMessage":"a bare string reason"}}]}
                """))
            .On(HttpMethod.Get, DeploymentUrl, State("Failed", ""","error":{"code":"DeploymentFailed","message":"At least one resource deployment operation failed.","details":[{"code":"Conflict","message":"see operations"}]}"""));

        DeploymentResult result = new AzureDeployments(stub.Http(), Compiles()).Deploy(Request(Name));

        Assert.Equal("complete", result.Status);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("DeploymentFailed", result.Error!.Code);
        Assert.Equal("Conflict", Assert.Single(result.Error.Details).Code);

        Assert.Equal(3, result.FailedOperations.Count);
        Assert.Equal(
            new DeploymentFailure("Microsoft.Storage/storageAccounts", "stneelam", result.FailedOperations[0].Error),
            result.FailedOperations[0]);
        Assert.Equal("StorageAccountAlreadyTaken", result.FailedOperations[0].Error.Code);

        // No error object: the status code and the message are what ARM gave.
        DeploymentFailure plain = result.FailedOperations[1];
        Assert.Null(plain.ResourceType);
        Assert.Equal("BadRequest", plain.Error.Code);
        Assert.Equal("plain text reason", plain.Error.Message);

        // A statusMessage that is a bare JSON string. Indexing it as an object used to throw, so
        // a failed deployment came back as an exception instead of outcome "failed".
        DeploymentFailure bare = result.FailedOperations[2];
        Assert.Equal("Conflict", bare.Error.Code);
        Assert.Equal("a bare string reason", bare.Error.Message);
    }

    [Fact]
    public void ADeploymentArmRefusesIsRejectedBeforeItStarted()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Put, DeploymentUrl, StubAzure.Json(400, """
            {"error":{"code":"InvalidTemplateDeployment","message":"The template deployment failed because of policy violation.",
              "details":[{"code":"RequestDisallowedByPolicy","message":"Resource 'stneelam' was disallowed by policy.","target":"stneelam"}]}}
            """));

        DeploymentResult result = new AzureDeployments(stub.Http(), Compiles()).Deploy(Request(Name));

        Assert.Equal("complete", result.Status);
        Assert.Equal("rejected", result.Outcome);
        Assert.Null(result.ProvisioningState);
        Assert.Null(result.CorrelationId);
        Assert.Equal("RequestDisallowedByPolicy", Assert.Single(result.Error!.Details).Code);
        Assert.Empty(stub.Sent(HttpMethod.Get, DeploymentUrl));
    }

    [Fact]
    public void ADeploymentOfAFileThatDoesNotCompileSendsNothing()
    {
        StubAzure stub = new();

        DeploymentResult result = new AzureDeployments(stub.Http(), Compiles(succeeded: false)).Deploy(Request(Name));

        Assert.Equal("complete", result.Status);
        Assert.Equal("notCompiled", result.Outcome);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void AStatusReadOfAMissingDeploymentIsRefused()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Get, "/deployments/gone?", StubAzure.ArmError(404, "DeploymentNotFound", "not found"));

        GraphException ex = Assert.Throws<GraphException>(() => new AzureDeployments(stub.Http(), Compiles()).Status(Rg, "gone", StubAzure.Sub));

        Assert.Contains("No deployment named 'gone'", ex.Message);
    }

    [Fact]
    public void AStatusReadHasNoPathAndNoDiagnostics()
    {
        StubAzure stub = new StubAzure().On(HttpMethod.Get, DeploymentUrl, State("Canceled"));

        DeploymentResult result = new AzureDeployments(stub.Http(), Compiles()).Status(Rg, Name, StubAzure.Sub);

        Assert.Equal("complete", result.Status);
        Assert.Equal("canceled", result.Outcome);
        Assert.Null(result.Path);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new AzureSubscription(StubAzure.Sub, null, "explicit"), result.Subscription);
    }
}
