using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Runs each API-doc case against the frozen XML fixture and compares the envelope, and the
/// formatted view, with what Get-ApiDoc.ps1 answered for it.
/// </summary>
/// <remarks>
/// Recorded once by tests/Janet.Goldens from the script in git, so the comparison is against an
/// independent implementation and needs no PowerShell to run.
///
/// The whole member is compared, field by field, rather than only the ids: this port's risky part
/// is not which members match but what each one says about itself -- the flattened prose, the
/// shortened signature, and which summary an inheritdoc chain resolved to.
/// </remarks>
public class ApiDocGoldenTests
{
    private static string FixtureXml => File.ReadAllText(Fixture.Resolve("Fixtures", "apidoc.xml"));

    public static TheoryData<string> Labels() => [.. Cases.ApiDocs.Select(c => c.Label)];

    public static TheoryData<string> TextLabels() => [.. Cases.ApiDocTextViews.Select(c => c.Label)];

    [Theory]
    [MemberData(nameof(Labels))]
    public void MatchesTheRecordedAnswer(string label)
    {
        Cases.Read query = Cases.ApiDocs.Single(c => c.Label == label);
        ApiDocRequest request = Parse(query.Args);

        IReadOnlyList<ApiMember> members = ApiDoc.Parse(FixtureXml, Cases.ApiDocSource, request.Full);

        string actualJson = query.Args.Length == 0
            ? ApiDocJson.Serialize(ApiDoc.Orient(members, Cases.ApiDocSource))
            : ApiDocJson.Serialize(ApiDoc.Query(members, Cases.ApiDocSource, request));

        JsonNode expected = JsonNode.Parse(Fixture.ReadGolden("apidoc", label, ".json"))!;
        JsonNode actual = JsonNode.Parse(actualJson)!;

        if (expected["members"] is null)
        {
            AssertSameOrientation(label, expected, actual);
            return;
        }

        AssertSameEnvelope(label, expected, actual);
    }

    [Theory]
    [MemberData(nameof(TextLabels))]
    public void TheFormattedViewMatchesTheRecordedAnswer(string label)
    {
        Cases.Read view = Cases.ApiDocTextViews.Single(c => c.Label == label);
        ApiDocRequest request = Parse(view.Args);

        IReadOnlyList<ApiMember> members = ApiDoc.Parse(FixtureXml, Cases.ApiDocSource, request.Full);

        string actual = view.Args.Length == 0
            ? ApiDocJson.Render(ApiDoc.Orient(members, Cases.ApiDocSource))
            : ApiDocJson.Render(ApiDoc.Query(members, Cases.ApiDocSource, request), request.Full);

        Assert.Equal(Fixture.ReadGolden("apidoc-text", label, ".txt"), actual);
    }

    private static void AssertSameOrientation(string label, JsonNode expected, JsonNode actual)
    {
        Assert.Equal(expected["source"]!.GetValue<string>(), actual["source"]!.GetValue<string>());
        Assert.Equal(expected["total"]!.GetValue<int>(), actual["total"]!.GetValue<int>());

        // Flattened to a list of pairs so ORDER is asserted too. The largest-types map is sorted
        // by count and the caller reads it top down; a map with the same entries in another order
        // answers a different question.
        Assert.Equal(Flatten(expected["kinds"]!), Flatten(actual["kinds"]!));
        Assert.Equal(Flatten(expected["types"]!), Flatten(actual["types"]!));
    }

    private static void AssertSameEnvelope(string label, JsonNode expected, JsonNode actual)
    {
        string[] expectedIds = Ids(expected);
        string[] actualIds = Ids(actual);

        // Order is part of the contract: the caller picks from a ranked shortlist without having
        // read the file, so a different ranking is a different answer.
        Assert.True(
            expectedIds.SequenceEqual(actualIds),
            $"[{label}] member order differs.\n  golden: {string.Join(", ", expectedIds)}\n  actual: {string.Join(", ", actualIds)}");

        Assert.Equal(expected["source"]!.GetValue<string>(), actual["source"]!.GetValue<string>());
        Assert.Equal(expected["returned"]!.GetValue<int>(), actual["returned"]!.GetValue<int>());
        Assert.Equal(expected["totalMatches"]!.GetValue<int>(), actual["totalMatches"]!.GetValue<int>());
        Assert.Equal(expected["truncated"]!.GetValue<bool>(), actual["truncated"]!.GetValue<bool>());

        JsonArray expectedMembers = expected["members"]!.AsArray();
        JsonArray actualMembers = actual["members"]!.AsArray();

        for (int i = 0; i < expectedMembers.Count; i++)
        {
            Assert.True(
                JsonNode.DeepEquals(expectedMembers[i], actualMembers[i]),
                $"[{label}] member {expectedIds[i]} differs.\n  golden: {expectedMembers[i]}\n  actual: {actualMembers[i]}");
        }
    }

    private static string[] Ids(JsonNode envelope) =>
        [.. envelope["members"]!.AsArray().Select(m => m!["id"]!.GetValue<string>())];

    private static List<string> Flatten(JsonNode map) =>
        [.. map.AsObject().Select(p => $"{p.Key}={p.Value!.GetValue<int>()}")];

    /// <summary>Turns the case's argument list into the equivalent request.</summary>
    /// <remarks>
    /// The arguments are the PowerShell ones because the golden was captured by running them.
    /// Binding them here rather than storing a request object is what keeps the two sides
    /// answering the same question.
    /// </remarks>
    internal static ApiDocRequest Parse(string[] args)
    {
        List<string> ids = [];
        string? query = null;
        string? kind = null;
        string? type = null;
        int first = 5;
        bool all = false;
        bool full = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-Id": ids.Add(args[++i]); break;
                case "-Query": query = args[++i]; break;
                case "-Kind": kind = args[++i]; break;
                case "-Type": type = args[++i]; break;
                case "-First": first = int.Parse(args[++i]); break;
                case "-All": all = true; break;
                case "-Full": full = true; break;
                default: throw new ArgumentException($"unhandled case argument: {args[i]}");
            }
        }

        return new ApiDocRequest
        {
            Ids = ids,
            Query = query,
            Kind = kind,
            Type = type,
            First = first,
            All = all,
            Full = full,
        };
    }
}
