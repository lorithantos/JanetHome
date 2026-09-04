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

    /// <summary>
    /// Topic selection does not care where an item sits, which is why selection by position was
    /// removed on 2026-08-14 rather than corrected.
    /// </summary>
    /// <remarks>
    /// The seed puts a completed item ABOVE two live ones, so the displayed list and the stored
    /// list disagree: Show hides 'done first', which put 'beta' first on screen while it stays
    /// second in the file. A position taken off the screen therefore addressed 'gamma'. The
    /// assertions below are the property that survives that skew -- the topic reaches its own
    /// item, and the one next to it is untouched.
    /// </remarks>
    [Fact]
    public void ATopicSelectsTheSameItemWhateverPositionItIsDisplayedAt()
    {
        string path = Empty();

        ThreadItems.Add(path, "done first");
        ThreadItems.Add(path, "beta");
        ThreadItems.Add(path, "gamma");
        ThreadItems.Complete(path, new ThreadSelector { Topic = "done first" });

        Assert.Equal("beta", ThreadItems.Show(path).Items[0].Topic);
        Assert.Equal(1, IndexInFile(path, "beta"));

        ThreadItems.Update(path, new ThreadSelector { Topic = "beta" }, next: "amended by topic");

        IReadOnlyList<ThreadItem> stored = ThreadItems.Parse(File.ReadAllText(path));

        Assert.Equal("amended by topic", stored.Single(i => i.Topic == "beta").Next);
        Assert.Equal(string.Empty, stored.Single(i => i.Topic == "gamma").Next);
    }

    private static int IndexInFile(string path, string topic)
    {
        IReadOnlyList<ThreadItem> stored = ThreadItems.Parse(File.ReadAllText(path));

        return Enumerable.Range(0, stored.Count).Single(i => stored[i].Topic == topic);
    }

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

    // ---- areas, and reading through them -----------------------------------------------

    /// <summary>
    /// A list with areas set, and one item deliberately left unfiled.
    /// </summary>
    /// <remarks>
    /// Unfiled is the ordinary case, not an edge one: the live list carried 62 items and no
    /// areas at all when the field arrived, and none of them were backfilled. A fixture where
    /// everything is labelled would test the easy half.
    /// </remarks>
    private string Filed()
    {
        string path = Empty();

        ThreadItems.Add(path, "cache eviction", notes: "Ruled out the TTL.\nSecond line.", area: "RazorGraph");
        ThreadItems.Add(path, "cache warming", area: "razorgraph");
        ThreadItems.Add(path, "the startup brief", area: "JanetHome", active: true);
        ThreadItems.Add(path, "something noticed in passing");

        return path;
    }

    /// <summary>A topic returns that one item, with everything it holds.</summary>
    /// <remarks>
    /// Notes in full, which is the point of asking for one item rather than the list: the
    /// report exists to answer "where was I" cheaply, and this is the expensive question asked
    /// deliberately about a single item.
    /// </remarks>
    [Fact]
    public void ATopicReturnsExactlyThatItemWithItsNotesInFull()
    {
        ThreadShowResult shown = ThreadItems.Show(Filed(), topic: "eviction");

        ThreadItem item = Assert.Single(shown.Items);

        Assert.Equal("cache eviction", item.Topic);
        Assert.Equal("Ruled out the TTL.\nSecond line.", item.Notes);
        Assert.Equal(1, shown.Count);
    }

    /// <summary>
    /// 'active' names the focus of the LIST, not of the answer.
    /// </summary>
    /// <remarks>
    /// The trap this exists for. ActiveTopic is computed over the unprojected list, so a
    /// caller who asks about some other item still learns what is in focus. Computed after the
    /// selector instead, it would be null here -- and a reader would correctly conclude from
    /// that envelope that nothing is in focus, which is false. Nothing else catches it: every
    /// test that existed before these selectors did passes no selector at all.
    /// </remarks>
    [Fact]
    public void ActiveStillNamesTheFocusWhenADifferentItemWasSelected()
    {
        string path = Filed();

        Assert.Equal("the startup brief", ThreadItems.Show(path, topic: "eviction").Active);
        Assert.Equal("the startup brief", ThreadItems.Show(path, area: "RazorGraph").Active);
        Assert.Equal("the startup brief", ThreadItems.Report(path, topic: "eviction").Active);
    }

    /// <summary>Ambiguity is refused, and every candidate named.</summary>
    /// <remarks>
    /// The same contract the writing verbs hold to. A reader that showed the first of two
    /// matches would teach its caller that the text they typed identifies one item, which is
    /// the belief that makes the next update rewrite the wrong one.
    /// </remarks>
    [Fact]
    public void AnAmbiguousTopicIsRefusedWithEveryCandidateNamed()
    {
        string message = Assert.Throws<GraphException>(
            () => ThreadItems.Show(Filed(), topic: "cache")).Message;

        Assert.Contains("ambiguous", message, StringComparison.Ordinal);
        Assert.Contains("cache eviction", message, StringComparison.Ordinal);
        Assert.Contains("cache warming", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A selector that matches nothing is refused, not answered with an empty list.
    /// </summary>
    /// <remarks>
    /// "No item matches what you typed" and "no such work is open" are different claims, and
    /// an empty envelope makes the second one. This is also the boundary between the two kinds
    /// of failure here: 'error' means the list could not be READ, and folding a caller's
    /// mistake into it would make both unreadable. Show still never throws for a bad file --
    /// see the startup tests below -- and startup passes no selector.
    /// </remarks>
    [Fact]
    public void AnUnmatchedTopicIsRefusedRatherThanReturningEmpty()
    {
        string path = Filed();

        Assert.Contains(
            "No item matches topic",
            Assert.Throws<GraphException>(() => ThreadItems.Show(path, topic: "no such thing")).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "No item matches topic",
            Assert.Throws<GraphException>(() => ThreadItems.Report(path, topic: "no such thing")).Message,
            StringComparison.Ordinal);
    }

    /// <summary>An unknown area is refused too, and says which areas exist.</summary>
    [Fact]
    public void AnUnknownAreaIsRefusedAndNamesTheAreasInUse()
    {
        string message = Assert.Throws<GraphException>(
            () => ThreadItems.Show(Filed(), area: "SomeOtherRepo")).Message;

        Assert.Contains("No item is filed under an area", message, StringComparison.Ordinal);
        Assert.Contains("JanetHome", message, StringComparison.Ordinal);
        Assert.Contains(ThreadItems.Unfiled, message, StringComparison.Ordinal);
    }

    /// <summary>An area returns that area's items and nothing else.</summary>
    /// <remarks>
    /// Case-insensitive, and matched against the stored label rather than the topic: 'cache
    /// warming' is filed under 'razorgraph' in the fixture precisely so that a matcher which
    /// compared case-sensitively, or which fell back to the topic string, would come back with
    /// one item instead of two.
    /// </remarks>
    [Fact]
    public void AnAreaReturnsOnlyThatAreasItems()
    {
        ThreadShowResult shown = ThreadItems.Show(Filed(), area: "razorGRAPH");

        Assert.Equal(["cache eviction", "cache warming"], shown.Items.Select(i => i.Topic));
        Assert.Equal(2, shown.Count);
    }

    /// <summary>The unfiled items are a group like any other, and reachable as one.</summary>
    /// <remarks>
    /// This is what makes "do not backfill" a workable position rather than a way of losing
    /// items: nothing was guessed into a neighbouring area, and nothing became unreachable for
    /// having been left alone.
    /// </remarks>
    [Fact]
    public void UnfiledItemsAreReachableAsTheirOwnGroup()
    {
        ThreadShowResult shown = ThreadItems.Show(Filed(), area: ThreadItems.Unfiled);

        Assert.Equal(["something noticed in passing"], shown.Items.Select(i => i.Topic));
        Assert.Equal(string.Empty, shown.Items[0].Area);
    }

    /// <summary>No selector is the old behaviour exactly.</summary>
    /// <remarks>
    /// The startup path takes this branch, so it is the one that must not have moved. Both
    /// selectors are opt-in and neither has a default that filters.
    /// </remarks>
    [Fact]
    public void NoSelectorReturnsTheListUnchanged()
    {
        string path = Filed();

        ThreadShowResult shown = ThreadItems.Show(path);

        Assert.Equal(
            ["cache eviction", "cache warming", "the startup brief", "something noticed in passing"],
            shown.Items.Select(i => i.Topic));

        Assert.Equal(4, shown.Count);
        Assert.Equal("the startup brief", shown.Active);
        Assert.Null(shown.Error);
    }

    /// <summary>Nothing is capped. An explicit selector is a request for a known set.</summary>
    [Fact]
    public void AnAreaSelectorIsNeverTruncated()
    {
        string path = Empty();

        foreach (int i in Enumerable.Range(0, 40))
        {
            ThreadItems.Add(path, $"topic {i:00}", area: "JanetHome");
        }

        Assert.Equal(40, ThreadItems.Show(path, area: "JanetHome").Items.Count);
    }

    /// <summary>
    /// A '*' is a literal asterisk, in both selectors.
    /// </summary>
    /// <remarks>
    /// The PowerShell matched with -like "*topic*", so a topic containing * or ? behaved as a
    /// pattern by accident. That was removed rather than reproduced, and these selectors are
    /// new code that could reintroduce it without anything noticing.
    /// </remarks>
    [Fact]
    public void AWildcardInASelectorIsALiteral()
    {
        string path = Empty();

        ThreadItems.Add(path, "a plain topic", area: "JanetHome");
        ThreadItems.Add(path, "a * topic", area: "Star * Area");

        Assert.Equal("a * topic", Assert.Single(ThreadItems.Show(path, topic: "*").Items).Topic);
        Assert.Equal("a * topic", Assert.Single(ThreadItems.Show(path, area: "* Area").Items).Topic);

        Assert.Throws<GraphException>(() => ThreadItems.Show(path, area: "Star*"));
    }

    /// <summary>
    /// An area survives a write and a read.
    /// </summary>
    /// <remarks>
    /// Normalise and Serialize are an allow-list -- they read and write exactly the keys they
    /// name, and unlike research_update they preserve nothing else. A field added to the record
    /// but to only one of those two is dropped by the next write with no error anywhere, which
    /// is why this asserts the FILE and not just the returned object.
    /// </remarks>
    [Fact]
    public void AnAreaSurvivesAWriteAndARead()
    {
        string path = Empty();

        ThreadItems.Add(path, "cache eviction", area: "RazorGraph");

        Assert.Contains("\"area\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal("RazorGraph", ThreadItems.Parse(File.ReadAllText(path))[0].Area);
        Assert.Equal("RazorGraph", ThreadItems.Show(path).Items[0].Area);

        // And through a second write, which is where a one-sided allow-list loses it: the
        // update rewrites every item from what Normalise read.
        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, next: "look again");

        Assert.Equal("RazorGraph", ThreadItems.Show(path).Items[0].Area);
        Assert.Equal("look again", ThreadItems.Show(path).Items[0].Next);
    }

    /// <summary>An area can be set on an existing item, and cleared again.</summary>
    [Fact]
    public void AnAreaCanBeSetLaterAndUnset()
    {
        string path = Seeded();

        ThreadUpdateResult set = ThreadItems.Update(
            path, new ThreadSelector { Topic = "cache eviction" }, area: "RazorGraph");

        Assert.Contains("area", set.Changed);
        Assert.Equal("RazorGraph", ThreadItems.Show(path).Items[0].Area);

        // Empty clears, as it does for every other field here: an item filed by mistake has to
        // be returnable to unfiled.
        ThreadItems.Update(path, new ThreadSelector { Topic = "cache eviction" }, area: "");

        Assert.Equal(string.Empty, ThreadItems.Show(path).Items[0].Area);
        Assert.Equal(ThreadItems.Unfiled, ThreadItems.AreaOf(ThreadItems.Show(path).Items[0]));
    }

    /// <summary>
    /// An item with no area is not written with one.
    /// </summary>
    /// <remarks>
    /// The no-backfill rule, enforced at the file. Writing "area": "" for every unlabelled item
    /// would rewrite all of them the first time anything touched the list -- harmless in
    /// content, but it would put the field on 62 items that nobody has labelled, which is the
    /// appearance of a decision that was not made.
    /// </remarks>
    [Fact]
    public void AnUnfiledItemIsNotGivenAnAreaKeyOnDisk()
    {
        string path = Empty();

        ThreadItems.Add(path, "something noticed in passing");

        Assert.DoesNotContain("\"area\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(string.Empty, ThreadItems.Show(path).Items[0].Area);
    }

    /// <summary>
    /// A pre-existing list reads as unfiled, and stays that way.
    /// </summary>
    /// <remarks>
    /// The migration, such as it is: the live list had 62 items and no areas when the field
    /// arrived, and reading it must not invent any. Splitting the topic on its first colon
    /// produced 12 groups for 16 topics and fragmented single projects across several, which is
    /// why an inferred area is not a cheaper version of a stored one.
    /// </remarks>
    [Fact]
    public void ExistingItemsReadAsUnfiledAndAreNeverInferred()
    {
        string path = Empty();

        File.WriteAllText(
            path,
            """
            [
              { "topic": "RazorGraph: coverage misses lambdas", "status": "parked", "refs": [], "next": "", "notes": "" },
              { "topic": "no colon here at all", "status": "parked", "refs": [], "next": "", "notes": "" }
            ]
            """);

        IReadOnlyList<ThreadItem> items = ThreadItems.Show(path).Items;

        Assert.All(items, i => Assert.Equal(string.Empty, i.Area));
        Assert.All(items, i => Assert.Equal(ThreadItems.Unfiled, ThreadItems.AreaOf(i)));
        Assert.Equal(2, ThreadItems.Show(path, area: ThreadItems.Unfiled).Count);
    }

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
