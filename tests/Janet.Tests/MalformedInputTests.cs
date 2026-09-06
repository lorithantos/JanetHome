using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The malformed-input guard on both write paths: a field carrying the body of a mangled tool
/// call is refused, the refusal names what it found and where, and nothing lands.
/// </summary>
/// <remarks>
/// The payloads are the shape the harness actually hands over when an agent's markup breaks:
/// the field's own content, then the closing tag, the next parameter's opening tag, its content,
/// and the closing tags after it -- one string where there should have been two parameters.
/// Every refusal test snapshots the store first and asserts it is byte-identical afterwards,
/// because "refused" and "refused after writing" read the same in a message.
///
/// In the "thread store" collection for the reason the other thread test classes give: another
/// class sets the notes ceiling on the process for the length of one test.
/// </remarks>
[Collection("thread store")]
public class MalformedInputTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly List<string> _directories = [];

    /// <summary>What follows the content of a notes field when the call after it is swallowed.</summary>
    private const string AfterNotes =
        "</notes>\n<parameter name=\"next\">query the telemetry table</parameter>\n</invoke>";

    /// <summary>What follows any parameter's content when the rest of the call is swallowed.</summary>
    private const string AfterParameter =
        "</parameter>\n<parameter name=\"caveats\">[\"one\"]</parameter>\n</invoke>";

    private static ThreadSelector Topic(string topic) => new() { Topic = topic };

    private string Store()
    {
        string directory = Path.Combine(Path.GetTempPath(), "janet-malformed", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        return Path.Combine(directory, "thread-stack.json");
    }

    private string Seeded()
    {
        string path = Store();

        ThreadItems.Add(path, "cache eviction", notes: "Ruled out the TTL.", next: "query the telemetry table", area: "gamehub");

        return path;
    }

    private static void AssertRefusal(GraphException refused, string field, string subject, string signature, int offset)
    {
        Assert.Contains($"{field} for '{subject}'", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"contains '{signature}' at character {offset}", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", refused.Message, StringComparison.Ordinal);
        Assert.Contains("body of a tool call, not content", refused.Message, StringComparison.Ordinal);
        Assert.Contains("one parameter per field", refused.Message, StringComparison.Ordinal);
    }

    private static void AssertUntouched(string path, byte[] before) =>
        Assert.Equal(before, File.ReadAllBytes(path));

    // ---- the scan itself ---------------------------------------------------------------

    [Fact]
    public void FindReportsTheEarliestSignatureAndItsOffset()
    {
        Assert.Equal(("</invoke>", 1), MalformedInput.Find("a</invoke>b</notes>"));
        Assert.Equal(("parameter name=", 4), MalformedInput.Find("ab <parameter name=\"next\">"));
        Assert.Null(MalformedInput.Find("Ruled out the TTL."));
        Assert.Null(MalformedInput.Find(null));
        Assert.Null(MalformedInput.Find(string.Empty));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.Equal(("</notes>", 2), MalformedInput.Find("ab</NOTES>"));
        Assert.Equal(("parameter name=", 1), MalformedInput.Find("<Parameter Name=\"x\">"));

        string path = Seeded();
        byte[] before = File.ReadAllBytes(path);

        GraphException refused = Assert.Throws<GraphException>(() =>
            ThreadItems.Update(path, Topic("cache eviction"), notes: "Shouting.</NOTES></INVOKE>"));

        AssertRefusal(refused, "notes", "cache eviction", "</notes>", 9);
        AssertUntouched(path, before);
    }

    // ---- thread items, one field each ------------------------------------------------

    [Fact]
    public void ThreadTopicOnAddIsRefused()
    {
        string path = Seeded();
        byte[] before = File.ReadAllBytes(path);

        GraphException refused = Assert.Throws<GraphException>(() =>
            ThreadItems.Add(path, "cache warming" + AfterParameter, notes: "clean"));

        AssertRefusal(refused, "topic", "cache warming" + AfterParameter, "</parameter>", 13);
        AssertUntouched(path, before);
    }

    [Fact]
    public void ThreadNotesOnAddAreRefused()
    {
        string path = Seeded();
        byte[] before = File.ReadAllBytes(path);

        GraphException refused = Assert.Throws<GraphException>(() =>
            ThreadItems.Add(path, "cache warming", notes: "Ruled out the TTL." + AfterNotes));

        AssertRefusal(refused, "notes", "cache warming", "</notes>", 18);
        AssertUntouched(path, before);
        Assert.Single(ThreadItems.Show(path, all: true).Items);
    }

    [Fact]
    public void ThreadNotesOnReplacingUpdateAreRefused()
    {
        string path = Seeded();
        byte[] before = File.ReadAllBytes(path);

        GraphException refused = Assert.Throws<GraphException>(() =>
            ThreadItems.Update(path, Topic("cache eviction"), notes: "Second pass." + AfterNotes));

        AssertRefusal(refused, "notes", "cache eviction", "</notes>", 12);
        AssertUntouched(path, before);
    }

    /// <summary>An append scans the fragment, and the fragment is what the refusal describes.</summary>
    [Fact]
    public void ThreadNotesOnAppendingUpdateAreRefusedByTheFragment()
    {
        string path = Seeded();
        byte[] before = File.ReadAllBytes(path);

        GraphException refused = Assert.Throws<GraphException>(() =>
            ThreadItems.Update(path, Topic("cache eviction"), notes: "Second pass." + AfterNotes, appendNotes: true));

        // Offset 12 is where the tag sits in the FRAGMENT; in the would-be result it sits after
        // the existing notes and the blank line, at 32.
        AssertRefusal(refused, "notes", "cache eviction", "</notes>", 12);
        AssertUntouched(path, before);
    }

    [Fact]
    public void ThreadNextIsRefusedOnAddAndOnUpdate()
    {
        string path = Seeded();
        byte[] before = File.ReadAllBytes(path);
        string next = "query the table</next>\n</invoke>";

        GraphException onAdd = Assert.Throws<GraphException>(() =>
            ThreadItems.Add(path, "cache warming", next: next));

        GraphException onUpdate = Assert.Throws<GraphException>(() =>
            ThreadItems.Update(path, Topic("cache eviction"), next: next));

        AssertRefusal(onAdd, "next", "cache warming", "</next>", 15);
        AssertRefusal(onUpdate, "next", "cache eviction", "</next>", 15);
        AssertUntouched(path, before);
    }

    [Fact]
    public void ThreadAreaIsRefusedOnAddAndOnUpdate()
    {
        string path = Seeded();
        byte[] before = File.ReadAllBytes(path);
        string area = "gamehub</parameter>\n</invoke>";

        GraphException onAdd = Assert.Throws<GraphException>(() =>
            ThreadItems.Add(path, "cache warming", area: area));

        GraphException onUpdate = Assert.Throws<GraphException>(() =>
            ThreadItems.Update(path, Topic("cache eviction"), area: area));

        AssertRefusal(onAdd, "area", "cache warming", "</parameter>", 7);
        AssertRefusal(onUpdate, "area", "cache eviction", "</parameter>", 7);
        AssertUntouched(path, before);
    }

    /// <summary>
    /// An item whose STORED notes carry the literal tags stays amendable: the append scans the
    /// fragment, not the result.
    /// </summary>
    /// <remarks>
    /// Such items exist -- the corrupted ones, and the thread item that describes the bug with
    /// the tags spelled out. A scan of the result would freeze exactly the items the guard is
    /// about.
    /// </remarks>
    [Fact]
    public void AnAppendToNotesThatAlreadyHoldTheTagsLandsWhenTheFragmentIsClean()
    {
        string path = Store();
        string existing = "The store holds </notes> then <parameter name=\"next\"> verbatim; repair parked.";

        File.WriteAllText(path, new JsonArray(new JsonObject
        {
            ["topic"] = "markup repair",
            ["status"] = "parked",
            ["refs"] = new JsonArray(),
            ["next"] = "",
            ["notes"] = existing,
        }).ToJsonString());

        ThreadItems.Update(path, Topic("markup repair"), notes: "Diffs for the seven items drafted.", appendNotes: true);
        ThreadItems.Update(path, Topic("markup repair"), next: "dry-run the repair");

        ThreadShownItem item = Assert.Single(ThreadItems.Show(path, topic: "markup repair", full: true).Items);

        Assert.Equal(existing + "\n\nDiffs for the seven items drafted.", item.Notes);
        Assert.Equal("dry-run the repair", item.Next);
    }

    // ---- research nodes, one field each ----------------------------------------------

    private static AddRequest Node(string summary, IReadOnlyList<string>? caveats = null, string? section = null) => new()
    {
        Id = "sandbox.mangled",
        Kind = "note",
        NodePath = "notes\\sandbox-mangled.md",
        Summary = summary,
        Caveats = caveats ?? [],
        Section = section,
    };

    private static UpdateRequest Patch(string field, JsonNode? value, bool append = false) => new()
    {
        Id = "script.get-research",
        Append = append,
        Set = new System.Collections.Generic.OrderedDictionary<string, JsonNode?> { [field] = value },
    };

    [Fact]
    public void ResearchSummaryOnAddIsRefused()
    {
        string graph = _sandbox.CopyOfLayout();
        byte[] before = File.ReadAllBytes(graph);

        GraphException refused = Assert.Throws<GraphException>(() =>
            GraphWriter.Add(graph, Node("A summary." + AfterParameter)));

        AssertRefusal(refused, "summary", "sandbox.mangled", "</parameter>", 10);
        AssertUntouched(graph, before);
    }

    [Fact]
    public void ResearchSummaryOnUpdateIsRefused()
    {
        string graph = _sandbox.CopyOfLayout();
        byte[] before = File.ReadAllBytes(graph);

        GraphException refused = Assert.Throws<GraphException>(() =>
            GraphWriter.Update(graph, Patch("summary", "A summary." + AfterParameter)));

        AssertRefusal(refused, "summary", "script.get-research", "</parameter>", 10);
        AssertUntouched(graph, before);
    }

    /// <summary>Each caveat is scanned, and the refusal says which one.</summary>
    [Fact]
    public void ResearchCaveatsOnAddAreRefusedPerEntry()
    {
        string graph = _sandbox.CopyOfLayout();
        byte[] before = File.ReadAllBytes(graph);

        GraphException refused = Assert.Throws<GraphException>(() =>
            GraphWriter.Add(graph, Node("Clean.", caveats: ["A clean caveat.", "Bites." + AfterParameter])));

        AssertRefusal(refused, "caveats[1]", "sandbox.mangled", "</parameter>", 6);
        AssertUntouched(graph, before);
    }

    [Fact]
    public void ResearchCaveatsOnAppendingUpdateAreRefusedPerEntry()
    {
        string graph = _sandbox.CopyOfLayout();
        byte[] before = File.ReadAllBytes(graph);

        GraphException refused = Assert.Throws<GraphException>(() =>
            GraphWriter.Update(graph, Patch(
                "caveats", new JsonArray("A clean caveat.", "Bites." + AfterParameter), append: true)));

        AssertRefusal(refused, "caveats[1]", "script.get-research", "</parameter>", 6);
        AssertUntouched(graph, before);
    }

    [Fact]
    public void ResearchSectionIsRefusedOnAddAndOnUpdate()
    {
        string graph = _sandbox.CopyOfLayout();
        byte[] before = File.ReadAllBytes(graph);
        string section = "3" + AfterParameter;

        GraphException onAdd = Assert.Throws<GraphException>(() =>
            GraphWriter.Add(graph, Node("Clean.", section: section)));

        GraphException onUpdate = Assert.Throws<GraphException>(() =>
            GraphWriter.Update(graph, Patch("section", section)));

        AssertRefusal(onAdd, "section", "sandbox.mangled", "</parameter>", 1);
        AssertRefusal(onUpdate, "section", "script.get-research", "</parameter>", 1);
        AssertUntouched(graph, before);
    }

    /// <summary>
    /// Angle brackets are not the signal. Arrows and generic type names are ordinary prose here.
    /// </summary>
    [Fact]
    public void ACleanSummaryWithArrowsAndGenericsIsAccepted()
    {
        string graph = _sandbox.CopyOfLayout();
        string summary = "Maps A -> B through a List<string> and a Dictionary<string, int>; <notes> is not a tag here.";

        AddResult added = GraphWriter.Add(graph, Node(summary, caveats: ["Returns IReadOnlyList<T> -> never null."]));

        Assert.True(added.Added);
        Assert.True(ResearchGraph.Load(graph).TryGet("sandbox.mangled", out ResearchNode node));
        Assert.Equal(summary, node.Summary);
    }

    public void Dispose()
    {
        _sandbox.Dispose();

        foreach (string directory in _directories.Where(Directory.Exists))
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
        }

        GC.SuppressFinalize(this);
    }
}
