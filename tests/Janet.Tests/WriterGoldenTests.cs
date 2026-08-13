using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Applies each write to a copy of the layout fixture and compares the resulting file, byte for
/// byte, with the file the original PowerShell produced from the same starting point.
/// </summary>
/// <remarks>
/// Byte equality is the assertion that matters, not "both produce valid JSON". The whole reason
/// writes splice instead of reserializing is that the graph is hand-curated -- comment keys,
/// grouped sections, blank lines, field order -- and an implementation that produced equivalent
/// JSON with a different shape would pass a semantic check and still turn every future change
/// into an unreadable diff.
/// </remarks>
public class WriterGoldenTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public static TheoryData<string> Labels() =>
        [.. Cases.Writes.Where(w => w.Script != "Rename-ResearchNode.ps1").Select(w => w.Label)];

    [Theory]
    [MemberData(nameof(Labels))]
    public void MatchesTheRecordedAnswer(string label)
    {
        string graph = _sandbox.CopyOfLayout();

        Apply(label, graph);

        GraphAssert.SameFile(Fixture.Golden("write", label, ".json"), graph, label);
    }

    /// <summary>
    /// The same operations as <see cref="Cases.Writes"/>, expressed as Janet.Core calls.
    /// </summary>
    /// <remarks>
    /// Deliberately hand-written rather than bound from the argument list: binding both sides
    /// through one translator would mean a bug in the translator cancelling itself out. If this
    /// drifts from the case list, the golden no longer describes what ran and the test fails --
    /// which is the point.
    /// </remarks>
    private static void Apply(string label, string graph)
    {
        switch (label)
        {
            case "add":
                GraphWriter.Add(graph, new AddRequest
                {
                    Id = "sandbox.parity-add",
                    Kind = "note",
                    NodePath = "notes\\sandbox-parity.md",
                    Summary = Cases.AwkwardSummary,
                    Tags = ["retrieval", "agent"],
                    Links = ["pattern.thread-items", "script.get-research"],
                    Caveats = [Cases.AwkwardCaveat],
                });
                break;

            case "add with params and section":
                GraphWriter.Add(graph, new AddRequest
                {
                    Id = "sandbox.parity-script",
                    Kind = "script",
                    NodePath = "scripts\\Sandbox-Parity.ps1",
                    Summary = "A script node, to exercise the params branch.",
                    Params = ["Path", "Force", "WhatIf"],
                    Tags = ["powershell"],
                    Section = "12",
                });
                break;

            case "update summary":
                GraphWriter.Update(graph, new UpdateRequest
                {
                    Id = "script.get-research",
                    Set = new System.Collections.Generic.OrderedDictionary<string, JsonNode?>
                    {
                        ["summary"] = Cases.AwkwardSummary,
                    },
                });
                break;

            case "update appending tags":
                GraphWriter.Update(graph, new UpdateRequest
                {
                    Id = "script.get-research",
                    Append = true,
                    Set = new System.Collections.Generic.OrderedDictionary<string, JsonNode?>
                    {
                        ["tags"] = new JsonArray("retrieval", "sandbox-new-tag"),
                    },
                });
                break;

            case "update removing caveats":
                GraphWriter.Update(graph, new UpdateRequest
                {
                    Id = "script.get-research",
                    Remove = ["caveats"],
                });
                break;

            case "update replacing caveats":
                GraphWriter.Update(graph, new UpdateRequest
                {
                    Id = "script.search-json",
                    Set = new System.Collections.Generic.OrderedDictionary<string, JsonNode?>
                    {
                        ["caveats"] = new JsonArray(
                            Cases.AwkwardCaveat, "A second caveat, with its own comma."),
                    },
                });
                break;

            default:
                throw new ArgumentException($"no Janet.Core equivalent for case '{label}'");
        }
    }

    /// <summary>
    /// The one place the port deliberately does not match the original.
    /// </summary>
    /// <remarks>
    /// Add-ResearchNode.ps1 wrote a new node's tags before its caveats; 37 of the graph's 51
    /// nodes carrying both fields have caveats first, and the 14 that do not are the ones that
    /// script wrote. The golden for "add" carries this correction, declared in
    /// tests/Janet.Goldens and recorded in Goldens/meta.json -- asserted here as well so the
    /// deviation is visible in the suite and not only in the generator.
    /// </remarks>
    [Fact]
    public void AddedNodePutsCaveatsBeforeTags()
    {
        string graph = _sandbox.CopyOfLayout();

        Apply("add", graph);

        string[] node = [.. File.ReadAllLines(graph)
            .SkipWhile(l => !l.Contains("\"sandbox.parity-add\"", StringComparison.Ordinal))];

        int caveats = Array.FindIndex(node, l => l.Contains("\"caveats\"", StringComparison.Ordinal));
        int tags = Array.FindIndex(node, l => l.Contains("\"tags\"", StringComparison.Ordinal));

        Assert.True(caveats >= 0 && tags >= 0, "the added node should carry both fields");
        Assert.True(caveats < tags, "caveats belongs before tags, as in the hand-authored nodes");
    }

    [Fact]
    public void AnUpdateTouchesOnlyItsOwnNode()
    {
        string graph = _sandbox.CopyOfLayout();

        Apply("update summary", graph);

        GraphAssert.OnlyOneNodeChanged(graph, "script.get-research");
    }

    [Fact]
    public void RefusesToWriteWhenTheNodeIsMissing()
    {
        string graph = _sandbox.CopyOfLayout();
        byte[] before = File.ReadAllBytes(graph);

        Assert.Throws<GraphException>(() => GraphWriter.Update(graph, new UpdateRequest
        {
            Id = "sandbox.does-not-exist",
            Set = new System.Collections.Generic.OrderedDictionary<string, JsonNode?> { ["summary"] = "x" },
        }));

        Assert.Equal(before, File.ReadAllBytes(graph));
    }

    [Fact]
    public void RefusesToRemoveARequiredField()
    {
        string graph = _sandbox.CopyOfLayout();
        byte[] before = File.ReadAllBytes(graph);

        Assert.Throws<GraphException>(() => GraphWriter.Update(graph, new UpdateRequest
        {
            Id = "script.get-research",
            Remove = ["summary"],
        }));

        Assert.Equal(before, File.ReadAllBytes(graph));
    }

    [Fact]
    public void RefusesToAddADuplicateId()
    {
        string graph = _sandbox.CopyOfLayout();
        byte[] before = File.ReadAllBytes(graph);

        Assert.Throws<GraphException>(() => GraphWriter.Add(graph, new AddRequest
        {
            Id = "script.get-research",
            Kind = "script",
            NodePath = "scripts\\Get-Research.ps1",
            Summary = "duplicate",
        }));

        Assert.Equal(before, File.ReadAllBytes(graph));
    }

    public void Dispose()
    {
        _sandbox.Dispose();
        GC.SuppressFinalize(this);
    }
}
