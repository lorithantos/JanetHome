using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Runs each query against the frozen corpus and compares the envelope with the answer
/// Get-Research.ps1 gave for it.
/// </summary>
/// <remarks>
/// These began as parity tests that shelled out to PowerShell on every run. That stopped being
/// a test the moment the scripts became shims over this code: it compared the port with itself
/// and reported green. The answers are now recorded once, by tests/Janet.Goldens, from the
/// scripts as they stood in git before the shims -- so the comparison is still against an
/// independent implementation, and it runs on a machine with no PowerShell at all.
///
/// Compared as parsed JSON rather than as text: the two sides escape ampersands and apostrophes
/// differently, and the contract is the content and its order, not the encoder.
/// </remarks>
public class CatalogGoldenTests
{
    public static TheoryData<string> Labels() => [.. Cases.Queries.Select(q => q.Label)];

    [Theory]
    [MemberData(nameof(Labels))]
    public void MatchesTheRecordedAnswer(string label)
    {
        Cases.Read query = Cases.Queries.Single(q => q.Label == label);
        ResearchGraph graph = ResearchGraph.Load(Fixture.Corpus);

        string actualJson = query.Args.Length == 0
            ? CatalogJson.Serialize(CatalogQuery.Orient(graph))
            : CatalogJson.Serialize(CatalogQuery.Execute(graph, Parse(query.Args)));

        JsonNode expected = JsonNode.Parse(Fixture.ReadGolden("query", label, ".json"))!;
        JsonNode actual = JsonNode.Parse(actualJson)!;

        if (expected["nodes"] is null)
        {
            AssertSameOrientation(label, expected, actual);
            return;
        }

        AssertSameEnvelope(label, expected, actual);
    }

    private static void AssertSameOrientation(string label, JsonNode expected, JsonNode actual)
    {
        Assert.Equal(expected["total"]!.GetValue<int>(), actual["total"]!.GetValue<int>());
        Assert.Equal(Flatten(expected["kinds"]!), Flatten(actual["kinds"]!));
        Assert.Equal(Flatten(expected["tags"]!), Flatten(actual["tags"]!));
        Assert.True(expected["nodes"] is null && actual["nodes"] is null, $"[{label}] orientation carries nodes");
    }

    private static void AssertSameEnvelope(string label, JsonNode expected, JsonNode actual)
    {
        string[] expectedIds = Ids(expected);
        string[] actualIds = Ids(actual);

        // Order is part of the contract, not incidental: the caller's job is to pick the right
        // node without having read the graph, so a shortlist that ranks differently is a
        // different answer even when it holds the same nodes.
        Assert.True(
            expectedIds.SequenceEqual(actualIds),
            $"[{label}] node order differs.\n  golden: {string.Join(", ", expectedIds)}\n  actual: {string.Join(", ", actualIds)}");

        Assert.Equal(expected["returned"]!.GetValue<int>(), actual["returned"]!.GetValue<int>());
        Assert.Equal(expected["totalMatches"]!.GetValue<int>(), actual["totalMatches"]!.GetValue<int>());
        Assert.Equal(expected["truncated"]!.GetValue<bool>(), actual["truncated"]!.GetValue<bool>());

        JsonArray expectedNodes = expected["nodes"]!.AsArray();
        JsonArray actualNodes = actual["nodes"]!.AsArray();

        for (int i = 0; i < expectedNodes.Count; i++)
        {
            Assert.True(
                JsonNode.DeepEquals(expectedNodes[i], actualNodes[i]),
                $"[{label}] node {expectedIds[i]} differs.\n  golden: {expectedNodes[i]}\n  actual: {actualNodes[i]}");
        }
    }

    private static string[] Ids(JsonNode envelope) =>
        [.. envelope["nodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>())];

    private static List<string> Flatten(JsonNode map) =>
        [.. map.AsObject().Select(p => $"{p.Key}={p.Value!.GetValue<int>()}")];

    /// <summary>Turns the case's argument list into the equivalent options object.</summary>
    /// <remarks>
    /// The arguments are the PowerShell ones because the golden was captured by running them.
    /// Binding them here rather than storing an options object is what keeps the two sides
    /// answering the same question.
    /// </remarks>
    internal static CatalogQueryOptions Parse(string[] args)
    {
        List<string> ids = [];
        List<string> tags = [];
        string? kind = null;
        string? query = null;
        int first = 5;
        int depth = 1;
        bool all = false;
        bool expand = false;
        bool full = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-Id": ids.Add(args[++i]); break;
                case "-Tag": tags.Add(args[++i]); break;
                case "-Kind": kind = args[++i]; break;
                case "-Query": query = args[++i]; break;
                case "-First": first = int.Parse(args[++i]); break;
                case "-Depth": depth = int.Parse(args[++i]); break;
                case "-All": all = true; break;
                case "-Expand": expand = true; break;
                case "-Full": full = true; break;
                default: throw new ArgumentException($"unhandled case argument: {args[i]}");
            }
        }

        return new CatalogQueryOptions
        {
            Id = ids,
            Tag = tags,
            Kind = kind,
            Query = query,
            First = first,
            Depth = depth,
            All = all,
            Expand = expand,
            Full = full,
        };
    }
}
