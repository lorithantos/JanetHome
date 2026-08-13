using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Proves the write queue does what serialising a read-modify-write is supposed to do.
/// </summary>
/// <remarks>
/// Every test here fails against the implementation that preceded the queue, which is the only
/// reason to keep them: each one reproduces a specific way concurrent writers corrupted the
/// catalog. Two concurrent sessions is the normal case in this repo -- it is the stated reason
/// the thread-item list needs a mutex at all -- and the catalog had no equivalent.
/// </remarks>
public class ConcurrencyTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    /// <summary>
    /// The lost update, which is the whole point.
    /// </summary>
    /// <remarks>
    /// Without the queue every thread reads the same text, splices its own node into it, and
    /// writes; the last writer wins and the rest vanish with no error anywhere. Sixteen writers
    /// reliably lost most of them.
    /// </remarks>
    [Fact]
    public void ConcurrentAddsAllSurvive()
    {
        string graph = _sandbox.CopyOfLayout();
        int before = ResearchGraph.Load(graph).Nodes.Count;
        const int writers = 16;

        Parallel.For(0, writers, i => GraphWriter.Add(graph, new AddRequest
        {
            Id = $"sandbox.concurrent-{i:00}",
            Kind = "note",
            NodePath = "notes\\concurrent.md",
            Summary = $"Written by writer {i}.",
        }));

        ResearchGraph after = ResearchGraph.Load(graph);

        Assert.Equal(before + writers, after.Nodes.Count);

        for (int i = 0; i < writers; i++)
        {
            Assert.True(after.Contains($"sandbox.concurrent-{i:00}"), $"writer {i} was lost");
        }
    }

    /// <summary>Updates to different nodes must not overwrite each other either.</summary>
    [Fact]
    public void ConcurrentUpdatesToDifferentNodesAllSurvive()
    {
        string graph = _sandbox.CopyOfLayout();
        string[] ids = [.. ResearchGraph.Load(graph).Nodes.Select(n => n.Id)];

        Parallel.ForEach(ids, id => GraphWriter.Update(graph, new UpdateRequest
        {
            Id = id,
            Append = true,
            Set = new System.Collections.Generic.OrderedDictionary<string, JsonNode?>
            {
                ["tags"] = new JsonArray("sandbox-concurrent"),
            },
        }));

        ResearchGraph after = ResearchGraph.Load(graph);

        Assert.All(ids, id =>
        {
            Assert.True(after.TryGet(id, out ResearchNode node));
            Assert.Contains("sandbox-concurrent", node.Tags, StringComparer.Ordinal);
        });
    }

    /// <summary>
    /// A caller must never be told a total that was never on disk.
    /// </summary>
    /// <remarks>
    /// Each operation computes its result against the text as it stood partway through its
    /// batch, and that intermediate state never reaches the file. Results are therefore
    /// restated with the total as of the write they landed in -- not as of the whole run, which
    /// would be a different lie: a write that finished first has no business claiming to know
    /// about writes that came after it.
    ///
    /// So the invariant is per batch, and it is exact. Results sharing a total came from the
    /// same write, and every one of them must report a batch size equal to how many of them
    /// there are. That is the merge being asserted rather than assumed: if a batch reported 3
    /// and only 2 results carried its total, someone was told about a write they were not in.
    /// </remarks>
    [Fact]
    public void EveryResultReportsTheWriteItLandedIn()
    {
        string graph = _sandbox.CopyOfLayout();
        int before = ResearchGraph.Load(graph).Nodes.Count;
        const int writers = 12;

        ConcurrentBag<AddResult> results = [];

        Parallel.For(0, writers, i => results.Add(GraphWriter.Add(graph, new AddRequest
        {
            Id = $"sandbox.total-{i:00}",
            Kind = "note",
            NodePath = "notes\\concurrent.md",
            Summary = $"Written by writer {i}.",
        })));

        int final = ResearchGraph.Load(graph).Nodes.Count;

        Assert.Equal(before + writers, final);
        Assert.Equal(writers, results.Count);

        // Every add increments the count, so a total identifies the write it came from.
        List<IGrouping<int, AddResult>> batches = [.. results.GroupBy(r => r.TotalNodes)];

        Assert.All(batches, batch => Assert.All(batch, r => Assert.Equal(batch.Count(), r.Batched)));

        // The totals are the running count, so they land between the first write and the last,
        // and the last batch saw the file as it now stands.
        Assert.All(results, r => Assert.InRange(r.TotalNodes, before + 1, final));
        Assert.Equal(final, batches.Max(b => b.Key));
        Assert.Equal(writers, batches.Sum(b => b.Count()));
    }

    /// <summary>
    /// One caller's bad request must not take down everyone else's good ones.
    /// </summary>
    /// <remarks>
    /// A batch is not a transaction across unrelated callers: someone else's duplicate id is no
    /// reason to reject your valid add. The failure goes to the caller that caused it and the
    /// rest of the batch still commits.
    /// </remarks>
    [Fact]
    public void AFailingWriteFailsOnlyItsOwnCaller()
    {
        string graph = _sandbox.CopyOfLayout();
        int before = ResearchGraph.Load(graph).Nodes.Count;
        const int good = 8;

        ConcurrentBag<Exception> failures = [];

        Parallel.For(0, good + 1, i =>
        {
            // The last one collides with a node the fixture already has.
            string id = i == good ? "script.get-research" : $"sandbox.mixed-{i:00}";

            try
            {
                GraphWriter.Add(graph, new AddRequest
                {
                    Id = id,
                    Kind = "note",
                    NodePath = "notes\\concurrent.md",
                    Summary = "one of a mixed batch",
                });
            }
            catch (GraphException ex)
            {
                failures.Add(ex);
            }
        });

        ResearchGraph after = ResearchGraph.Load(graph);

        Assert.Single(failures);
        Assert.Equal(before + good, after.Nodes.Count);

        for (int i = 0; i < good; i++)
        {
            Assert.True(after.Contains($"sandbox.mixed-{i:00}"), $"writer {i} was rolled back by someone else's failure");
        }
    }

    /// <summary>
    /// An add leaves the graph symmetric: the node names its links and they name it back.
    /// </summary>
    /// <remarks>
    /// Note what this does NOT assert. The reverse links used to be a file write each -- the
    /// node, then one per link -- so the graph was briefly asymmetric and stayed that way for
    /// good if anything failed partway. They are now applied to the batch's in-memory text and
    /// land in the same single write. But this test only reads the end state, which is the same
    /// either way: it passes against the old one-write-per-link implementation too, measured.
    /// The atomicity is structural -- there is no code path left that writes a link separately
    /// -- and this test guards the symmetry, not the number of writes.
    /// </remarks>
    [Fact]
    public void AnAddLeavesTheGraphSymmetric()
    {
        string graph = _sandbox.CopyOfLayout();
        string[] targets = ["pattern.thread-items", "script.get-research"];

        AddResult result = GraphWriter.Add(graph, new AddRequest
        {
            Id = "sandbox.atomic-add",
            Kind = "note",
            NodePath = "notes\\concurrent.md",
            Summary = "arrives with its back-links or not at all",
            Links = targets,
        });

        Assert.Equal(targets, result.ReverseLinks);

        ResearchGraph after = ResearchGraph.Load(graph);

        Assert.All(targets, target =>
        {
            Assert.True(after.TryGet(target, out ResearchNode node));
            Assert.Contains("sandbox.atomic-add", node.Links, StringComparer.Ordinal);
        });
    }

    /// <summary>
    /// The lock excludes a second holder, which is what makes the cross-process case correct.
    /// </summary>
    /// <remarks>
    /// Share mode is enforced by the OS per handle and does not care which process opened it,
    /// so a second handle here fails exactly as a second PROCESS would. That is the closest an
    /// in-process test gets to proving it, and it is the mechanism the CLI relies on: a hook or
    /// a shim is a fresh process every time and cannot join an in-process queue.
    /// </remarks>
    [Fact]
    public void TheWriteLockExcludesASecondHolder()
    {
        string graph = _sandbox.CopyOfLayout();
        string lockPath = graph + ".lock";

        using (FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            GraphQueue.LockTimeout = TimeSpan.FromMilliseconds(200);

            try
            {
                GraphException refused = Assert.Throws<GraphException>(() => GraphWriter.Add(graph, new AddRequest
                {
                    Id = "sandbox.blocked",
                    Kind = "note",
                    NodePath = "notes\\concurrent.md",
                    Summary = "should never be written",
                }));

                // Bounded and loud, not a hang: a hook that waits forever is indistinguishable
                // from an agent thinking.
                Assert.Contains("Could not take the write lock", refused.Message, StringComparison.Ordinal);
                Assert.Contains("Nothing was written", refused.Message, StringComparison.Ordinal);
            }
            finally
            {
                GraphQueue.LockTimeout = TimeSpan.FromSeconds(30);
            }
        }

        Assert.False(ResearchGraph.Load(graph).Contains("sandbox.blocked"));
    }

    public void Dispose()
    {
        _sandbox.Dispose();
        GC.SuppressFinalize(this);
    }
}
