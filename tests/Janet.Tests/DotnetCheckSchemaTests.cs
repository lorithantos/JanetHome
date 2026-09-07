using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The declared envelope and the emitted one, held to each other.
/// </summary>
/// <remarks>
/// contracts\dotnet-check.schema.json is the published shape, and scripts\Test-OutputContracts.ps1
/// validates a real sample against it on every commit -- but that sampler runs
/// `janet check --no-tests`, so `tests` is null in the sample and the entire tests section has
/// always been declared without ever being checked. A field added to the serializer and not the
/// schema, or the reverse, passed the gate silently.
/// <para>
/// So this pins the two together from the side the gate cannot see. It is deliberately an
/// EXACT set comparison rather than a subset one: the schema says additionalProperties false
/// and requires every key, so a field present in one and absent from the other is a defect
/// whichever direction it points.
/// </para>
/// </remarks>
public class DotnetCheckSchemaTests
{
    private static JsonObject Schema() =>
        JsonNode.Parse(File.ReadAllText(
            Fixture.Resolve("Contracts", "dotnet-check.schema.json")))!.AsObject();

    /// <summary>The complete arm's declaration of one property path, as (properties, required).</summary>
    private static JsonObject TestsSchema() =>
        Schema()["oneOf"]![0]!["properties"]!["tests"]!.AsObject();

    private static IEnumerable<string> Declared(JsonObject node) =>
        node["properties"]!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal);

    private static IEnumerable<string> Required(JsonObject node) =>
        node["required"]!.AsArray().Select(value => value!.GetValue<string>()).Order(StringComparer.Ordinal);

    /// <summary>A run with every field populated, so nothing is omitted for being empty.</summary>
    private static JsonObject Emitted()
    {
        TestRun run = new(
            false,
            3,
            1,
            1,
            1,
            [new TestFailure("Sample.Tests.T.Fails", "boom", ["at Sample.Tests.T.Fails()"])],
            [new TestAssembly("Sample.Tests", 3, 1, 1, 1, "complete")
            {
                DurationSeconds = 2.5,
                TestTimeSeconds = 6.25,
                ResultsFile = @"D:\Temp\janet-trx\20260907-093249-abcd\Sample.Tests.trx",
            }])
        {
            RunnerExitCode = 1,
            Abort = null,
            DurationSeconds = 2.5,
            TestTimeSeconds = 6.25,
            Slowest = [new TestClassTime("Sample.Tests.T", 6.25, 3)],
            ResultsDirectory = @"D:\Temp\janet-trx\20260907-093249-abcd",
        };

        CheckResult result = new(
            @"D:\Repos\Sample\App.slnx",
            "Debug",
            false,
            new BuildReport(true, 1.2, [], [], 0, null, null, null),
            run,
            null);

        return JsonNode.Parse(DotnetCheckJson.Serialize(result))!.AsObject()["tests"]!.AsObject();
    }

    [Fact]
    public void TheTestsSectionEmitsExactlyWhatTheSchemaDeclares()
    {
        Assert.Equal(
            Declared(TestsSchema()),
            Emitted().Select(property => property.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheTestsSectionRequiresEverythingItDeclares()
    {
        // Every field in this envelope is present-and-null rather than absent, because a
        // reader has to tell "not applicable" from "this build does not report it".
        Assert.Equal(Declared(TestsSchema()), Required(TestsSchema()));
    }

    [Fact]
    public void EachAssemblyEmitsExactlyWhatTheSchemaDeclares()
    {
        JsonObject declared = TestsSchema()["properties"]!["assemblies"]!["items"]!.AsObject();

        Assert.Equal(Declared(declared), Required(declared));
        Assert.Equal(
            Declared(declared),
            Emitted()["assemblies"]![0]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EachSlowestRowEmitsExactlyWhatTheSchemaDeclares()
    {
        JsonObject declared = TestsSchema()["properties"]!["slowest"]!["items"]!.AsObject();

        Assert.Equal(Declared(declared), Required(declared));
        Assert.Equal(
            Declared(declared),
            Emitted()["slowest"]![0]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheDeclaredStatusesAreTheOnesTheReaderCanProduce()
    {
        JsonArray statuses = TestsSchema()["properties"]!["assemblies"]!["items"]!
            ["properties"]!["status"]!["enum"]!.AsArray();

        Assert.Equal(
            ["aborted", "complete", "empty"],
            statuses.Select(value => value!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheSchemaStampsTheContractTheCodeEmits()
    {
        JsonObject schema = Schema();

        Assert.Equal(DotnetDiagnostics.Contract, schema["$janet"]!["contract"]!.GetValue<int>());
        Assert.Equal(
            DotnetDiagnostics.Contract,
            schema["oneOf"]![0]!["properties"]!["contract"]!["const"]!.GetValue<int>());
        Assert.Equal(
            DotnetDiagnostics.Contract,
            schema["oneOf"]![1]!["properties"]!["contract"]!["const"]!.GetValue<int>());
    }
}
