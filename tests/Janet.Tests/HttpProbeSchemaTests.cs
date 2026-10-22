using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The http_probe envelope held to contracts\http-probe.schema.json, from the code's own output.
/// </summary>
/// <remarks>
/// The gate's live samples reach whatever the sampled endpoint happens to answer. These build each
/// arm through the probe itself (a stub endpoint, never the network), serialize it the way both
/// front ends do, and walk it beside the schema at every depth -- then require that the walk
/// reached every object the schema declares, so an object no sample populated fails here rather
/// than passing unchecked.
/// </remarks>
public class HttpProbeSchemaTests
{
    private const string File = "http-probe.schema.json";

    /// <summary>One emitted envelope and the arm it stands for.</summary>
    public sealed record Arm(string Name, JsonObject Emitted);

    private static JsonObject Emit(StubHttp stub, HttpProbeRequest request) =>
        JsonNode.Parse(HttpProbeJson.Serialize(new HttpProbe(stub, stub.Slept.Add).Probe(request)))!.AsObject();

    private static IEnumerable<Arm> EveryArm()
    {
        const string url = "https://site.example.invalid/healthz";

        // Warmed up after a refusal and a 503: attempts of every kind but 'unexpected', headers
        // present, a capped body.
        yield return new Arm("warmed", HttpProbeSchemaTests.Emit(
            new StubHttp(StubHttp.Refused(), StubHttp.Status(503), StubHttp.Status(200, new string('x', 64), "X-Build: 7")),
            new HttpProbeRequest { Url = url, Retries = 3, DelayMs = 0, BodyCap = 16, ExpectStatus = 200 }));

        yield return new Arm("unexpected", HttpProbeSchemaTests.Emit(
            new StubHttp(StubHttp.Status(500, "boom")),
            new HttpProbeRequest { Url = url }));

        yield return new Arm("transport failure", HttpProbeSchemaTests.Emit(
            new StubHttp(StubHttp.Refused()),
            new HttpProbeRequest { Url = url }));

        yield return new Arm("refused", HttpProbeSchemaTests.Emit(
            new StubHttp(StubHttp.Status(200)),
            new HttpProbeRequest { Url = "file:///C:/x" }));
    }

    [Fact]
    public void EveryObjectTheSchemaDeclaresIsReachedAndMatchedByTheEmittedEnvelopes()
    {
        JsonObject root = ContractSchema.Load(HttpProbeSchemaTests.File);
        HashSet<JsonObject> reached = new(ReferenceEqualityComparer.Instance);

        foreach (Arm arm in HttpProbeSchemaTests.EveryArm())
        {
            ContractSchema.AssertEmittedMatches(root, root, arm.Emitted, arm.Name, reached);
        }

        string[] unreached = ContractSchema.ObjectSchemas(root, "#")
            .Where(found => !reached.Contains(found.Schema))
            .Select(found => found.Path)
            .ToArray();

        Assert.True(unreached.Length == 0, $"no emitted object reached: {string.Join(", ", unreached)}");
    }

    [Fact]
    public void EveryObjectTheSchemaDeclaresIsClosedAndRequiresEveryProperty() =>
        ContractSchema.AssertEveryObjectIsClosedAndRequiresEveryProperty(HttpProbeSchemaTests.File);

    [Fact]
    public void EveryPropertyTheSchemaDeclaresIsDescribed()
    {
        foreach ((string path, JsonObject schema) in ContractSchema.ObjectSchemas(ContractSchema.Load(HttpProbeSchemaTests.File), "#"))
        {
            foreach ((string name, JsonNode? property) in schema["properties"]!.AsObject())
            {
                Assert.False(string.IsNullOrWhiteSpace(property!["description"]?.GetValue<string>()), $"{path}/{name} has no description");
            }
        }
    }

    [Fact]
    public void TheOutcomeEnumIsExactlyTheOutcomesTheCodeCanEmit()
    {
        JsonObject root = ContractSchema.Load(HttpProbeSchemaTests.File);
        IEnumerable<string> declared = root["properties"]!["attempts"]!["items"]!["properties"]!["outcome"]!["enum"]!
            .AsArray()
            .Select(value => value!.GetValue<string>());

        Assert.Equal(HttpProbe.Outcomes, declared);
    }

    [Fact]
    public void TheSchemaStampsTheContractTheCodeEmits()
    {
        JsonObject root = ContractSchema.Load(HttpProbeSchemaTests.File);

        Assert.Equal(HttpProbeResult.ContractVersion, root["$janet"]!["contract"]!.GetValue<int>());
        Assert.Equal(HttpProbeResult.ContractVersion, root["properties"]!["contract"]!["const"]!.GetValue<int>());
        Assert.Equal(HttpProbeResult.ContractVersion, HttpProbeSchemaTests.EveryArm().First().Emitted["contract"]!.GetValue<int>());
    }
}
