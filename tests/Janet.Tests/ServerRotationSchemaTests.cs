using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>The rotation envelope held to contracts\server-rotation.schema.json, key for key.</summary>
/// <remarks>
/// A live sample would need a real rotation, which restarts a server; so the sample is a fully
/// populated status -- every nullable present, every list non-empty -- serialized and compared
/// EXACTLY at each level, the way AzureSchemaTests does it.
/// </remarks>
public class ServerRotationSchemaTests
{
    private static JsonObject Schema() =>
        JsonNode.Parse(File.ReadAllText(Fixture.Resolve("Contracts", "server-rotation.schema.json")))!.AsObject();

    private static JsonObject Emitted() =>
        JsonNode.Parse(ServerRotationJson.Serialize(new RotationStatus
        {
            Server = "janet",
            State = "failed",
            StartedAt = "2026-10-08T23:30:00.0000000Z",
            FinishedAt = "2026-10-08T23:30:41.0000000Z",
            LauncherPid = 4242,
            Error = "publish failed",
            CurrentBuild = @"C:\JanetHome\.janet-bin\build-20261008-233000",
            LogPath = @"C:\Temp\Janet\rotation-janet.log",
            LogTail = ["publishing", "publish failed"],
            Target = new RotationTarget("janet", @"C:\JanetHome\src\Janet.Mcp\Janet.Mcp.csproj", @"C:\JanetHome\.janet-bin", "janet-mcp", ["--http"], [@"C:\JanetHome\src\Janet.Cli\Janet.Cli.csproj"], 7717),
            ServingEnvironment = new EnvironmentReport(true, null, ["Platform"], ["PATH"], ["JANET_RESULT_BUDGET"]),
        }))!.AsObject();

    private static IEnumerable<string> Declared(JsonObject schema) =>
        schema["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);

    private static IEnumerable<string> Required(JsonObject schema) =>
        schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).Order(StringComparer.Ordinal);

    private static IEnumerable<string> Keys(JsonNode node) =>
        node.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);

    [Theory]
    [InlineData(null)]
    [InlineData("target")]
    [InlineData("servingEnvironment")]
    public void EachLevelEmitsExactlyWhatItDeclaresAndRequiresIt(string? property)
    {
        JsonObject schema = property is null ? Schema() : Schema()["properties"]![property]!.AsObject();
        JsonNode emitted = property is null ? Emitted() : Emitted()[property]!;

        Assert.Equal(Declared(schema), Keys(emitted));
        Assert.Equal(Declared(schema), Required(schema));
    }

    [Fact]
    public void TheDeclaredStatesAreTheOnesTheCodeCanProduce()
    {
        string[] declared = [.. Schema()["properties"]!["state"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).Order(StringComparer.Ordinal)];

        Assert.Equal(["failed", "never", "running", "started", "succeeded", "unknown"], declared);
    }

    [Fact]
    public void TheSchemaStampsTheContractTheCodeEmits()
    {
        JsonObject schema = Schema();

        Assert.Equal(RotationStatus.ContractVersion, schema["$janet"]!["contract"]!.GetValue<int>());
        Assert.Equal(RotationStatus.ContractVersion, schema["properties"]!["contract"]!["const"]!.GetValue<int>());
        Assert.Equal(RotationStatus.ContractVersion, Emitted()["contract"]!.GetValue<int>());
    }
}
