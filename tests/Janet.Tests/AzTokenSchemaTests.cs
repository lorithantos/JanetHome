using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// contracts\az-token.schema.json and the envelope AzTokenJson emits, held to each other from the
/// code side, in both of its arms.
/// </summary>
/// <remarks>
/// The gate's sampler needs a live `az login` and asks for no token, so on a signed-in machine it
/// samples the withheld arm and nowhere else does it sample anything: the arm that carries the
/// token was never checked by any test against the schema. Both arms are built here from a fake
/// value, with no credential and no `az`, so no real token is ever involved. The shape follows
/// DotnetCheckSchemaTests: the whole schema closed, then every arm walked beside it.
/// </remarks>
public class AzTokenSchemaTests
{
    private const string SchemaFile = "az-token.schema.json";

    private const string FakeToken = "fake-token-not-a-credential";

    /// <summary>One answer with every nullable populated, and the token set or withheld.</summary>
    private static JsonObject Emitted(string? token) =>
        JsonNode.Parse(AzTokenJson.Serialize(new AzTokenResult
        {
            Requested = "arm",
            Scope = "https://management.azure.com/.default",
            Resource = "arm",
            Tenant = "00000000-0000-0000-0000-000000000000",
            TokenType = "Bearer",
            ExpiresOn = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero),
            ExpiresInSeconds = 3599,
            Cached = true,
            ServedBy = ServedBy.Server,
            Token = token,
        }))!.AsObject();

    private static IEnumerable<string> Keys(JsonObject emitted) =>
        emitted.Select(property => property.Key).Order(StringComparer.Ordinal);

    [Fact]
    public void EveryObjectTheSchemaDeclaresIsClosedAndRequiresEveryPropertyButToken() =>
        ContractSchema.AssertEveryObjectIsClosedAndRequiresEveryProperty(SchemaFile, ["#/token"]);

    [Fact]
    public void TheTokenArmEmitsEveryDeclaredKey()
    {
        JsonObject asked = Emitted(FakeToken);

        Assert.Equal(ContractSchema.Declared(ContractSchema.Load(SchemaFile)), Keys(asked));
        Assert.Equal(FakeToken, (string?)asked["token"]);
    }

    [Fact]
    public void TheWithheldArmEmitsExactlyWhatTheSchemaRequires()
    {
        // Absent, not null: the required set is everything but token, and that is all it emits.
        Assert.Equal(ContractSchema.Required(ContractSchema.Load(SchemaFile)), Keys(Emitted(null)));
    }

    [Fact]
    public void BothArmsAreMatchedAndTogetherReachEveryObjectTheSchemaDeclares()
    {
        JsonObject root = ContractSchema.Load(SchemaFile);
        HashSet<JsonObject> reached = new(ReferenceEqualityComparer.Instance);

        ContractSchema.AssertEmittedMatches(root, root, Emitted(FakeToken), "token", reached);
        ContractSchema.AssertEmittedMatches(root, root, Emitted(null), "withheld", reached);

        string[] unreached = ContractSchema.ObjectSchemas(root, "#")
            .Where(found => !reached.Contains(found.Schema))
            .Select(found => found.Path)
            .ToArray();

        Assert.True(unreached.Length == 0, $"no emitted object reached: {string.Join(", ", unreached)}");
    }

    [Fact]
    public void TheSchemaStampsTheContractTheCodeEmits()
    {
        JsonObject schema = ContractSchema.Load(SchemaFile);
        int emitted = Emitted(null)["contract"]!.GetValue<int>();

        Assert.Equal(emitted, schema["$janet"]!["contract"]!.GetValue<int>());
        Assert.Equal(emitted, schema["properties"]!["contract"]!["const"]!.GetValue<int>());
    }
}
