using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The five Bicep and Azure envelopes, each held to the schema that declares it.
/// </summary>
/// <remarks>
/// Same argument as DotnetCheckSchemaTests, and stronger here: a live what-if needs a sign-in and
/// a resource group, a live deployment changes one, and one live sample reaches one outcome. So
/// these build every envelope fully populated -- every list non-empty, every nullable object
/// present -- serialize it, and compare key sets EXACTLY against the schema at every level,
/// nested items included. A field in one and not the other is a defect whichever way it points.
/// <para>
/// Where the code computes a key set itself (the what-if and probe summaries), the sample comes
/// from the code rather than being typed out here, so the test cannot agree with the schema by
/// construction.
/// </para>
/// </remarks>
public class AzureSchemaTests
{
    public static TheoryData<string> Schemas =>
    [
        "bicep-check.schema.json",
        "azure-whatif.schema.json",
        "azure-deployment.schema.json",
        "azure-roles.schema.json",
        "storage-probe.schema.json",
    ];

    private static JsonObject Load(string file) =>
        JsonNode.Parse(File.ReadAllText(Fixture.Resolve("Contracts", file)))!.AsObject();

    /// <summary>
    /// The object schema a node stands for: through a local $ref, or the $ref arm of a nullable
    /// oneOf. Anything else is returned as it is.
    /// </summary>
    private static JsonObject Resolve(JsonObject root, JsonNode node)
    {
        JsonObject schema = node.AsObject();

        if (schema["$ref"]?.GetValue<string>() is string reference)
        {
            Assert.StartsWith("#/definitions/", reference);
            return root["definitions"]![reference["#/definitions/".Length..]]!.AsObject();
        }

        if (schema["oneOf"] is JsonArray arms)
        {
            return Resolve(root, arms.Single(arm => arm!["type"]?.GetValue<string>() != "null")!);
        }

        return schema;
    }

    private static JsonObject Property(JsonObject root, JsonObject schema, string name) =>
        Resolve(root, schema["properties"]![name]!);

    private static JsonObject Items(JsonObject root, JsonObject schema, string name) =>
        Resolve(root, Property(root, schema, name)["items"]!);

    private static IEnumerable<string> Declared(JsonObject schema) =>
        schema["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);

    private static IEnumerable<string> Keys(JsonNode emitted) =>
        emitted.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);

    /// <summary>Emitted keys equal declared keys, and declared keys are all required.</summary>
    private static void Matches(JsonObject schema, JsonNode emitted, string where)
    {
        Assert.True(Declared(schema).SequenceEqual(Keys(emitted)),
            $"{where}: declared [{string.Join(", ", Declared(schema))}] but emitted [{string.Join(", ", Keys(emitted))}]");
        Assert.Equal(Declared(schema), schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    private static readonly AzureSubscription Subscription = new(StubAzure.Sub, "Neelam Production", "explicit");

    private static readonly BicepDiagnostic Diagnostic = new("error", "BCP057", "The name does not exist.", @"C:\Fixture\broken.bicep", 5, 23);

    private static readonly AzureError Error = new("DeploymentFailed", "wrapper", "stneelam",
        [new AzureError("StorageAccountAlreadyTaken", "taken", "stneelam", [])]);

    /// <summary>The checks shared by the three envelopes that carry them.</summary>
    private static void SharedShapes(JsonObject root, JsonObject schema, JsonObject emitted, string envelope)
    {
        if (schema["properties"]!["subscription"] is not null)
        {
            Matches(Property(root, schema, "subscription"), emitted["subscription"]!, $"{envelope}.subscription");
        }

        if (schema["properties"]!["diagnostics"] is not null)
        {
            Matches(Items(root, schema, "diagnostics"), emitted["diagnostics"]![0]!, $"{envelope}.diagnostics[]");
        }

        if (schema["properties"]!["error"] is not null)
        {
            JsonObject error = Property(root, schema, "error");
            Matches(error, emitted["error"]!, $"{envelope}.error");
            Matches(Items(root, error, "details"), emitted["error"]!["details"]![0]!, $"{envelope}.error.details[]");
        }
    }

    // ---- bicep check -----------------------------------------------------------------------

    [Fact]
    public void TheBicepCheckEnvelopeEmitsExactlyWhatItsSchemaDeclares()
    {
        JsonObject root = Load("bicep-check.schema.json");
        JsonObject emitted = JsonNode.Parse(BicepJson.Serialize(new BicepCheckResult
        {
            Path = @"C:\Fixture\broken.bicep",
            Kind = "template",
            Succeeded = false,
            Errors = 1,
            Warnings = 0,
            Notes = 0,
            Diagnostics = [Diagnostic],
            BicepVersion = "Bicep CLI version 0.47.16 (3f73e1a234)",
            DurationSeconds = 1.25,
        }))!.AsObject();

        Matches(root, emitted, "bicep-check");
        SharedShapes(root, root, emitted, "bicep-check");
    }

    // ---- what-if ---------------------------------------------------------------------------

    /// <summary>
    /// A real summary from the code (the notCompiled path makes no HTTP call), then every list
    /// filled from AzureDeployments.Change over raw ARM changes, and an error.
    /// </summary>
    private static JsonObject EmittedWhatIf()
    {
        WhatIfResult result = new AzureDeployments(new StubAzure().Http(), (path, _) => new BicepCompiled(path, false, [Diagnostic], null, []))
            .WhatIf(new DeploymentRequest { Path = @"C:\Fixture\main.bicep", ResourceGroup = "rg", Subscription = StubAzure.Sub, Name = "n" });

        WhatIfChange create = AzureDeployments.Change(JsonNode.Parse("""
            {"resourceId":"/subscriptions/s/resourceGroups/rg/providers/Microsoft.Web/sites/app","changeType":"Create",
             "after":{"properties":{"httpsOnly":true}}}
            """)!.AsObject());
        WhatIfChange modify = AzureDeployments.Change(JsonNode.Parse("""
            {"resourceId":"/subscriptions/s/resourceGroups/rg/providers/Microsoft.Web/sites/app","changeType":"Modify",
             "delta":[{"path":"properties.httpsOnly","propertyChangeType":"Modify","before":false,"after":true}]}
            """)!.AsObject());

        return JsonNode.Parse(AzureJson.Serialize(result with
        {
            Changes = [create with { Deltas = modify.Deltas, UnsupportedReason = "reason" }],
            Error = Error,
        }))!.AsObject();
    }

    [Fact]
    public void TheWhatIfEnvelopeEmitsExactlyWhatItsSchemaDeclares()
    {
        JsonObject root = Load("azure-whatif.schema.json");
        JsonObject emitted = EmittedWhatIf();

        Matches(root, emitted, "whatif");
        SharedShapes(root, root, emitted, "whatif");

        JsonObject change = Items(root, root, "changes");
        Matches(change, emitted["changes"]![0]!, "whatif.changes[]");
        Matches(Items(root, change, "deltas"), emitted["changes"]![0]!["deltas"]![0]!, "whatif.changes[].deltas[]");
        Matches(Items(root, change, "fields"), emitted["changes"]![0]!["fields"]![0]!, "whatif.changes[].fields[]");
    }

    [Fact]
    public void TheWhatIfSummaryDeclaresEveryChangeTypeTheCodeCounts()
    {
        JsonObject root = Load("azure-whatif.schema.json");

        Matches(Property(root, root, "summary"), EmittedWhatIf()["summary"]!, "whatif.summary");

        // The change-type enum on each change is the same vocabulary as the summary's keys.
        Assert.Equal(
            Declared(Property(root, root, "summary")),
            Items(root, root, "changes")["properties"]!["changeType"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    // ---- deployment ------------------------------------------------------------------------

    [Fact]
    public void TheDeploymentEnvelopeEmitsExactlyWhatItsSchemaDeclares()
    {
        JsonObject root = Load("azure-deployment.schema.json");
        JsonObject emitted = JsonNode.Parse(AzureJson.Serialize(new DeploymentResult
        {
            Status = "complete",
            Outcome = "failed",
            Path = @"C:\Fixture\main.bicep",
            Subscription = Subscription,
            ResourceGroup = "rg-neelam",
            Name = "main-20261008-100000",
            ProvisioningState = "Failed",
            Timestamp = "2026-10-08T10:00:41Z",
            Duration = "PT41.2S",
            CorrelationId = "c0rr",
            Diagnostics = [Diagnostic],
            Outputs = new JsonObject { ["endpoint"] = "https://app.example.invalid", ["settings"] = new JsonObject { ["a"] = 1 } },
            Error = Error,
            FailedOperations = [new DeploymentFailure("Microsoft.Storage/storageAccounts", "stneelam", Error)],
        }))!.AsObject();

        Matches(root, emitted, "deployment");
        SharedShapes(root, root, emitted, "deployment");

        JsonObject failure = Items(root, root, "failedOperations");
        Matches(failure, emitted["failedOperations"]![0]!, "deployment.failedOperations[]");
        Matches(Property(root, failure, "error"), emitted["failedOperations"]![0]!["error"]!, "deployment.failedOperations[].error");

        // Outputs carry the template's own names, so it is the one object left open.
        Assert.True(Property(root, root, "outputs")["additionalProperties"]!.GetValue<bool>());
    }

    // ---- roles -----------------------------------------------------------------------------

    [Fact]
    public void TheRolesEnvelopeEmitsExactlyWhatItsSchemaDeclares()
    {
        JsonObject root = Load("azure-roles.schema.json");
        JsonObject emitted = JsonNode.Parse(AzureJson.Serialize(new RoleAssignmentsResult
        {
            Operation = "assign",
            Subscription = Subscription with { Source = "scope", Name = null },
            Scope = "/subscriptions/s/resourceGroups/rg",
            Assignee = "33333333-3333-4333-8333-333333333333",
            Outcome = "created",
            PrincipalNames = "resolved",
            Assignments =
            [
                new RoleAssignmentRow("/subscriptions/s/resourceGroups/rg/providers/Microsoft.Authorization/roleAssignments/a", "/subscriptions/s/resourceGroups/rg",
                    "at", "Storage Blob Data Reader", "BuiltInRole", "/subscriptions/s/providers/Microsoft.Authorization/roleDefinitions/r",
                    "33333333-3333-4333-8333-333333333333", "ServicePrincipal", "app-neelam", "why", "2026-10-01T09:00:00Z", "creator"),
            ],
        }))!.AsObject();

        Matches(root, emitted, "roles");
        SharedShapes(root, root, emitted, "roles");
        Matches(Items(root, root, "assignments"), emitted["assignments"]![0]!, "roles.assignments[]");
        Assert.Equal(1, emitted["count"]!.GetValue<int>());
    }

    // ---- storage probe ---------------------------------------------------------------------

    /// <summary>A real probe of one container, so the summary keys and settings come from the code.</summary>
    private static JsonObject EmittedProbe()
    {
        StubAzure stub = new StubAzure()
            .On(HttpMethod.Get, "/storageAccounts/acct?", StubAzure.Json(200,
                """{"id":"/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct","name":"acct","properties":{}}"""))
            .On(HttpMethod.Get, "acct.blob.core.windows.net/c?", StubAzure.Xml(403, "<Error><Code>AuthorizationFailure</Code><Message>no</Message></Error>"))
            .On(HttpMethod.Get, "/tableServices/default/tables?", StubAzure.Json(200, """{"value":[]}"""));

        StorageProbeResult result = new StorageProbe(stub.Http()).Probe(new StorageProbeRequest
        {
            Account = "acct",
            ResourceGroup = "rg",
            Subscription = StubAzure.Sub,
            Containers = ["c"],
        });

        return JsonNode.Parse(AzureJson.Serialize(result))!.AsObject();
    }

    [Fact]
    public void TheStorageProbeEnvelopeEmitsExactlyWhatItsSchemaDeclares()
    {
        JsonObject root = Load("storage-probe.schema.json");
        JsonObject emitted = EmittedProbe();

        Matches(root, emitted, "probe");
        SharedShapes(root, root, emitted, "probe");
        Matches(Property(root, root, "caller"), emitted["caller"]!, "probe.caller");
        Matches(Property(root, root, "settings"), emitted["settings"]!, "probe.settings");
        Matches(Property(root, root, "summary"), emitted["summary"]!, "probe.summary");
        Matches(Items(root, root, "targets"), emitted["targets"]![0]!, "probe.targets[]");
    }

    // ---- every schema ----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Schemas))]
    public void EveryObjectTheSchemaDeclaresIsClosedAndRequiresEveryProperty(string file)
    {
        foreach ((string path, JsonObject schema) in ObjectSchemas(Load(file), "#"))
        {
            Assert.True(schema["additionalProperties"]?.GetValue<bool>() == false, $"{file} {path}: additionalProperties is not false");
            Assert.True(Declared(schema).SequenceEqual(schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order(StringComparer.Ordinal)),
                $"{file} {path}: required is not every declared property");
        }
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    public void EveryPropertyTheSchemaDeclaresIsDescribed(string file)
    {
        foreach ((string path, JsonObject schema) in ObjectSchemas(Load(file), "#"))
        {
            foreach ((string name, JsonNode? property) in schema["properties"]!.AsObject())
            {
                Assert.False(string.IsNullOrWhiteSpace(property!["description"]?.GetValue<string>()), $"{file} {path}/{name} has no description");
            }
        }
    }

    [Theory]
    [InlineData("bicep-check.schema.json", BicepCheckResult.ContractVersion)]
    [InlineData("azure-whatif.schema.json", WhatIfResult.ContractVersion)]
    [InlineData("azure-deployment.schema.json", DeploymentResult.ContractVersion)]
    [InlineData("azure-roles.schema.json", RoleAssignmentsResult.ContractVersion)]
    [InlineData("storage-probe.schema.json", StorageProbeResult.ContractVersion)]
    public void TheSchemaStampsTheContractTheCodeEmits(string file, int contract)
    {
        JsonObject root = Load(file);

        Assert.Equal(contract, root["$janet"]!["contract"]!.GetValue<int>());
        Assert.Equal(contract, root["properties"]!["contract"]!["const"]!.GetValue<int>());
    }

    /// <summary>Every node in the document that declares properties, with a readable path to it.</summary>
    private static IEnumerable<(string Path, JsonObject Schema)> ObjectSchemas(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject)
                {
                    yield return (path, obj);
                }

                foreach ((string key, JsonNode? child) in obj)
                {
                    foreach ((string Path, JsonObject Schema) found in ObjectSchemas(child, $"{path}/{key}"))
                    {
                        yield return found;
                    }
                }

                break;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    foreach ((string Path, JsonObject Schema) found in ObjectSchemas(array[i], $"{path}/{i}"))
                    {
                        yield return found;
                    }
                }

                break;
        }
    }
}
