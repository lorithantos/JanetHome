using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The thread-item list: the behaviours the PowerShell had, asserted against the port.
/// </summary>
/// <remarks>
/// This list is the one piece of Janet state that has already been destroyed once -- an
/// unlocked read-modify-write took a session's notes on 2026-08-08 -- so the tests that matter
/// most here are the ones about not losing anything: nothing is removed, a completion is a
/// status change, an ambiguous selector refuses rather than guessing, and concurrent writers
/// all survive.
/// </remarks>
public class ThreadItemTests : IDisposable
{
    private readonly List<string> _directories = [];

    /// <summary>A list with one of everything the operations have to cope with.</summary>
    private string Seeded()
    {
        string path = Empty();

        ThreadItems.Add(path, "cache eviction", next: "query the telemetry table", refs: ["note.cache"]);
        ThreadItems.Add(path, "cache warming", notes: "not started");
        ThreadItems.Add(path, "chase the AV", active: true);
        ThreadItems.Add(path, "finished thing");
        ThreadItems.Complete(path, new ThreadSelector { Topic = "finished thing" });

        return path;
    }

    private string Empty()
    {
        string directory = Path.Combine(Path.GetTempPath(), "janet-threads", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        return Path.Combine(directory, "thread-stack.json");
    }

    // ---- the shape of the file ---------------------------------------------------------

    /// <summary>A list that has never been written is empty, not missing. Nothing has happened yet.</summary>
    [Fact]
    public void AnAbsentListReadsAsEmpty()
    {
        ThreadShowResult shown = ThreadItems.Show(Empty());

        Assert.Empty(shown.Items);
        Assert.Null(shown.Active);
        Assert.Null(shown.Error);
    }

    /// <summary>
    /// The old { topic, status, notes } form still reads, and reads as the five-field shape.
    /// </summary>
    /// <remarks>
    /// Migration is therefore a read followed by a write, with no transcription of note bodies
    /// -- so no note body can be mangled by the migration itself.
    /// </remarks>
    [Fact]
    public void TheOlderThreeFieldShapeStillReads()
    {
        string path = Empty();
        File.WriteAllText(path, """[{"topic":"old shape","status":"active","notes":"kept"}]""");

        ThreadItem item = Assert.Single(ThreadItems.Show(path).Items);

        Assert.Equal("old shape", item.Topic);
        Assert.Equal("kept", item.Notes);
        Assert.Empty(item.Refs);
        Assert.Equal(string.Empty, item.Next);
    }

    /// <summary>An item with no status is parked, not active. Recording work does not claim focus.</summary>
    [Fact]
    public void AnItemWithNoStatusDefaultsToParked()
    {
        string path = Empty();
        File.WriteAllText(path, """[{"topic":"no status"}]""");

        Assert.Equal(ThreadItems.Parked, Assert.Single(ThreadItems.Show(path).Items).Status);
    }

    // ---- adding ------------------------------------------------------------------------

    /// <summary>
    /// Adding does not displace what is active. That separation is the whole reason this is a
    /// list and not the push/pop stack it replaced.
    /// </summary>
    [Fact]
    public void AddingDoesNotStealFocus()
    {
        string path = Seeded();

        ThreadAddResult result = ThreadItems.Add(path, "something noticed in passing");

        Assert.Equal("chase the AV", result.Active);
        Assert.Equal("chase the AV", ThreadItems.Show(path).Active);
    }

    [Fact]
    public void AddingActiveParksThePrevious()
    {
        string path = Seeded();

        ThreadItems.Add(path, "urgent detour", active: true);

        ThreadShowResult shown = ThreadItems.Show(path);

        Assert.Equal("urgent detour", shown.Active);
        Assert.Single(shown.Items, i => i.IsActive);
    }

    [Fact]
    public void AddingADuplicateTopicIsRefused()
    {
        string path = Seeded();

        // Case-insensitively, as the PowerShell's -eq was.
        GraphException refused = Assert.Throws<GraphException>(
            () => ThreadItems.Add(path, "Cache Eviction"));

        Assert.Contains("already exists", refused.Message, StringComparison.Ordinal);
        Assert.Equal(4, ThreadItems.Show(path, all: true).Count);
    }

    /// <summary>The reported count is of live items: done work is not outstanding work.</summary>
    [Fact]
    public void CountsExcludeCompletedItems()
    {
        string path = Seeded();

        ThreadAddResult result = ThreadItems.Add(path, "another");

        Assert.Equal(4, result.Count);
        Assert.Equal(5, ThreadItems.Show(path, all: true).Count);
    }

    // ---- selecting ---------------------------------------------------------------------

    /// <summary>
    /// An ambiguous topic refuses rather than resolving to a first match.
    /// </summary>
    /// <remarks>
    /// The operation that follows rewrites the file, and silently amending the wrong item is
    /// exactly how notes get lost. Both candidates are named, so the caller can disambiguate
    /// without going and reading the list.
    /// </remarks>
    [Fact]
    public void AnAmbiguousTopicIsRefusedAndNamesTheCandidates()
    {
        string path = Seeded();

        GraphException refused = Assert.Throws<GraphException>(() =>
            ThreadItems.Complete(path, new ThreadSelector { Topic = "cache" }));

        Assert.Contains("ambiguous", refused.Message, StringComparison.Ordinal);
        Assert.Contains("cache eviction", refused.Message, StringComparison.Ordinal);
        Assert.Contains("cache warming", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySelectorMeansWhateverIsActive()
    {
        string path = Seeded();

        ThreadCompleteResult result = ThreadItems.Complete(path, new ThreadSelector());

        Assert.Equal("chase the AV", result.Completed);
    }

    [Fact]
    public void WithNothingActiveAnEmptySelectorRefuses()
    {
        string path = Seeded();
        ThreadItems.SetActive(path, null);

        GraphException refused = Assert.Throws<GraphException>(
            () => ThreadItems.Complete(path, new ThreadSelector()));

        Assert.Contains("No item is active", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutOfRangeIndexRefuses() =>
        Assert.Contains(
            "out of range",
            Assert.Throws<GraphException>(
                () => ThreadItems.Complete(Seeded(), new ThreadSelector { Index = 99 })).Message,
            StringComparison.Ordinal);

    // ---- updating ----------------------------------------------------------------------

    /// <summary>
    /// Not supplied and supplied-as-empty are different requests.
    /// </summary>
    /// <remarks>
    /// Clearing the resume cursor is a legitimate thing to want, so an absent argument cannot
    /// mean the same as an empty one. Everything not named is left exactly as it was.
    /// </remarks>
    [Fact]
    public void AnEmptyStringClearsAFieldAndAbsenceLeavesItAlone()
    {
        string path = Seeded();

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, next: "");

        ThreadItem item = ThreadItems.Show(path).Items.Single(i => i.Topic == "cache eviction");

        Assert.Equal(string.Empty, item.Next);
        Assert.Equal(["note.cache"], item.Refs);
    }

    [Fact]
    public void UpdatingWithNothingToChangeRefuses() =>
        Assert.Contains(
            "Nothing to change",
            Assert.Throws<GraphException>(
                () => ThreadItems.Update(Seeded(), new ThreadSelector { Topic = "cache eviction" })).Message,
            StringComparison.Ordinal);

    [Fact]
    public void AppendingNotesKeepsWhatWasThere()
    {
        string path = Seeded();

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache warming" },
            notes: "ruled out threading", appendNotes: true);

        Assert.Equal(
            "not started\n\nruled out threading",
            ThreadItems.Show(path).Items.Single(i => i.Topic == "cache warming").Notes);
    }

    /// <summary>Appending to empty notes does not leave a blank line at the top.</summary>
    [Fact]
    public void AppendingToEmptyNotesJustSetsThem()
    {
        string path = Seeded();

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" },
            notes: "first thing", appendNotes: true);

        Assert.Equal(
            "first thing",
            ThreadItems.Show(path).Items.Single(i => i.Topic == "cache eviction").Notes);
    }

    /// <summary>
    /// Appended refs are concatenated, not merged.
    /// </summary>
    /// <remarks>
    /// Deliberately unlike catalog tags, which deduplicate: a repeated ref is the caller's to
    /// decide about, and dropping one they meant twice is a silent edit to their notes.
    /// </remarks>
    [Fact]
    public void AppendedRefsAreConcatenatedNotDeduplicated()
    {
        string path = Seeded();

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" },
            refs: ["note.cache", "note.other"], appendRefs: true);

        Assert.Equal(
            ["note.cache", "note.cache", "note.other"],
            ThreadItems.Show(path).Items.Single(i => i.Topic == "cache eviction").Refs);
    }

    [Fact]
    public void SettingStatusToActiveParksTheOther()
    {
        string path = Seeded();

        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, status: ThreadItems.Active);

        ThreadShowResult shown = ThreadItems.Show(path);

        Assert.Equal("cache eviction", shown.Active);
        Assert.Single(shown.Items, i => i.IsActive);
    }

    [Fact]
    public void ChangedFieldsAreReportedInOrder()
    {
        ThreadUpdateResult result = ThreadItems.Update(
            Seeded(), new ThreadSelector { Topic = "cache eviction" },
            notes: "n", next: "x", refs: ["r"], status: ThreadItems.Parked);

        Assert.Equal(["notes", "next", "refs", "status"], result.Changed);
    }

    // ---- completing --------------------------------------------------------------------

    /// <summary>
    /// Completion is a status change, not a removal.
    /// </summary>
    /// <remarks>
    /// The stack this replaced deleted items on completion, so finishing work erased the record
    /// of having done it. Nothing is ever removed from this list.
    /// </remarks>
    [Fact]
    public void CompletingKeepsTheItem()
    {
        string path = Seeded();

        ThreadItems.Complete(path, new ThreadSelector { Topic = "chase the AV" });

        Assert.Equal(4, ThreadItems.Show(path, all: true).Count);
        Assert.Contains(ThreadItems.Show(path, all: true).Items, i => i.Topic == "chase the AV" && i.IsDone);
        Assert.DoesNotContain(ThreadItems.Show(path).Items, i => i.Topic == "chase the AV");
    }

    [Fact]
    public void CompletingTwiceRefuses()
    {
        string path = Seeded();

        Assert.Contains(
            "already completed",
            Assert.Throws<GraphException>(() =>
                ThreadItems.Complete(path, new ThreadSelector { Topic = "finished thing" })).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompletingTheActiveItemLeavesNothingActive()
    {
        string path = Seeded();

        ThreadCompleteResult result = ThreadItems.Complete(path, new ThreadSelector { Topic = "chase the AV" });

        Assert.Null(result.Active);
        Assert.Equal(2, result.Remaining);
    }

    // ---- focus -------------------------------------------------------------------------

    [Fact]
    public void SettingActiveReportsWhatItDisplaced()
    {
        ThreadActiveResult result = ThreadItems.SetActive(
            Seeded(), new ThreadSelector { Topic = "cache eviction" });

        Assert.Equal("cache eviction", result.Active);
        Assert.Equal("chase the AV", result.Previous);
    }

    [Fact]
    public void ClearingFocusLeavesNothingActive()
    {
        string path = Seeded();

        ThreadActiveResult result = ThreadItems.SetActive(path, null);

        Assert.Null(result.Active);
        Assert.Equal("chase the AV", result.Previous);
        Assert.Null(ThreadItems.Show(path).Active);
    }

    [Fact]
    public void ADoneItemCannotBeMadeActiveWithoutReopeningIt() =>
        Assert.Contains(
            "Reopen it",
            Assert.Throws<GraphException>(() =>
                ThreadItems.SetActive(Seeded(), new ThreadSelector { Topic = "finished thing" })).Message,
            StringComparison.Ordinal);

    // ---- the startup path --------------------------------------------------------------

    /// <summary>
    /// A corrupt list is reported, not thrown.
    /// </summary>
    /// <remarks>
    /// Show runs in the startup path. Throwing would let a mangled temp file stop a session
    /// from beginning, which is a far worse outcome than starting without the list.
    /// </remarks>
    [Fact]
    public void ACorruptListIsReportedInBandRatherThanThrown()
    {
        string path = Empty();
        File.WriteAllText(path, "{ this is not json");

        ThreadShowResult shown = ThreadItems.Show(path);

        Assert.NotNull(shown.Error);
        Assert.Empty(shown.Items);
        Assert.Equal(0, shown.Count);
    }

    // ---- concurrency -------------------------------------------------------------------

    /// <summary>
    /// The 2026-08-08 failure, reproduced against the port.
    /// </summary>
    /// <remarks>
    /// An unlocked read-modify-write destroyed a session's notes: every writer read the same
    /// list, appended its own item, and wrote, so all but the last vanished. The write queue is
    /// what makes this pass, and it is the same queue the catalog uses -- the list is no longer
    /// defended by a mechanism every future writer has to remember to take.
    /// </remarks>
    [Fact]
    public void ConcurrentWritersAllSurvive()
    {
        string path = Empty();
        const int writers = 16;

        Parallel.For(0, writers, i => ThreadItems.Add(path, $"topic {i:00}"));

        IReadOnlyList<ThreadItem> items = ThreadItems.Show(path, all: true).Items;

        Assert.Equal(writers, items.Count);

        for (int i = 0; i < writers; i++)
        {
            Assert.Contains(items, item => item.Topic == $"topic {i:00}");
        }
    }

    /// <summary>Concurrent notes on different items must not overwrite each other.</summary>
    [Fact]
    public void ConcurrentUpdatesToDifferentItemsAllSurvive()
    {
        string path = Empty();
        string[] topics = [.. Enumerable.Range(0, 8).Select(i => $"topic {i:00}")];

        foreach (string topic in topics)
        {
            ThreadItems.Add(path, topic);
        }

        Parallel.ForEach(topics, topic =>
            ThreadItems.Update(path, new ThreadSelector { Topic = topic }, notes: $"note for {topic}"));

        IReadOnlyList<ThreadItem> items = ThreadItems.Show(path).Items;

        Assert.All(topics, topic =>
            Assert.Equal($"note for {topic}", items.Single(i => i.Topic == topic).Notes));
    }

    /// <summary>
    /// Focus stays single even when several writers claim it at once.
    /// </summary>
    /// <remarks>
    /// Parking the others and taking focus is a read-modify-write over the whole list, so
    /// racing writers could each park a stale copy and leave two items active -- a list with
    /// two active items is not a worse ordering, it is an invalid state.
    /// </remarks>
    [Fact]
    public void ConcurrentFocusChangesLeaveExactlyOneActive()
    {
        string path = Empty();
        string[] topics = [.. Enumerable.Range(0, 8).Select(i => $"topic {i:00}")];

        foreach (string topic in topics)
        {
            ThreadItems.Add(path, topic);
        }

        Parallel.ForEach(topics, topic =>
            ThreadItems.SetActive(path, new ThreadSelector { Topic = topic }));

        Assert.Single(ThreadItems.Show(path).Items, i => i.IsActive);
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
