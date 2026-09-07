using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Focus is one cursor PER AREA, not one for the whole list.
/// </summary>
/// <remarks>
/// The defect these exist for, measured 2026-09-06. Items gained a stored area on 2026-09-03
/// and the reading verbs gained an area selector, but focus was left global -- the list was
/// partitioned for reading and not for focus, and this list is shared by every repo on this
/// machine, so four concurrent sessions fought over one variable. The startup brief ran the
/// report narrowed to JanetHome, returned 21 JanetHome items, and named a gamehub item beside
/// them as the work in hand. Setting a JanetHome item active parked a gamehub session's item
/// twice; completing a JanetHome item cleared focus for all four areas.
///
/// Nothing about the stored format changed: status plus the item's area already encoded one
/// cursor per area, and only the scope the operations enforce it over is new. So every test
/// here is about SCOPE, and the central one is the first -- that a write in one area leaves
/// another area's cursor exactly where it was.
///
/// In the "thread store" collection with every other class that writes items, because
/// ThreadNotesCeilingTests sets JANET_NOTES_BUDGET on the process and a write here racing that
/// window would be refused for a reason this file never mentions.
/// </remarks>
[Collection("thread store")]
public class ThreadAreaFocusTests : IDisposable
{
    private readonly List<string> _directories = [];

    /// <summary>
    /// Four areas, three labelled and one unfiled, with two of them holding focus.
    /// </summary>
    /// <remarks>
    /// Two cursors at once is the state the old invariant could not represent, and building it
    /// with two ordinary adds is itself the first assertion: under global focus the second
    /// add:active would have parked the first, and this fixture could not exist.
    ///
    /// One area is left unfiled deliberately. (unfiled) is an area like any other and gets its
    /// own cursor -- it is not a residue, and a fixture where everything is labelled would test
    /// the easy half.
    /// </remarks>
    private string Filed()
    {
        string path = Empty();

        ThreadItems.Add(path, "the startup brief", area: "JanetHome", active: true);
        ThreadItems.Add(path, "the contract gate", area: "JanetHome");
        ThreadItems.Add(path, "the doctrine board", area: "gamehub", active: true);
        ThreadItems.Add(path, "scouting doctrine", area: "gamehub");
        ThreadItems.Add(path, "the graph guard", area: "RazorGraphTool");
        ThreadItems.Add(path, "something noticed in passing");

        return path;
    }

    private string Empty()
    {
        string directory = Path.Combine(Path.GetTempPath(), "janet-area-focus", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        return Path.Combine(directory, "thread-stack.json");
    }

    /// <summary>The topic in focus in one area, read back through the store.</summary>
    private static string? FocusIn(string path, string area) =>
        ThreadItems.Report(path, area: area).Active;

    // ---- the central claim ---------------------------------------------------------------

    /// <summary>
    /// Taking focus in one area does not touch another area's cursor.
    /// </summary>
    /// <remarks>
    /// THE test this change exists for. Measured before it: setting a JanetHome item active
    /// parked a gamehub session's item twice, because ParkActive walked the whole list.
    /// </remarks>
    [Fact]
    public void SettingActiveInOneAreaLeavesAnotherAreasFocusAlone()
    {
        string path = Filed();

        ThreadActiveResult result = ThreadItems.SetActive(
            path, new ThreadSelector { Topic = "the contract gate" });

        Assert.Equal("the contract gate", result.Active);

        // 'previous' is still reported, and is now that AREA's displaced cursor rather than
        // the list's -- the switch stays visible without being a claim about other projects.
        Assert.Equal("the startup brief", result.Previous);

        Assert.Equal("the contract gate", FocusIn(path, "JanetHome"));
        Assert.Equal("the doctrine board", FocusIn(path, "gamehub"));
    }

    /// <summary>Two areas hold focus at once, and an area with none reports null.</summary>
    [Fact]
    public void SeveralAreasHoldFocusAtOnceAndOneWithoutItReportsNull()
    {
        string path = Filed();

        Assert.Equal("the startup brief", FocusIn(path, "JanetHome"));
        Assert.Equal("the doctrine board", FocusIn(path, "gamehub"));
        Assert.Null(FocusIn(path, "RazorGraphTool"));
        Assert.Null(FocusIn(path, ThreadItems.Unfiled));

        // And the store agrees: two items are active, in two different areas.
        Assert.Equal(2, ThreadItems.Show(path).Items.Count(i => i.IsActive));
    }

    /// <summary>(unfiled) is an area like any other and carries its own cursor.</summary>
    /// <remarks>
    /// The group of unlabelled items is not a residue to be swept into whichever project it
    /// resembles, and it is not exempt from the invariant either. Most of the live list is
    /// unfiled, so an unfiled cursor that behaved differently would be the common case
    /// behaving differently.
    /// </remarks>
    [Fact]
    public void UnfiledIsItsOwnAreaAndItsFocusCoexistsWithALabelledOnes()
    {
        string path = Filed();

        ThreadItems.SetActive(path, new ThreadSelector { Topic = "something noticed" });

        Assert.Equal("something noticed in passing", FocusIn(path, ThreadItems.Unfiled));
        Assert.Equal("the startup brief", FocusIn(path, "JanetHome"));
        Assert.Equal("the doctrine board", FocusIn(path, "gamehub"));
    }

    // ---- the other write verbs -----------------------------------------------------------

    /// <summary>Completing in one area clears that area's cursor and no other.</summary>
    /// <remarks>
    /// Measured before this change: completing a JanetHome item cleared focus for all four
    /// areas, so finishing a piece of work told three other sessions that nothing was in hand.
    /// </remarks>
    [Fact]
    public void CompletingInOneAreaLeavesAnotherAreasFocusAlone()
    {
        string path = Filed();

        ThreadCompleteResult result = ThreadItems.Complete(
            path, new ThreadSelector { Topic = "the startup brief" });

        Assert.Equal("the startup brief", result.Completed);

        // The completed item's own area, which is now empty. Not the list's focus, which no
        // longer exists as a single thing.
        Assert.Null(result.Active);

        Assert.Null(FocusIn(path, "JanetHome"));
        Assert.Equal("the doctrine board", FocusIn(path, "gamehub"));
    }

    /// <summary>An add that takes focus parks only its own area's cursor.</summary>
    [Fact]
    public void AddingActiveParksOnlyTheNewItemsOwnArea()
    {
        string path = Filed();

        ThreadAddResult result = ThreadItems.Add(path, "urgent detour", area: "JanetHome", active: true);

        Assert.Equal("urgent detour", result.Active);
        Assert.Equal("urgent detour", FocusIn(path, "JanetHome"));
        Assert.Equal("the doctrine board", FocusIn(path, "gamehub"));
    }

    /// <summary>An update to status active parks only that item's own area's cursor.</summary>
    [Fact]
    public void UpdatingStatusToActiveParksOnlyTheItemsOwnArea()
    {
        string path = Filed();

        ThreadItems.Update(
            path, new ThreadSelector { Topic = "the contract gate" }, status: ThreadItems.Active);

        Assert.Equal("the contract gate", FocusIn(path, "JanetHome"));
        Assert.Equal("the doctrine board", FocusIn(path, "gamehub"));

        // Still one cursor in JanetHome, so the park happened rather than being skipped.
        Assert.Single(ThreadItems.Show(path, area: "JanetHome").Items, i => i.IsActive);
    }

    // ---- the ambiguity rule --------------------------------------------------------------

    /// <summary>
    /// Clearing focus with no area works while exactly ONE area holds it.
    /// </summary>
    /// <remarks>
    /// "Act when exactly one, refuse when several" rather than "always refuse": the recorded
    /// goldens exercise a list that is entirely unfiled, where one area holds everything, and
    /// the common single-project case must not need an area it has no reason to name.
    /// </remarks>
    [Fact]
    public void ClearingWithNoAreaActsWhenASingleAreaHoldsFocus()
    {
        string path = Filed();

        ThreadItems.SetActive(path, null, area: "gamehub");

        ThreadActiveResult result = ThreadItems.SetActive(path, null);

        Assert.Null(result.Active);
        Assert.Equal("the startup brief", result.Previous);
        Assert.Null(FocusIn(path, "JanetHome"));
    }

    /// <summary>
    /// Clearing focus with no area is REFUSED while several hold it, and every cursor is named.
    /// </summary>
    /// <remarks>
    /// The same house rule an ambiguous topic gets: refused with every candidate named, never
    /// resolved to a first match. Resolving here would clear another repo's cursor.
    /// </remarks>
    [Fact]
    public void ClearingWithNoAreaIsRefusedWhenSeveralHoldFocusAndNamesThemAll()
    {
        string message = Assert.Throws<GraphException>(
            () => ThreadItems.SetActive(Filed(), null)).Message;

        Assert.Contains("JanetHome: the startup brief", message, StringComparison.Ordinal);
        Assert.Contains("gamehub: the doctrine board", message, StringComparison.Ordinal);
        Assert.Contains("pass area", message, StringComparison.Ordinal);
    }

    /// <summary>An empty selector completes the sole cursor while there is exactly one.</summary>
    [Fact]
    public void AnEmptySelectorCompletesTheSoleCursor()
    {
        string path = Filed();

        ThreadItems.SetActive(path, null, area: "gamehub");

        Assert.Equal("the startup brief", ThreadItems.Complete(path, new ThreadSelector()).Completed);
    }

    /// <summary>An empty selector is REFUSED while several areas hold focus, naming them all.</summary>
    [Fact]
    public void AnEmptySelectorIsRefusedWhenSeveralAreasHoldFocusAndNamesThemAll()
    {
        string message = Assert.Throws<GraphException>(
            () => ThreadItems.Complete(Filed(), new ThreadSelector())).Message;

        Assert.Contains("JanetHome: the startup brief", message, StringComparison.Ordinal);
        Assert.Contains("gamehub: the doctrine board", message, StringComparison.Ordinal);
        Assert.Contains("pass a topic", message, StringComparison.Ordinal);
    }

    /// <summary>With nothing in focus anywhere, an empty selector still refuses as it always did.</summary>
    /// <remarks>
    /// The no-focus case is unchanged deliberately: "nothing to act on" was already the right
    /// answer, and the ambiguity rule is about several cursors, not about none.
    /// </remarks>
    [Fact]
    public void WithNoFocusAnywhereAnEmptySelectorStillRefusesForTheOldReason()
    {
        string path = Filed();

        ThreadItems.SetActive(path, null, area: "gamehub");
        ThreadItems.SetActive(path, null, area: "JanetHome");

        Assert.Contains(
            "No item is active",
            Assert.Throws<GraphException>(() => ThreadItems.Complete(path, new ThreadSelector())).Message,
            StringComparison.Ordinal);

        // And clearing what is not there is a success reporting nothing displaced, not a fault.
        Assert.Null(ThreadItems.SetActive(path, null).Previous);
    }

    // ---- clearing one named area ---------------------------------------------------------

    /// <summary>none with an area clears that area's cursor and only that one.</summary>
    [Fact]
    public void ClearingANamedAreaLeavesEveryOtherAreasFocusAlone()
    {
        string path = Filed();

        ThreadActiveResult result = ThreadItems.SetActive(path, null, area: "gamehub");

        Assert.Null(result.Active);
        Assert.Equal("the doctrine board", result.Previous);

        Assert.Null(FocusIn(path, "gamehub"));
        Assert.Equal("the startup brief", FocusIn(path, "JanetHome"));
    }

    /// <summary>An area that names no group, and one that names two, are both refused.</summary>
    /// <remarks>
    /// A write resolves its area to exactly one, the way a topic resolves to exactly one item.
    /// A substring reaching two areas would clear two cursors, which is the wrong-item write
    /// the whole selector discipline exists to prevent.
    /// </remarks>
    [Fact]
    public void AnUnknownOrAmbiguousAreaIsRefusedOnTheClear()
    {
        string path = Filed();

        Assert.Contains(
            "Areas in use",
            Assert.Throws<GraphException>(() => ThreadItems.SetActive(path, null, area: "nowhere")).Message,
            StringComparison.Ordinal);

        ThreadItems.Add(path, "a second razor item", area: "RazorGraphSkill");

        Assert.Contains(
            "ambiguous",
            Assert.Throws<GraphException>(() => ThreadItems.SetActive(path, null, area: "RazorGraph")).Message,
            StringComparison.Ordinal);
    }

    /// <summary>An area with a topic is refused: the two would name different areas.</summary>
    [Fact]
    public void AnAreaWithATopicIsRefusedRatherThanSilentlyIgnored()
    {
        string message = Assert.Throws<GraphException>(() => ThreadItems.SetActive(
            Filed(), new ThreadSelector { Topic = "the contract gate" }, area: "gamehub")).Message;

        Assert.Contains("applies only with none", message, StringComparison.Ordinal);
    }

    // ---- the envelope --------------------------------------------------------------------

    /// <summary>
    /// 'active' carries the narrowed area's cursor; unnarrowed it is null and 'areas' has them all.
    /// </summary>
    /// <remarks>
    /// The null is not "nothing is in focus" -- two areas are holding something throughout.
    /// It is "you did not narrow", and the areas map is where the complete answer lives. That
    /// distinction is the whole reason the rows gained a cursor rather than the scalar being
    /// left to pick one.
    /// </remarks>
    [Fact]
    public void TheReportCarriesTheNarrowedAreasCursorAndOtherwiseEveryCursorInTheMap()
    {
        string path = Filed();

        Assert.Equal("the startup brief", ThreadItems.Report(path, area: "JanetHome").Active);
        Assert.Equal("the doctrine board", ThreadItems.Report(path, area: "gamehub").Active);
        Assert.Null(ThreadItems.Report(path, area: "RazorGraphTool").Active);

        ThreadReportResult whole = ThreadItems.Report(path);

        Assert.Null(whole.Active);
        Assert.Equal(
            [
                (ThreadItems.Unfiled, null),
                ("gamehub", "the doctrine board"),
                ("JanetHome", "the startup brief"),
                ("RazorGraphTool", null),
            ],
            whole.Areas.Select(a => (a.Area, a.Active)));
    }

    /// <summary>Nothing in focus anywhere is every row carrying null, not a missing map.</summary>
    /// <remarks>
    /// The state that would be indistinguishable from "you did not narrow" if the scalar were
    /// the only field. Told apart by the rows, which is what they are for.
    /// </remarks>
    [Fact]
    public void NothingInFocusAnywhereIsEveryRowNull()
    {
        string path = Filed();

        ThreadItems.SetActive(path, null, area: "gamehub");
        ThreadItems.SetActive(path, null, area: "JanetHome");

        ThreadReportResult report = ThreadItems.Report(path);

        Assert.Equal(4, report.Areas.Count);
        Assert.All(report.Areas, a => Assert.Null(a.Active));
    }

    /// <summary>
    /// 'areas' still describes the WHOLE open list under an area selector.
    /// </summary>
    /// <remarks>
    /// The contract-3 guarantee, re-pinned because contract 4 added a field to these rows and a
    /// cursor computed from the narrowed set instead of the whole one would break it silently:
    /// a report of JanetHome would show every other area with a null cursor and read as "no
    /// other project has anything in hand", which is the exact false claim the map exists to
    /// prevent.
    /// </remarks>
    [Fact]
    public void TheAreasMapStillDescribesTheWholeListUnderAnAreaSelector()
    {
        string path = Filed();

        ThreadReportResult whole = ThreadItems.Report(path);
        ThreadReportResult narrowed = ThreadItems.Report(path, area: "JanetHome");
        ThreadReportResult one = ThreadItems.Report(path, topic: "graph guard");

        Assert.Equal(2, narrowed.Items.Count);
        Assert.Equal(whole.Areas, narrowed.Areas);
        Assert.Equal(whole.Areas, one.Areas);

        // Including the cursors: gamehub's is carried by a report that returned no gamehub item.
        Assert.Equal(
            "the doctrine board",
            narrowed.Areas.Single(a => a.Area == "gamehub").Active);
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
