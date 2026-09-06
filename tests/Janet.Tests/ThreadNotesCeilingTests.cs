using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The two governance rules that arrived together on 2026-09-05: a write that would leave an
/// item's notes over the ceiling is refused, and Show carries a lead unless one item is asked
/// for whole.
/// </summary>
/// <remarks>
/// Both exist because the store is one JSON file with the notes inline, and nothing bounded
/// them: the list reached 238,552 characters through Show and three items held a third of it.
/// The result budget refuses the read that is too large; this refuses the write that would
/// make the next read too large, which is the cheaper place to say no.
///
/// In the "thread store" collection, and the reason the other three thread test classes are
/// too: <see cref="TheOverrideGovernsTheWrite"/> sets JANET_NOTES_BUDGET on the process for the
/// length of one test, and xUnit runs classes in parallel unless they share a collection. A
/// write in another class racing that window would be refused for a reason its own file never
/// mentions.
/// </remarks>
[Collection("thread store")]
public class ThreadNotesCeilingTests : IDisposable
{
    private readonly List<string> _directories = [];

    private string Empty()
    {
        string directory = Path.Combine(Path.GetTempPath(), "janet-notes-ceiling", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        return Path.Combine(directory, "thread-stack.json");
    }

    private static string Chars(int count, char c = 'x') => new(c, count);

    private static ThreadSelector Topic(string topic) => new() { Topic = topic };

    // ---- the ceiling -------------------------------------------------------------------

    /// <summary>An add over the ceiling is refused whole, and nothing lands.</summary>
    [Fact]
    public void AnAddOverTheCeilingIsRefusedAndNothingIsWritten()
    {
        string path = Empty();

        GraphException refused = Assert.Throws<GraphException>(
            () => ThreadItems.Add(path, "a long one", notes: Chars(NotesBudget.Default + 1)));

        Assert.Contains("'a long one'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("8,001 characters", refused.Message, StringComparison.Ordinal);
        Assert.Contains("ceiling is 8,000", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, ThreadItems.Show(path, all: true).Count);
    }

    /// <summary>Exactly at the ceiling is inside it. The bound is on what is over.</summary>
    [Fact]
    public void AnAddAtTheCeilingLands()
    {
        string path = Empty();

        ThreadItems.Add(path, "at the line", notes: Chars(NotesBudget.Default));

        Assert.Equal(NotesBudget.Default, Assert.Single(ThreadItems.Show(path).Items).NotesLength);
    }

    [Fact]
    public void ReplacingNotesWithSomethingOverTheCeilingIsRefused()
    {
        string path = Empty();
        ThreadItems.Add(path, "cache eviction", notes: "short");

        GraphException refused = Assert.Throws<GraphException>(() =>
            ThreadItems.Update(path, Topic("cache eviction"), notes: Chars(NotesBudget.Default + 1)));

        Assert.Contains("'cache eviction'", refused.Message, StringComparison.Ordinal);
        Assert.Equal("short", Assert.Single(ThreadItems.Show(path, topic: "cache eviction", full: true).Items).Notes);
    }

    /// <summary>
    /// An append is measured on the RESULT, not the fragment.
    /// </summary>
    /// <remarks>
    /// The same 20-character append is refused when it would cross the line and lands when it
    /// would not, which is what pins that the result is what was measured: a check on the
    /// fragment alone would pass both.
    /// </remarks>
    [Fact]
    public void AnAppendIsMeasuredOnTheResultNotTheFragment()
    {
        string path = Empty();
        string existing = Chars(NotesBudget.Default - 10);
        ThreadItems.Add(path, "cache eviction", notes: existing);

        // 7,990 + "\n\n" + 20 = 8,012: over.
        GraphException refused = Assert.Throws<GraphException>(() =>
            ThreadItems.Update(path, Topic("cache eviction"), notes: Chars(20, 'y'), appendNotes: true));

        Assert.Contains("8,012 characters", refused.Message, StringComparison.Ordinal);

        // 7,990 + "\n\n" + 8 = 8,000: at the line, and in.
        ThreadItems.Update(path, Topic("cache eviction"), notes: Chars(8, 'y'), appendNotes: true);

        ThreadShownItem item = Assert.Single(ThreadItems.Show(path, topic: "cache eviction", full: true).Items);

        Assert.Equal(existing + "\n\n" + Chars(8, 'y'), item.Notes);
        Assert.Equal(NotesBudget.Default, item.NotesLength);
    }

    /// <summary>The resume cursor has its own, fixed ceiling, on add and on update alike.</summary>
    [Fact]
    public void NextOverItsOwnCeilingIsRefused()
    {
        string path = Empty();

        GraphException onAdd = Assert.Throws<GraphException>(
            () => ThreadItems.Add(path, "cache eviction", next: Chars(NotesBudget.NextCeiling + 1)));

        Assert.Contains("next for 'cache eviction'", onAdd.Message, StringComparison.Ordinal);
        Assert.Contains("1,001 characters", onAdd.Message, StringComparison.Ordinal);
        Assert.Contains("ceiling is 1,000", onAdd.Message, StringComparison.Ordinal);

        ThreadItems.Add(path, "cache eviction", next: Chars(NotesBudget.NextCeiling));

        Assert.Throws<GraphException>(() =>
            ThreadItems.Update(path, Topic("cache eviction"), next: Chars(NotesBudget.NextCeiling + 1)));

        Assert.Equal(NotesBudget.NextCeiling, Assert.Single(ThreadItems.Show(path).Items).Next.Length);
    }

    /// <summary>
    /// An item already over the ceiling stays amendable in everything but its notes.
    /// </summary>
    /// <remarks>
    /// Such items exist: the live list held several when the ceiling arrived. A ceiling that
    /// measured the item rather than the write would freeze exactly the items it was meant to
    /// shrink -- and would block the escape, which is a notes write that REPLACES the long text
    /// with something shorter.
    /// </remarks>
    [Fact]
    public void WritesThatLeaveNotesAloneSucceedOnAnItemAlreadyOverTheCeiling()
    {
        string path = Empty();
        string oversized = Chars(NotesBudget.Default + 500);

        File.WriteAllText(path, new JsonArray(new JsonObject
        {
            ["topic"] = "grandfathered",
            ["status"] = "parked",
            ["refs"] = new JsonArray(),
            ["next"] = "",
            ["notes"] = oversized,
        }).ToJsonString());

        // One field per call: a known, separate bug applies only the status when notes, next
        // and status arrive together.
        ThreadItems.Update(path, Topic("grandfathered"), status: ThreadItems.Active);
        ThreadItems.Update(path, Topic("grandfathered"), next: "a short cursor");
        ThreadItems.Update(path, Topic("grandfathered"), refs: ["note.one"]);
        ThreadItems.Update(path, Topic("grandfathered"), area: "JanetHome");

        ThreadShownItem item = Assert.Single(ThreadItems.Show(path, topic: "grandfathered", full: true).Items);

        Assert.Equal(ThreadItems.Active, item.Status);
        Assert.Equal("a short cursor", item.Next);
        Assert.Equal(["note.one"], item.Refs);
        Assert.Equal("JanetHome", item.Area);
        Assert.Equal(oversized, item.Notes);

        // And the escape itself: replacing the notes with a shorter working log is a notes
        // write on an over-ceiling item, and it lands because the RESULT is what is measured.
        ThreadItems.Update(path, Topic("grandfathered"), notes: "moved to note.grandfathered; see refs");

        Assert.Equal(
            "moved to note.grandfathered; see refs",
            Assert.Single(ThreadItems.Show(path, topic: "grandfathered", full: true).Items).Notes);
    }

    /// <summary>The refusal says where long-form goes, not only that it does not go here.</summary>
    [Fact]
    public void TheRefusalNamesTheEscape()
    {
        string message = Assert.Throws<GraphException>(
            () => ThreadItems.Add(Empty(), "a long one", notes: Chars(NotesBudget.Default + 1))).Message;

        Assert.Contains("catalogued note", message, StringComparison.Ordinal);
        Assert.Contains("notes\\<slug>.md", message, StringComparison.Ordinal);
        Assert.Contains("`janet research add` it as note.<slug>", message, StringComparison.Ordinal);
        Assert.Contains("put the id in refs", message, StringComparison.Ordinal);
        Assert.Contains("replace notes with a shorter working log", message, StringComparison.Ordinal);
        Assert.Contains(NotesBudget.EnvironmentVariable, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The override governs the WRITE, not merely the number that can be read back.
    /// </summary>
    /// <remarks>
    /// The consumer test, in the sense the operating rules mean it: a test that NotesBudget.Current
    /// reads 50 would pass with the ceiling check deleted. This one sets the override to 50,
    /// proves a 60-character write is refused with that number in the message, and proves a
    /// 40-character one lands -- so it fails if the write path stops consulting the override,
    /// and fails if the check is removed.
    /// </remarks>
    [Fact]
    public void TheOverrideGovernsTheWrite()
    {
        string path = Empty();
        string? saved = Environment.GetEnvironmentVariable(NotesBudget.EnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(NotesBudget.EnvironmentVariable, "50");

            GraphException refused = Assert.Throws<GraphException>(
                () => ThreadItems.Add(path, "sixty", notes: Chars(60)));

            Assert.Contains("60 characters", refused.Message, StringComparison.Ordinal);
            Assert.Contains("ceiling is 50", refused.Message, StringComparison.Ordinal);

            ThreadItems.Add(path, "forty", notes: Chars(40));

            Assert.Equal("forty", Assert.Single(ThreadItems.Show(path).Items).Topic);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NotesBudget.EnvironmentVariable, saved);
        }
    }

    /// <summary>Anything that is not a positive integer is the default, never unbounded or zero.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("8e3")]
    [InlineData("8,000")]
    public void ANonPositiveOverrideMeansTheDefault(string? raw) =>
        Assert.Equal(NotesBudget.Default, NotesBudget.Resolve(raw));

    // ---- lead by default on show -------------------------------------------------------

    /// <summary>A list with one long, multi-line item and one short one.</summary>
    private string Mixed()
    {
        string path = Empty();

        ThreadItems.Add(path, "long story", notes: "\n\nThe lead line.\n\n" + Chars(3_000) + "\nlast line", area: "JanetHome");
        ThreadItems.Add(path, "one liner", notes: "Ruled out the TTL.", area: "JanetHome");

        return path;
    }

    /// <summary>
    /// By default each item carries the lead the report carries, the stored size, and whether
    /// the lead is all there is.
    /// </summary>
    [Fact]
    public void ShowCarriesTheLeadTheSizeAndTheFlagByDefault()
    {
        string path = Mixed();
        string stored = "\n\nThe lead line.\n\n" + Chars(3_000) + "\nlast line";

        ThreadShowResult shown = ThreadItems.Show(path);

        ThreadShownItem longStory = shown.Items.Single(i => i.Topic == "long story");
        ThreadShownItem oneLiner = shown.Items.Single(i => i.Topic == "one liner");

        Assert.Equal("The lead line.", longStory.Notes);
        Assert.Equal(ThreadItems.Lead(stored), longStory.Notes);
        Assert.Equal(stored.Length, longStory.NotesLength);
        Assert.True(longStory.NotesTruncated);

        Assert.Equal("Ruled out the TTL.", oneLiner.Notes);
        Assert.Equal("Ruled out the TTL.".Length, oneLiner.NotesLength);
        Assert.False(oneLiner.NotesTruncated);

        // And the same lead the report carries, so the two views cannot disagree.
        Assert.Equal(
            ThreadItems.Report(path, topic: "long story").Items.Single().NotesLead,
            longStory.Notes);
    }

    /// <summary>
    /// In the envelope, the flag is a key that is present only when true; the size is always
    /// there.
    /// </summary>
    [Fact]
    public void TheEnvelopeWritesTheFlagOnlyWhenTheLeadIsNotTheWholeText()
    {
        JsonObject envelope = JsonNode.Parse(ThreadJson.Serialize(ThreadItems.Show(Mixed())))!.AsObject();
        JsonArray items = envelope["items"]!.AsArray();

        JsonObject longStory = items.OfType<JsonObject>().Single(i => i["topic"]!.GetValue<string>() == "long story");
        JsonObject oneLiner = items.OfType<JsonObject>().Single(i => i["topic"]!.GetValue<string>() == "one liner");

        Assert.True(longStory["notesTruncated"]!.GetValue<bool>());
        Assert.True(longStory.ContainsKey("notesLength"));
        Assert.False(oneLiner.ContainsKey("notesTruncated"));
        Assert.Equal("Ruled out the TTL.".Length, oneLiner["notesLength"]!.GetValue<int>());
    }

    [Fact]
    public void FullWithATopicReturnsTheWholeNotes()
    {
        string path = Mixed();
        string stored = "\n\nThe lead line.\n\n" + Chars(3_000) + "\nlast line";

        ThreadShownItem item = Assert.Single(ThreadItems.Show(path, topic: "long story", full: true).Items);

        Assert.Equal(stored, item.Notes);
        Assert.Equal(stored.Length, item.NotesLength);
        Assert.False(item.NotesTruncated);
    }

    /// <summary>
    /// full is refused with an area or no selector at all, and says why.
    /// </summary>
    /// <remarks>
    /// The invariant note.thread-item-projection records: notes are returned one item at a
    /// time, and a flag that expands notes across a set re-creates the original defect exactly.
    /// Refused before the file is read, so it holds for an empty list too.
    /// </remarks>
    [Fact]
    public void FullWithoutASingleItemSelectorIsRefused()
    {
        string path = Mixed();

        GraphException withArea = Assert.Throws<GraphException>(
            () => ThreadItems.Show(path, area: "JanetHome", full: true));

        GraphException unnarrowed = Assert.Throws<GraphException>(
            () => ThreadItems.Show(path, full: true));

        Assert.Contains("one item at a time", withArea.Message, StringComparison.Ordinal);
        Assert.Contains("pass topic", withArea.Message, StringComparison.Ordinal);
        Assert.Contains("note.thread-item-projection", unnarrowed.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (string directory in _directories.Where(Directory.Exists))
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
        }

        GC.SuppressFinalize(this);
    }
}
