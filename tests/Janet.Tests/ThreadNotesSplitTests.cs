using System.Globalization;
using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Automatic archiving: an append that would cross the notes ceiling moves the OLDEST
/// paragraphs to a file beside the store, keeps the newest half live behind a pointer line, and
/// reports what it did in the write envelope.
/// </summary>
/// <remarks>
/// Every test here runs against its own store under a fresh temp directory, and therefore
/// against its own archive directory: the archives follow the store, not the repo, so a test
/// that passes its own path can never write beside the live list.
///
/// In the "thread store" collection because several of these set JANET_NOTES_BUDGET or
/// JANET_NOTES_AUTOSPLIT on the process, and one sets WriteQueue.LockTimeout, all of which are
/// global. xUnit runs classes in parallel unless they share a collection, and a write in
/// another class racing one of those windows would fail for a reason its own file never
/// mentions.
/// </remarks>
[Collection("thread store")]
public class ThreadNotesSplitTests : IDisposable
{
    private readonly List<string> _directories = [];

    private string Empty()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "janet-notes-split", Guid.NewGuid().ToString("n")[..8]);

        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        return Path.Combine(directory, "thread-stack.json");
    }

    private static ThreadSelector Topic(string topic) => new() { Topic = topic };

    /// <summary>One block, self-identifying so a test can say which half of the text it is in.</summary>
    private static string Block(int index, int size) =>
        "P" + index.ToString("D3", CultureInfo.InvariantCulture) + " " +
        new string((char)('a' + (index % 26)), size - 5);

    /// <summary>Blocks joined the way Update joins appends: by a blank line.</summary>
    private static string Blocks(int count, int size) =>
        string.Join("\n\n", Enumerable.Range(0, count).Select(i => Block(i, size)));

    /// <summary>An item of fifteen 500-character paragraphs: 7,528 characters, under the ceiling.</summary>
    private string Splittable(string topic = "a splittable item")
    {
        string path = Empty();
        ThreadItems.Add(path, topic, notes: Blocks(15, 500), next: "resume here", area: "JanetHome");

        return path;
    }

    private static string Notes(string path, string topic) =>
        Assert.Single(ThreadItems.Show(path, topic: topic, full: true).Items).Notes;

    // ---- the archive itself ------------------------------------------------------------

    /// <summary>
    /// An append that crosses the ceiling archives the oldest paragraphs, leaves the newest
    /// live, and the item keeps its own topic.
    /// </summary>
    /// <remarks>
    /// The three things a caller depends on at once: the write LANDS where it used to be
    /// refused, the text is not lost, and nothing about how the item is selected changed. The
    /// last matters because the archive is a file rather than a second thread item -- a sibling
    /// item sharing any fragment of the topic would make the original ambiguous to the
    /// substring selector, and unaddressable by its own name.
    /// </remarks>
    [Fact]
    public void AnAppendOverTheCeilingArchivesTheOldestAndKeepsTheNewestLive()
    {
        string path = Splittable();
        string fragment = Block(99, 600);

        ThreadUpdateResult result = ThreadItems.Update(
            path, Topic("splittable"), notes: fragment, appendNotes: true);

        NotesArchive split = Assert.IsType<NotesArchive>(result.Split);

        Assert.Equal("file", split.Kind);
        Assert.Equal("paragraph", split.Boundary);
        Assert.Equal(1, split.Generation);
        Assert.True(File.Exists(split.Where), $"the archive file {split.Where} was not written");
        Assert.True(
            split.Retained <= NotesBudget.CurrentRetention,
            $"retained {split.Retained} is over the retention target {NotesBudget.CurrentRetention}");

        string live = Notes(path, "splittable");
        string archived = File.ReadAllText(split.Where);

        Assert.Equal(split.Retained, live.Length);
        Assert.EndsWith(fragment, live, StringComparison.Ordinal);

        // The oldest went, the newest stayed, and each is in exactly one of the two places.
        Assert.Contains("P000", archived, StringComparison.Ordinal);
        Assert.DoesNotContain("P000", live, StringComparison.Ordinal);
        Assert.Contains("P014", live, StringComparison.Ordinal);
        Assert.DoesNotContain("P014", archived, StringComparison.Ordinal);

        // Still the same item, selected the same way, with its other fields where they were.
        ThreadShownItem item = Assert.Single(
            ThreadItems.Show(path, topic: "a splittable item", full: true).Items);

        Assert.Equal("a splittable item", item.Topic);
        Assert.Equal("resume here", item.Next);
        Assert.Equal("JanetHome", item.Area);
        Assert.Equal(["notes"], result.Changed);
    }

    /// <summary>
    /// The pointer header is the first line of what is left, so it is the lead every report and
    /// the startup brief show.
    /// </summary>
    /// <remarks>
    /// This is the whole answer to a caller that never reads the write envelope: the path to the
    /// archived text arrives in the ordinary reading verbs, at the top of the item, without a
    /// new verb to learn.
    /// </remarks>
    [Fact]
    public void ThePointerHeaderIsTheFirstLineAndBecomesTheReportsLead()
    {
        string path = Splittable();

        NotesArchive split = Assert.IsType<NotesArchive>(ThreadItems.Update(
            path, Topic("splittable"), notes: Block(99, 600), appendNotes: true).Split);

        string live = Notes(path, "splittable");

        Assert.StartsWith(split.Header, live, StringComparison.Ordinal);
        Assert.Equal(split.Header, live.Split('\n')[0]);
        Assert.Contains(split.Where, split.Header, StringComparison.Ordinal);

        string? lead = ThreadItems.Report(path, topic: "splittable").Items.Single().NotesLead;

        Assert.Equal(ThreadItems.Lead(live), lead);
        Assert.StartsWith("ARCHIVED ", lead, StringComparison.Ordinal);
    }

    /// <summary>The envelope carries the whole of the machine-readable answer, and only when it happened.</summary>
    [Fact]
    public void TheEnvelopeCarriesSplitOnlyWhenAnArchiveHappened()
    {
        string path = Splittable();

        JsonObject ordinary = JsonNode.Parse(ThreadJson.Serialize(
            ThreadItems.Update(path, Topic("splittable"), next: "somewhere else")))!.AsObject();

        Assert.False(ordinary.ContainsKey("split"));

        ThreadUpdateResult result = ThreadItems.Update(
            path, Topic("splittable"), notes: Block(99, 600), appendNotes: true);

        JsonObject archived = JsonNode.Parse(ThreadJson.Serialize(result))!.AsObject();
        JsonObject split = archived["split"]!.AsObject();

        Assert.Equal(
            ["archived", "retained", "boundary", "paragraphs", "kind", "where", "generation", "header"],
            split.Select(f => f.Key));

        Assert.Equal(result.Split!.Archived, split["archived"]!.GetValue<int>());
        Assert.Equal(result.Split.Where, split["where"]!.GetValue<string>());
        Assert.Equal(Notes(path, "splittable").Split('\n')[0], split["header"]!.GetValue<string>());

        // 'changed' is not overloaded: it says what it always said.
        Assert.Equal(["notes"], archived["changed"]!.AsArray().Select(c => c!.GetValue<string>()));
    }

    // ---- what is still refused ---------------------------------------------------------

    /// <summary>
    /// Notes that are ONE block are refused exactly as they were before archiving existed.
    /// </summary>
    /// <remarks>
    /// The only boundary such an item has is the blank line this very append created, and
    /// cutting there would archive the whole stored note on the strength of the caller's own
    /// text. A paragraph is one indivisible unit of prose and the tool has no business guessing
    /// where a thought ends.
    /// </remarks>
    [Fact]
    public void ASingleBlockIsStillRefusedWholeAndNothingIsArchived()
    {
        string path = Empty();
        string one = new('x', 7_900);
        ThreadItems.Add(path, "one long block", notes: one);

        GraphException refused = Assert.Throws<GraphException>(() => ThreadItems.Update(
            path, Topic("one long block"), notes: new string('y', 200), appendNotes: true));

        Assert.Contains("ceiling is 8,000", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", refused.Message, StringComparison.Ordinal);
        Assert.Contains(
            "one block of 7,900 characters", refused.Message, StringComparison.Ordinal);

        Assert.Equal(one, Notes(path, "one long block"));
        Assert.False(Directory.Exists(NotesSplit.ArchiveDirectory(path)));
    }

    /// <summary>
    /// A fragment the caller sent in this call is never cut, so an append with no room left is
    /// refused rather than split.
    /// </summary>
    [Fact]
    public void AnAppendWhoseOwnTextLeavesNoRoomIsRefused()
    {
        string path = Empty();
        string stored = Blocks(10, 300);
        ThreadItems.Add(path, "ten short blocks", notes: stored);

        GraphException refused = Assert.Throws<GraphException>(() => ThreadItems.Update(
            path, Topic("ten short blocks"), notes: new string('y', 5_000), appendNotes: true));

        Assert.Contains("retention target", refused.Message, StringComparison.Ordinal);
        Assert.Contains("5,000 characters you sent", refused.Message, StringComparison.Ordinal);
        Assert.Equal(stored, Notes(path, "ten short blocks"));
        Assert.False(Directory.Exists(NotesSplit.ArchiveDirectory(path)));
    }

    /// <summary>
    /// A REPLACEMENT over the ceiling still refuses: archiving half of what the caller just
    /// wrote, in the same call, is the tool overruling them.
    /// </summary>
    [Fact]
    public void AReplacementOverTheCeilingStillRefusesOnASplittableItem()
    {
        string path = Splittable();

        Assert.Throws<GraphException>(() => ThreadItems.Update(
            path, Topic("splittable"), notes: Blocks(30, 500)));

        Assert.False(Directory.Exists(NotesSplit.ArchiveDirectory(path)));
    }

    /// <summary>An add has no past to archive, so it is refused as it always was.</summary>
    [Fact]
    public void AnAddOverTheCeilingStillRefuses()
    {
        string path = Empty();

        Assert.Throws<GraphException>(
            () => ThreadItems.Add(path, "born too big", notes: Blocks(30, 500)));

        Assert.False(Directory.Exists(NotesSplit.ArchiveDirectory(path)));
    }

    // ---- the opt-out -------------------------------------------------------------------

    /// <summary>split=false restores the old refusal, and the refusal names the flag.</summary>
    [Fact]
    public void SplitFalseRefusesAsBeforeAndNamesWhatWasTurnedOff()
    {
        string path = Splittable();
        string stored = Notes(path, "splittable");

        GraphException refused = Assert.Throws<GraphException>(() => ThreadItems.Update(
            path, Topic("splittable"), notes: Block(99, 600), appendNotes: true, split: false));

        Assert.Contains("ceiling is 8,000", refused.Message, StringComparison.Ordinal);
        Assert.Contains("split=false", refused.Message, StringComparison.Ordinal);
        Assert.Contains("JANET_NOTES_AUTOSPLIT=off", refused.Message, StringComparison.Ordinal);
        Assert.Equal(stored, Notes(path, "splittable"));
        Assert.False(Directory.Exists(NotesSplit.ArchiveDirectory(path)));
    }

    /// <summary>The process-level kill switch governs the WRITE, not merely what can be read back.</summary>
    [Fact]
    public void TheEnvironmentKillSwitchRefusesTheSameWrite()
    {
        string path = Splittable();
        string? saved = Environment.GetEnvironmentVariable(NotesBudget.AutosplitVariable);

        try
        {
            Environment.SetEnvironmentVariable(NotesBudget.AutosplitVariable, "off");

            GraphException refused = Assert.Throws<GraphException>(() => ThreadItems.Update(
                path, Topic("splittable"), notes: Block(99, 600), appendNotes: true));

            Assert.Contains("Automatic archiving", refused.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(NotesSplit.ArchiveDirectory(path)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(NotesBudget.AutosplitVariable, saved);
        }

        // And with it back on, the identical call lands.
        Assert.NotNull(ThreadItems.Update(
            path, Topic("splittable"), notes: Block(99, 600), appendNotes: true).Split);
    }

    /// <summary>Anything but an explicit off spelling leaves archiving on.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("on", true)]
    [InlineData("yes", true)]
    [InlineData("offf", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData(" off ", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    public void TheAutosplitSwitchOnlyReadsAnExplicitOff(string? raw, bool expected) =>
        Assert.Equal(expected, NotesBudget.ResolveAutosplit(raw));

    // ---- the chain ---------------------------------------------------------------------

    /// <summary>
    /// The second and third archive of one item write their own files, and each names the one
    /// before it.
    /// </summary>
    /// <remarks>
    /// The name is content-addressed -- slug, date, and eight hex of a SHA-256 over the archived
    /// text -- so there is no ordinal to probe and no collision to lose. The chain is what makes
    /// the older text reachable at all: the live notes name the newest archive, and each archive
    /// names its predecessor, so it is walkable backwards with a file read.
    /// </remarks>
    [Fact]
    public void TheSecondAndThirdArchivesDoNotCollideAndEachNamesItsPredecessor()
    {
        string path = Splittable();

        NotesArchive first = FillUntilArchived(path);
        NotesArchive second = FillUntilArchived(path);
        NotesArchive third = FillUntilArchived(path);

        Assert.Equal(1, first.Generation);
        Assert.Equal(2, second.Generation);
        Assert.Equal(3, third.Generation);

        Assert.Equal(3, new HashSet<string>([first.Where, second.Where, third.Where]).Count);
        Assert.Equal(3, Directory.GetFiles(NotesSplit.ArchiveDirectory(path), "*.md").Length);

        Assert.DoesNotContain("Previous archive:", File.ReadAllText(first.Where), StringComparison.Ordinal);
        Assert.Contains("Previous archive: " + first.Where, File.ReadAllText(second.Where), StringComparison.Ordinal);
        Assert.Contains("Previous archive: " + second.Where, File.ReadAllText(third.Where), StringComparison.Ordinal);

        // And the live item still points at the newest of the three.
        Assert.Equal(third.Where, NotesSplit.PointerIn(Notes(path, "splittable")));
    }

    /// <summary>Appends 600-character paragraphs until one of them archives, and reports it.</summary>
    private static NotesArchive FillUntilArchived(string path)
    {
        for (int i = 0; i < 40; i++)
        {
            ThreadUpdateResult result = ThreadItems.Update(
                path, Topic("splittable"), notes: Block(i, 600), appendNotes: true);

            if (result.Split is NotesArchive archived)
            {
                return archived;
            }
        }

        throw new InvalidOperationException("40 appends of 600 characters did not cross the ceiling");
    }

    // ---- the two failure paths ---------------------------------------------------------

    /// <summary>
    /// A file write that fails queues nothing: the caller hears about it and the item is exactly
    /// as it was.
    /// </summary>
    /// <remarks>
    /// The first half of what this design guarantees in place of a two-phase commit. The archive
    /// file is written BEFORE the write is queued, so a failure there has nothing to undo.
    /// </remarks>
    [Fact]
    public void AFailedArchiveWriteQueuesNothingAndLeavesTheItemAlone()
    {
        string path = Splittable();
        string stored = Notes(path, "splittable");

        // A FILE where the archive directory has to be, so creating it cannot succeed.
        File.WriteAllText(NotesSplit.ArchiveDirectory(path), "not a directory");

        GraphException failed = Assert.Throws<GraphException>(() => ThreadItems.Update(
            path, Topic("splittable"), notes: Block(99, 600), appendNotes: true));

        Assert.Contains("Could not write the notes archive", failed.Message, StringComparison.Ordinal);
        Assert.Contains("the notes are exactly as they were", failed.Message, StringComparison.Ordinal);
        Assert.Equal(stored, Notes(path, "splittable"));
    }

    /// <summary>
    /// A queued write that fails leaves an orphan archive file and an untouched item -- never a
    /// half-archive.
    /// </summary>
    /// <remarks>
    /// The second half, and the accepted price of not being a two-phase commit: the residue is a
    /// file with no pointer, which costs disk and nothing else, and which a retry re-creates
    /// byte-identically because the name is a hash of its contents. No text is lost either way.
    /// </remarks>
    [Fact]
    public void AFailedQueuedWriteLeavesAnOrphanFileAndAnUntouchedItem()
    {
        string path = Splittable();
        string stored = Notes(path, "splittable");

        try
        {
            // The store is readable, so the plan is made and the archive written; the atomic
            // rename at the end of the batch is what cannot land.
            File.SetAttributes(path, FileAttributes.ReadOnly);

            Exception failed = Assert.ThrowsAny<Exception>(() => ThreadItems.Update(
                path, Topic("splittable"), notes: Block(99, 600), appendNotes: true));

            Assert.True(
                failed is UnauthorizedAccessException or IOException,
                $"expected the write itself to fail, got {failed.GetType().Name}: {failed.Message}");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        // The archive was written -- it is prepared first, deliberately -- and nothing points at
        // it, which is the orphan this design accepts.
        string orphan = Assert.Single(Directory.GetFiles(NotesSplit.ArchiveDirectory(path), "*.md"));

        Assert.Contains("P000", File.ReadAllText(orphan), StringComparison.Ordinal);
        Assert.Equal(stored, Notes(path, "splittable"));
        Assert.Null(NotesSplit.PointerIn(stored));
    }

    // ---- the ceiling's purpose ---------------------------------------------------------

    /// <summary>
    /// The retention target moves with the override, and it is HALF the ceiling rather than the
    /// ceiling.
    /// </summary>
    /// <remarks>
    /// The consumer test for the target: cutting back to one character under the ceiling means
    /// the next append archives again, and then again, which is ten files where one was wanted.
    /// This sets the ceiling to 4,000, so the retention is 2,000, and proves the item came back
    /// under 2,000 rather than merely under 4,000.
    /// </remarks>
    [Fact]
    public void TheRetainedTextIsHalfTheCeilingAndTheOverrideGovernsIt()
    {
        string path = Empty();
        string? saved = Environment.GetEnvironmentVariable(NotesBudget.EnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(NotesBudget.EnvironmentVariable, "4000");
            ThreadItems.Add(path, "a smaller ceiling", notes: Blocks(26, 150));

            NotesArchive split = Assert.IsType<NotesArchive>(ThreadItems.Update(
                path, Topic("a smaller ceiling"), notes: Block(99, 150), appendNotes: true).Split);

            // The consumer assertion first: what the item actually holds, not what the target
            // reads back as. A test of the number alone would pass with the cut ignoring it.
            Assert.True(
                split.Retained <= 2_000,
                $"retained {split.Retained} is over the 2,000-character retention target");

            Assert.Equal(split.Retained, Notes(path, "a smaller ceiling").Length);
            Assert.Equal(2_000, NotesBudget.CurrentRetention);
        }
        finally
        {
            Environment.SetEnvironmentVariable(NotesBudget.EnvironmentVariable, saved);
        }
    }

    /// <summary>
    /// Archived text is moved verbatim and never re-scanned by the malformed-markup guard.
    /// </summary>
    /// <remarks>
    /// Items whose notes quote the tool-call markup that guard refuses exist -- the item
    /// tracking the guard among them -- and an archive that re-scanned what it moved would make
    /// exactly those items unamendable. The guard scans the fragment the caller sent, as it
    /// always has, and this append's fragment is clean.
    /// </remarks>
    [Fact]
    public void ArchivedTextIsMovedVerbatimAndNeverRescanned()
    {
        string path = Empty();
        string mangled = Blocks(7, 500) + "\n\n</invoke> the markup this item is about";

        // Written round the guard, exactly as such an item came to exist in the live list.
        File.WriteAllText(path, new JsonArray(new JsonObject
        {
            ["topic"] = "the markup guard",
            ["status"] = "parked",
            ["refs"] = new JsonArray(),
            ["next"] = "",
            ["notes"] = mangled + "\n\n" + Blocks(9, 500),
        }).ToJsonString());

        NotesArchive split = Assert.IsType<NotesArchive>(ThreadItems.Update(
            path, Topic("the markup guard"), notes: Block(99, 600), appendNotes: true).Split);

        Assert.Contains("</invoke>", File.ReadAllText(split.Where), StringComparison.Ordinal);
    }

    /// <summary>Focus survives an archive: an active item stays active across one.</summary>
    [Fact]
    public void FocusAndTheOtherFieldsSurviveAnArchive()
    {
        string path = Splittable();
        ThreadItems.Update(path, Topic("splittable"), status: ThreadItems.Active);
        ThreadItems.Update(path, Topic("splittable"), refs: ["note.one"]);

        Assert.NotNull(ThreadItems.Update(
            path, Topic("splittable"), notes: Block(99, 600), appendNotes: true).Split);

        ThreadShownItem item = Assert.Single(
            ThreadItems.Show(path, topic: "splittable", full: true).Items);

        Assert.Equal(ThreadItems.Active, item.Status);
        Assert.Equal("resume here", item.Next);
        Assert.Equal(["note.one"], item.Refs);
        Assert.Equal("JanetHome", item.Area);
    }

    /// <summary>The file name is the slug, the date and a hash of the text it holds.</summary>
    [Fact]
    public void TheArchiveNameIsContentAddressed()
    {
        string path = Splittable();

        NotesArchive split = Assert.IsType<NotesArchive>(ThreadItems.Update(
            path, Topic("splittable"), notes: Block(99, 600), appendNotes: true).Split);

        string archived = File.ReadAllText(split.Where);
        string moved = archived[(archived.IndexOf("\n\n", StringComparison.Ordinal) + 2)..].TrimEnd('\n');

        Assert.Equal(split.Archived, moved.Length);
        Assert.Equal(
            NotesSplit.Slug("a splittable item") + "-" +
            DateOnly.FromDateTime(DateTime.Now).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" +
            NotesSplit.Hash(moved) + ".md",
            Path.GetFileName(split.Where));
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
