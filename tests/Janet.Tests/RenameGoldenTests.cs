using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Rename must move every inbound link with the node.
/// </summary>
/// <remarks>
/// A rename that misses one leaves a dangling link that reads exactly like a deleted node --
/// which is the failure the operation exists to prevent, so it is asserted rather than assumed.
/// The layout fixture carries eight inbound links to the renamed node for that reason.
/// </remarks>
public class RenameGoldenTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    [Fact]
    public void MatchesTheRecordedAnswer()
    {
        string graph = _sandbox.CopyOfLayout();

        GraphRenamer.Rename(graph, new RenameRequest
        {
            Id = "script.get-research",
            NewId = "script.get-research-renamed",
        });

        GraphAssert.SameFile(Fixture.Golden("write", "rename", ".json"), graph, "rename");
    }

    [Fact]
    public void MovesEveryInboundLink()
    {
        string graph = _sandbox.CopyOfLayout();

        ResearchGraph before = ResearchGraph.Load(graph);
        int inboundBefore = before.Nodes.Count(n => n.Links.Contains("script.get-research", StringComparer.Ordinal));
        Assert.True(inboundBefore > 0, "fixture must have inbound links to be meaningful");

        RenameResult result = GraphRenamer.Rename(graph, new RenameRequest
        {
            Id = "script.get-research",
            NewId = "script.renamed-target",
        });

        ResearchGraph after = ResearchGraph.Load(graph);

        Assert.True(after.Contains("script.renamed-target"));
        Assert.False(after.Contains("script.get-research"));
        Assert.Equal(before.Nodes.Count, after.Nodes.Count);
        Assert.DoesNotContain(after.Nodes, n => n.Links.Contains("script.get-research", StringComparer.Ordinal));
        Assert.Equal(inboundBefore, after.Nodes.Count(n => n.Links.Contains("script.renamed-target", StringComparer.Ordinal)));
        Assert.Equal(inboundBefore, result.Relinked.Count);
    }

    [Fact]
    public void RefusesToRenameOntoAnExistingId()
    {
        string graph = _sandbox.CopyOfLayout();
        byte[] before = File.ReadAllBytes(graph);

        // Renaming onto a live id would merge two nodes into one and silently lose the target.
        Assert.Throws<GraphException>(() => GraphRenamer.Rename(graph, new RenameRequest
        {
            Id = "script.get-research",
            NewId = "script.search-json",
        }));

        Assert.Equal(before, File.ReadAllBytes(graph));
    }

    [Fact]
    public void ReportsBodyReferencesWithoutRewritingThem()
    {
        string graph = _sandbox.CopyOfLayout();
        string notes = Path.Combine(Path.GetDirectoryName(graph)!, "notes");
        Directory.CreateDirectory(notes);

        string note = Path.Combine(notes, "mentions.md");
        File.WriteAllText(note, "See script.get-research for the entry point.");

        RenameResult result = GraphRenamer.Rename(graph, new RenameRequest
        {
            Id = "script.get-research",
            NewId = "script.renamed-again",
        });

        Assert.Contains(result.BodyReferences, r => r.EndsWith("mentions.md", StringComparison.Ordinal));

        // Reported, not rewritten: editing prose is a different and much larger promise.
        Assert.Contains("script.get-research", File.ReadAllText(note), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _sandbox.Dispose();
        GC.SuppressFinalize(this);
    }
}
