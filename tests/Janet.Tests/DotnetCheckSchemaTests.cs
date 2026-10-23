using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The declared envelope and the emitted one, held to each other.
/// </summary>
/// <remarks>
/// contracts\dotnet-check.schema.json is the published shape, and scripts\Test-OutputContracts.ps1
/// validates a real sample against it on every commit -- but until 2026-10-23 that sampler ran
/// only `janet check --no-tests`, so `tests` was null in the sample and the entire tests section
/// was declared without ever being checked there. A field added to the serializer and not the
/// schema, or the reverse, passed the gate silently. The gate now also samples one named test,
/// which validates one populated shape; the exact set comparisons here still cover every field.
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
            new BuildReport(true, 1.2, [], [], 0, null, null, null, "MSB4126: ..."),
            run,
            null);

        return JsonNode.Parse(DotnetCheckJson.Serialize(result))!.AsObject()["tests"]!.AsObject();
    }

    /// <summary>The same run's build section.</summary>
    /// <remarks>
    /// The build section was declared and never held to the serializer until contract 8 added
    /// a field to it: the gate that validates a live sample runs `janet check --no-tests`, so
    /// build IS sampled there -- but the sampler needs a working repo build and a schema edit
    /// alone passed this suite. The tests section has had this since contract 7 and the build
    /// section had nothing, which is the asymmetry rather than a decision.
    /// </remarks>
    private static JsonObject EmittedBuild()
    {
        CheckResult result = new(
            @"D:\Repos\Sample\App.slnx",
            "Debug",
            false,
            new BuildReport(
                false,
                0.3,
                [new Diagnostic("App.slnx", null, "error", "MSB4126", "invalid")],
                [],
                0,
                null,
                null,
                null,
                "MSB4126: App.slnx does not declare ..."),
            null,
            null);

        return JsonNode.Parse(DotnetCheckJson.Serialize(result))!.AsObject()["build"]!.AsObject();
    }

    private static JsonObject BuildSchema() =>
        Schema()["oneOf"]![0]!["properties"]!["build"]!.AsObject();

    [Fact]
    public void TheBuildSectionEmitsExactlyWhatTheSchemaDeclares()
    {
        Assert.Equal(
            Declared(BuildSchema()),
            EmittedBuild().Select(property => property.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheBuildSectionRequiresEverythingItDeclares()
    {
        Assert.Equal(Declared(BuildSchema()), Required(BuildSchema()));
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

    // ---- the whole document ----------------------------------------------------------------

    [Fact]
    public void EveryObjectTheSchemaDeclaresIsClosedAndRequiresEveryProperty() =>
        ContractSchema.AssertEveryObjectIsClosedAndRequiresEveryProperty("dotnet-check.schema.json");

    /// <summary>
    /// Both arms of the union, every nullable object present and every list non-empty, so no
    /// object the schema declares is left unreached for want of data.
    /// </summary>
    private static IEnumerable<(string Arm, JsonObject Emitted)> FullyPopulated()
    {
        Diagnostic warning = new("src/App/Program.cs", 12, "warning", "CS8618", "Non-nullable property");

        CheckResult complete = new(
            @"D:\Repos\Sample\App.slnx",
            "Debug",
            false,
            new BuildReport(
                true,
                4.2,
                [new Diagnostic("src/App/Broken.cs", null, "error", "CS0103", "The name does not exist")],
                [new WarningGroup("CS8618", 3, [warning], 2)],
                3,
                [warning],
                1,
                new BaselineReport(@"D:\Repos\Sample\.janet\baseline.json", "2026-10-20T09:00:00Z", true),
                "MSB4126: the tool passed no platform."),
            new TestRun(
                false,
                3,
                1,
                1,
                1,
                [new TestFailure("Sample.Tests.T.Fails", "boom", ["at Sample.Tests.T.Fails()"])],
                [new TestAssembly("Sample.Tests", 3, 1, 1, 1, "complete") { ResultsFile = @"D:\Temp\Sample.Tests.trx" }])
            {
                RunnerExitCode = 1,
                Abort = "The active test run was aborted.",
                Slowest = [new TestClassTime("Sample.Tests.T", 6.25, 3)],
                ResultsDirectory = @"D:\Temp\janet-trx\run",
            },
            new GraphState
            {
                Via = "razorgraph",
                GraphId = "sample",
                Path = @"D:\Repos\Sample\App.slnx",
                BuiltAt = "2026-10-21T09:00:00Z",
                NewestSourceAt = "2026-10-21T08:00:00Z",
                Status = "current",
                CanRefresh = true,
            });

        yield return ("complete", JsonNode.Parse(DotnetCheckJson.Serialize(complete))!.AsObject());

        CheckPending running = new(@"D:\Repos\Sample\App.slnx", "Debug", "h-1234", "2026-10-21T09:00:00Z", 12.5);

        yield return ("running", JsonNode.Parse(DotnetCheckJson.Serialize(running))!.AsObject());
    }

    [Fact]
    public void EveryObjectTheSchemaDeclaresIsReachedAndMatchedByAFullyPopulatedEnvelope()
    {
        // The section tests above pin build, tests, assemblies[] and slowest[] by name; the root
        // keys, graph, failures[], warnings[], instances[], baseline, the diagnostic definition
        // and the running arm had nothing. This walks the whole emitted envelope instead, and then
        // requires that the walk reached every object the schema declares, so a section the
        // sample leaves empty fails here rather than passing unchecked.
        JsonObject root = Schema();
        HashSet<JsonObject> reached = new(ReferenceEqualityComparer.Instance);

        foreach ((string arm, JsonObject emitted) in FullyPopulated())
        {
            ContractSchema.AssertEmittedMatches(root, root, emitted, arm, reached);
        }

        string[] unreached = ContractSchema.ObjectSchemas(root, "#")
            .Where(found => !reached.Contains(found.Schema))
            .Select(found => found.Path)
            .ToArray();

        Assert.True(unreached.Length == 0, $"no emitted object reached: {string.Join(", ", unreached)}");
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
