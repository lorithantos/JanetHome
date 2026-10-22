using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// contracts\assembly-api.schema.json and the envelope AssemblyApiJson emits, held to each other
/// from the code side.
/// </summary>
/// <remarks>
/// Until this the envelope was pinned only by the gate's sampler, and the sampler reads a real
/// assembly whose folder holds its dependencies -- so siblingWarning, the one key that exists
/// only when there is something to say, was never in a sample at all. A rename of it on either
/// side passed every check. The shape follows DotnetCheckSchemaTests: the whole schema closed,
/// then one fully populated envelope walked beside it and required to reach every object the
/// schema declares.
/// </remarks>
public class AssemblyApiSchemaTests
{
    private const string SchemaFile = "assembly-api.schema.json";

    /// <summary>Every key populated, siblingWarning included, and every list non-empty.</summary>
    private static JsonObject FullyPopulated()
    {
        AssemblyApiResult result = new(
            1,
            "Sample.Library.dll",
            @"D:\Packages\sample.library\1.0.0\lib\net8.0",
            1,
            2,
            1,
            2,
            1,
            true,
            [new AssemblyType(
                "Sample.Library.Widget",
                "class",
                "System.Object",
                [new AssemblyMember("Property", "System.String", "Name")])],
            "Only 1 assembly in the folder: dependencies cannot resolve. Point at a build output instead.",
            3);

        return JsonNode.Parse(AssemblyApiJson.Serialize(result))!.AsObject();
    }

    [Fact]
    public void EveryObjectTheSchemaDeclaresIsClosedAndRequiresEveryPropertyButSiblingWarning() =>
        ContractSchema.AssertEveryObjectIsClosedAndRequiresEveryProperty(SchemaFile, ["#/siblingWarning"]);

    [Fact]
    public void AFullyPopulatedEnvelopeEmitsEveryDeclaredRootKeySiblingWarningIncluded()
    {
        // The walk below lets an optional key be absent, so the sample's own key set is what
        // proves siblingWarning was emitted under the name the schema declares.
        Assert.Equal(
            ContractSchema.Declared(ContractSchema.Load(SchemaFile)),
            FullyPopulated().Select(property => property.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryObjectTheSchemaDeclaresIsReachedAndMatchedByAFullyPopulatedEnvelope()
    {
        JsonObject root = ContractSchema.Load(SchemaFile);
        HashSet<JsonObject> reached = new(ReferenceEqualityComparer.Instance);

        ContractSchema.AssertEmittedMatches(root, root, FullyPopulated(), "populated", reached);

        string[] unreached = ContractSchema.ObjectSchemas(root, "#")
            .Where(found => !reached.Contains(found.Schema))
            .Select(found => found.Path)
            .ToArray();

        Assert.True(unreached.Length == 0, $"no emitted object reached: {string.Join(", ", unreached)}");
    }
}
