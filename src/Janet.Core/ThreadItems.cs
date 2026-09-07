using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// One investigation topic. Six fields, four of which are distinct roles rather than stages
/// of one idea.
/// </summary>
/// <remarks>
/// refs -- context that has earned a research.json node.
/// next -- the resume cursor: the one thing to do first on return.
/// notes -- detail too small or too fresh to be worth a node.
/// area -- which project this item belongs to.
///
/// An item may legitimately carry any combination, including none.
/// </remarks>
public sealed record ThreadItem
{
    public string Topic { get; init; } = string.Empty;
    public string Status { get; init; } = ThreadItems.Parked;
    public IReadOnlyList<string> Refs { get; init; } = [];
    public string Next { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;

    /// <summary>
    /// Which project or area this item belongs to. Empty means unfiled.
    /// </summary>
    /// <remarks>
    /// STORED, never derived from the topic. Measured on the live list (see
    /// note.thread-item-projection): splitting topics on their first colon produced 12 groups
    /// for 16 topics and split single projects across several of them, while 4 topics had no
    /// colon at all. An inferred area is therefore not a cheaper version of a stored one, it is
    /// a wrong one. An item with none reads as <see cref="ThreadItems.Unfiled"/> and is never
    /// guessed into a neighbour.
    /// </remarks>
    public string Area { get; init; } = string.Empty;

    public bool IsActive => string.Equals(Status, ThreadItems.Active, StringComparison.OrdinalIgnoreCase);

    public bool IsDone => string.Equals(Status, ThreadItems.Done, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Which item an operation acts on: a topic, or whatever is active.</summary>
/// <remarks>
/// Selection by position was removed on 2026-08-14. The list is addressed as a dictionary
/// keyed by topic, because position is not identity: Show filters completed items before
/// printing while a raw index counted into the unfiltered file, so a displayed number was
/// wrong by the done count and silently selected -- then rewrote -- a different item.
/// </remarks>
public sealed record ThreadSelector
{
    public string Topic { get; init; } = string.Empty;
}

public sealed record ThreadAddResult(
    string Added, string? Active, int Count, int Batched = 1) : IBatchedResult<ThreadAddResult>
{
    public ThreadAddResult WithBatch(int total, int batched) =>
        this with { Count = total, Batched = batched };
}

public sealed record ThreadUpdateResult(
    string Updated, IReadOnlyList<string> Changed, int Count, int Batched = 1)
    : IBatchedResult<ThreadUpdateResult>
{
    public ThreadUpdateResult WithBatch(int total, int batched) =>
        this with { Count = total, Batched = batched };
}

public sealed record ThreadCompleteResult(
    string Completed, string? Active, int Remaining, int Batched = 1)
    : IBatchedResult<ThreadCompleteResult>
{
    public ThreadCompleteResult WithBatch(int total, int batched) =>
        this with { Remaining = total, Batched = batched };
}

public sealed record ThreadActiveResult(
    string? Active, string? Previous, int Count, int Batched = 1) : IBatchedResult<ThreadActiveResult>
{
    public ThreadActiveResult WithBatch(int total, int batched) =>
        this with { Count = total, Batched = batched };
}

/// <summary>
/// What the list holds right now. Read-only, so it carries no batch.
/// </summary>
/// <remarks>
/// Error is in-band and not an exception: this runs in the startup path, and a corrupt list
/// must be reported as a fact about the list rather than take the session's startup down with
/// it. A caller that ignores the field gets an empty list, which is the honest degraded answer.
/// </remarks>
public sealed record ThreadShowResult(
    int Count, string? Active, IReadOnlyList<ThreadShownItem> Items, string? Error);

/// <summary>
/// One item as Show carries it: the stored fields, with Notes as a LEAD unless the caller asked
/// for the one item in full.
/// </summary>
/// <remarks>
/// A separate record from <see cref="ThreadItem"/> because what is returned is no longer what
/// is stored. Since 2026-09-05 Show returns the same lead the report does (first non-empty line,
/// capped at 200) and states the withheld size beside it; before that it returned every note
/// body whatever the selector, which is how a single read reached 238,552 characters.
///
/// NotesLength is always the STORED size, so a reader can tell a one-line note from a
/// five-day log without carrying it. NotesTruncated is true whenever Notes is not the whole
/// stored text -- a second line, a lead over the cap, even surrounding whitespace -- so "what
/// you have is not all of it" is stated rather than left for a reader to notice. Both are
/// carried even under full, where the flag is simply false.
///
/// Area is the STORED label, empty for an unfiled item, exactly as on ThreadItem; the JSON
/// envelope resolves it to <see cref="ThreadItems.Unfiled"/> on the way out, as it always has.
/// </remarks>
public sealed record ThreadShownItem(
    string Topic, string Status, IReadOnlyList<string> Refs, string Next, string Notes, string Area,
    int NotesLength, bool NotesTruncated)
{
    public bool IsActive => string.Equals(Status, ThreadItems.Active, StringComparison.OrdinalIgnoreCase);

    public bool IsDone => string.Equals(Status, ThreadItems.Done, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One item as the reporter sees it: everything except the note body.</summary>
/// <remarks>
/// NotesLead is the first non-empty line, trimmed and capped; NotesLength is the full size in
/// characters, so the reader can tell a one-line note from a five-day log without carrying it.
/// NotesLead is null when the caller asked for the map without leads (lead: false), and the
/// serializer then omits the key rather than writing an empty string that would read as "this
/// item has no notes". NotesLength is always carried, so the size withheld is still stated.
/// Reporting the length rather than a bare truncation flag is the catalog's convention -- a
/// response that truncates says so, and says by how much.
///
/// Area is the RESOLVED label: <see cref="ThreadItems.Unfiled"/> where the item carries none,
/// so a reader of the report never has to know that the stored field is empty. What is stored
/// stays empty -- an unlabelled item is not backfilled by being displayed.
/// </remarks>
public sealed record ThreadReportItem(
    string Topic, string Status, string Area, IReadOnlyList<string> Refs, string Next,
    string? NotesLead, int NotesLength);

/// <summary>One area, how many OPEN items are filed under it, and which one it has in focus.</summary>
/// <remarks>
/// Open, never total: the map exists so a narrowed report still says where the rest of the
/// backlog is, and completed items are not backlog. Area is the resolved label, so the group of
/// unlabelled items appears here as <see cref="ThreadItems.Unfiled"/> like any other.
///
/// Active is that area's OWN cursor, null where it has none, and it is what makes the
/// unnarrowed report complete. Focus became per-area on 2026-09-06; before that the envelope
/// had one scalar for the whole list, which could not describe four projects at once, and the
/// startup brief demonstrated the consequence by narrowing to JanetHome and naming a gamehub
/// item as the thing in hand. A row per area removes the null ambiguity too: "nothing is in
/// focus anywhere" is every row carrying null, which a reader can tell apart from the envelope's
/// own null, which means "you did not narrow -- read the map".
/// </remarks>
public sealed record ThreadAreaCount(string Area, int Open, string? Active);

/// <summary>
/// The list as a map rather than as its contents.
/// </summary>
/// <remarks>
/// A separate result from <see cref="ThreadShowResult"/>, deliberately, rather than a projection
/// flag on it. Show's envelope is captured by startup and asserted byte-for-byte against recorded
/// output, so growing it costs a declared correction to a contract that has a live consumer --
/// and every reader of the old shape has to be checked. This is a new format instead: nothing
/// that exists changes, and the reporter is free to answer a different question.
///
/// The question it answers is "where was I", which is what the text view has always answered
/// (see ThreadJson.Render, first line only). This is that view for a machine reader.
///
/// Areas is the per-area map of the WHOLE open list -- one entry per area in use, with its open
/// count and its own cursor, sorted by name -- and it ignores the selectors. Added 2026-09-04 so
/// that a report narrowed to one project still carries the shape of the backlog it left out: the
/// startup brief narrows to the session's own area, and without this the other projects' work
/// would simply vanish from it, which is the silent omission the envelope otherwise avoids.
///
/// Active is THIS ANSWER'S scope since 2026-09-06, not the whole list's: the area selector's
/// focus when one was passed, and null when none was, because a scalar cannot carry four
/// cursors and picking one of them to report is how the startup brief came to name another
/// repo's work. The complete answer to "what is in focus" when nothing narrowed is Areas.
/// </remarks>
public sealed record ThreadReportResult(
    int Count, string? Active, IReadOnlyList<ThreadAreaCount> Areas, IReadOnlyList<ThreadReportItem> Items,
    int NotesLength, string? Error);

/// <summary>
/// The thread-item list: investigation topics with explicit focus.
/// </summary>
/// <remarks>
/// Replaced a push/pop stack on 2026-08-08. The stack's failure was that "record a topic" and
/// "descend into a topic" were the same operation, so noting work displaced whatever was active,
/// and completing an item dropped it. A list separates those: position carries order, status
/// carries focus, and nothing is ever removed.
///
/// FOCUS IS ONE PER AREA since 2026-09-06, not one per list. Status plus the item's area already
/// encodes that, so nothing about the stored format changed -- only the scope every operation
/// enforces it over. The reason is that this list is shared by every repo on this machine: items
/// gained an area on 2026-09-03 and the reading verbs gained an area selector, but focus was
/// left global, so the list was partitioned for reading and not for focus and concurrent
/// sessions fought over one variable. Measured 2026-09-06: the startup brief narrowed to
/// JanetHome and named a gamehub item as the work in hand; setting a JanetHome item active
/// parked a gamehub session's item twice; completing one cleared focus for all four areas.
///
/// Every write goes through the shared write queue, which is the same mechanism the catalog
/// uses. The PowerShell serialised its read-modify-write with a named mutex, added after an
/// unlocked one destroyed a session's notes; the queue subsumes that and adds batching and an
/// atomic write, so this list is no longer defended by a mechanism every future writer has to
/// remember to take.
/// </remarks>
public static class ThreadItems
{
    public const string Active = "active";
    public const string Parked = "parked";
    public const string Done = "done";

    public static readonly string[] Statuses = [Active, Parked, Done];

    /// <summary>The single group an item with no area belongs to.</summary>
    /// <remarks>
    /// A literal, and one group rather than many: an unlabelled item is not sorted into a
    /// plausible neighbour, because a guess that is usually right is indistinguishable from a
    /// label that was set, and the point of the field is to be able to trust it.
    /// </remarks>
    public const string Unfiled = "(unfiled)";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,

        // Matches ConvertTo-Json, which leaves &, <, > and apostrophes alone. The default
        // encoder escapes them as \uXXXX -- valid JSON that no longer matches byte for byte,
        // and unreadable for anyone opening the file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Where the list lives when no path is given.</summary>
    /// <remarks>
    /// %TEMP%, deliberately: the list is session-scale working memory, not a repo artifact, and
    /// putting it in a repo would commit one session's train of thought into another's history.
    /// </remarks>
    public static string DefaultPath { get; } =
        Path.Combine(Path.GetTempPath(), "Janet", "thread-stack.json");

    public static string Resolve(string? path) => string.IsNullOrWhiteSpace(path) ? DefaultPath : path;

    // ---- reading -----------------------------------------------------------------------

    /// <summary>
    /// Parses the list, normalising every item to the six-field shape.
    /// </summary>
    /// <remarks>
    /// Absent fields default rather than fail, which is what makes migrating the old
    /// { topic, status, notes } form a read followed by a write -- no transcription of note
    /// bodies, so no note body can be mangled in the process.
    ///
    /// A bare object rather than an array is read as a one-item list, matching @($parsed).
    /// </remarks>
    public static IReadOnlyList<ThreadItem> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        JsonNode? parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        JsonObject[] entries = parsed switch
        {
            JsonArray array => [.. array.OfType<JsonObject>()],
            JsonObject single => [single],
            _ => [],
        };

        return [.. entries.Select(Normalise)];
    }

    public static IReadOnlyList<ThreadItem> Read(string? path)
    {
        string resolved = Resolve(path);

        return File.Exists(resolved) ? Parse(File.ReadAllText(resolved)) : [];
    }

    /// <summary>
    /// One stored entry as a <see cref="ThreadItem"/>.
    /// </summary>
    /// <remarks>
    /// This and <see cref="Serialize"/> are an ALLOW-LIST, not a passthrough: they read and
    /// write exactly the keys named here, and a key absent from both is silently dropped by the
    /// next write. research_update preserves fields it does not know about; this deliberately
    /// does not, because the list is a fixed shape rather than an open record. The consequence
    /// is that a field added to <see cref="ThreadItem"/> and to only one of these two is a data
    /// loss with no error -- so they are edited together, always.
    /// </remarks>
    private static ThreadItem Normalise(JsonObject entry) => new()
    {
        Topic = Text(entry, "topic", string.Empty),
        Status = Text(entry, "status", Parked),
        Refs = Strings(entry, "refs"),
        Next = Text(entry, "next", string.Empty),
        Notes = Text(entry, "notes", string.Empty),
        Area = Text(entry, "area", string.Empty),
    };

    private static string Text(JsonObject entry, string name, string fallback) =>
        entry.TryGetPropertyValue(name, out JsonNode? value) && value is not null
            ? NodeText.AsText(value)
            : fallback;

    private static IReadOnlyList<string> Strings(JsonObject entry, string name) =>
        entry.TryGetPropertyValue(name, out JsonNode? value) && value is JsonArray array
            ? [.. array.Where(v => v is not null).Select(NodeText.AsText)]
            : [];

    public static string Serialize(IReadOnlyList<ThreadItem> items) =>
        JsonSerializer.Serialize(items.Select(Stored).ToArray(), Options);

    /// <summary>
    /// One item as it is written back to disk. The other half of the allow-list.
    /// </summary>
    /// <remarks>
    /// 'area' is written only when the item has one. Emitting "area": "" for every unlabelled
    /// item would rewrite all of them the first time any write touched the list -- a backfill
    /// by another name, and the whole point of the field is that a label was assigned rather
    /// than acquired. An absent key reads back as empty, which reads as (unfiled), so the
    /// round trip is stable in both directions.
    /// </remarks>
    private static JsonObject Stored(ThreadItem item)
    {
        JsonObject stored = new()
        {
            ["topic"] = item.Topic,
            ["status"] = item.Status,
            ["refs"] = new JsonArray([.. item.Refs.Select(r => (JsonNode)JsonValue.Create(r)!)]),
            ["next"] = item.Next,
            ["notes"] = item.Notes,
        };

        if (item.Area.Length > 0)
        {
            stored["area"] = item.Area;
        }

        return stored;
    }

    /// <summary>The area an item is filed under, or <see cref="Unfiled"/> where it has none.</summary>
    public static string AreaOf(ThreadItem item) => AreaOf(item.Area);

    /// <summary>The same resolution for an item as Show carries it.</summary>
    public static string AreaOf(ThreadShownItem item) => AreaOf(item.Area);

    private static string AreaOf(string area) => string.IsNullOrWhiteSpace(area) ? Unfiled : area;

    /// <summary>
    /// The topic in focus among the items given, or null. Nothing active is an ordinary state,
    /// not a fault.
    /// </summary>
    /// <remarks>
    /// SCOPED BY ITS ARGUMENT, which is the whole of how per-area focus is implemented: pass an
    /// area's items and you get that area's cursor, pass the list and you get whichever cursor
    /// happens to come first. Since 2026-09-06 nothing passes the whole list expecting an
    /// answer about the whole list, because there is no such single answer any more.
    /// </remarks>
    public static string? ActiveTopic(IEnumerable<ThreadItem> items) =>
        items.FirstOrDefault(i => i.IsActive)?.Topic;

    /// <summary>The topic in focus in ONE area, or null where that area has no cursor.</summary>
    /// <remarks>
    /// Areas are compared with Ordinal equality on the RESOLVED label, the same comparer
    /// <see cref="AreaCounts"/> groups by, so a cursor always belongs to exactly one row of the
    /// map. The read selectors match an area case-insensitively by substring, deliberately
    /// differently: that is a narrowing, and this is identity.
    /// </remarks>
    public static string? ActiveTopic(IEnumerable<ThreadItem> items, string area) =>
        ActiveTopic(items.Where(i => SameArea(i, area)));

    /// <summary>Whether an item is filed under the given RESOLVED area label.</summary>
    private static bool SameArea(ThreadItem item, string area) =>
        string.Equals(AreaOf(item), area, StringComparison.Ordinal);

    /// <summary>
    /// Every item holding focus, at most one per area.
    /// </summary>
    /// <remarks>
    /// The invariant is per-area since 2026-09-06, so this is a LIST rather than the single
    /// item it used to be. A file hand-edited into two cursors in one area is still possible;
    /// the callers that must resolve one item cope by counting distinct AREAS, so a corrupt
    /// pair inside one area behaves as the old global single did rather than becoming a new
    /// refusal nobody can act on.
    /// </remarks>
    private static List<ThreadItem> InFocus(IEnumerable<ThreadItem> items) =>
        [.. items.Where(i => i.IsActive)];

    /// <summary>Every cursor as "area: topic", for a refusal that names all of them.</summary>
    private static string Cursors(IEnumerable<ThreadItem> active) =>
        string.Join("; ", active.Select(i => $"{AreaOf(i)}: {i.Topic}"));

    /// <summary>Everything not yet completed. Done items stay in the file and out of the way.</summary>
    public static IReadOnlyList<ThreadItem> Live(IEnumerable<ThreadItem> items) =>
        [.. items.Where(i => !i.IsDone)];

    private static int LiveCount(string text) => Live(Parse(text)).Count;

    // ---- selecting ---------------------------------------------------------------------

    /// <summary>
    /// Resolves a selector to exactly one index, or throws.
    /// </summary>
    /// <remarks>
    /// Ambiguity is an error rather than a first-match guess: the operations that follow rewrite
    /// the file, and silently amending the wrong item is how notes get lost.
    ///
    /// Topic matching is case-insensitive substring, as -like "*topic*" was. The PowerShell
    /// implemented it with -like, so a topic containing * or ? behaved as a wildcard by
    /// accident; that edge is not reproduced, matching the same decision made for the catalog.
    ///
    /// Topic is the only selector: see <see cref="ThreadSelector"/> for why position was
    /// removed rather than corrected.
    /// </remarks>
    public static int Find(IReadOnlyList<ThreadItem> items, ThreadSelector selector)
    {
        if (string.IsNullOrEmpty(selector.Topic))
        {
            return IndexOfSoleActive(items);
        }

        List<int> matched = Matching(items, selector.Topic);

        if (matched.Count == 0)
        {
            throw new GraphException($"No item matches topic '{selector.Topic}'.");
        }

        if (matched.Count > 1)
        {
            string names = string.Join("; ", matched.Select(i => items[i].Topic));

            throw new GraphException(
                $"Topic '{selector.Topic}' is ambiguous -- it matches {matched.Count} items: {names}");
        }

        return matched[0];
    }

    /// <summary>
    /// Every item whose topic contains the given text, case-insensitively.
    /// </summary>
    /// <remarks>
    /// The single definition of what a topic match IS, so that the writing verbs and the
    /// reading ones cannot come to disagree about which item a caller named -- a selector that
    /// means one thing to update and another to show is worse than either alone.
    ///
    /// Substring rather than -like: the PowerShell used -like "*topic*", so a topic containing
    /// * or ? behaved as a pattern by accident. That edge is not reproduced, matching the same
    /// decision made for the catalog. A '*' here is a literal asterisk.
    /// </remarks>
    private static List<int> Matching(IReadOnlyList<ThreadItem> items, string topic) =>
    [
        .. Enumerable.Range(0, items.Count)
            .Where(i => items[i].Topic.Contains(topic, StringComparison.OrdinalIgnoreCase))
    ];

    /// <summary>
    /// The one item an EMPTY selector means, or a refusal naming every cursor.
    /// </summary>
    /// <remarks>
    /// "Whatever is active" was unambiguous while focus was global. Per-area it is a question
    /// with up to one answer per area, so this applies the same house rule an ambiguous topic
    /// gets: act when exactly one area holds focus, and otherwise REFUSE with every candidate
    /// named rather than resolve to a first match. Guessing here would complete or amend
    /// another repo's item, which is the concrete harm measured on 2026-09-06 -- completing a
    /// JanetHome item cleared focus for all four areas.
    ///
    /// Counted by distinct AREA, not by item: one area holding two cursors is a corrupt file
    /// rather than an ambiguous request, and behaving there as the old global single did is
    /// better than a refusal whose remedy is to hand-edit the store.
    /// </remarks>
    private static int IndexOfSoleActive(IReadOnlyList<ThreadItem> items)
    {
        List<int> active = [.. Enumerable.Range(0, items.Count).Where(i => items[i].IsActive)];

        if (active.Count == 0)
        {
            throw new GraphException(
                "No item is active, so there is nothing to act on. Pass a topic.");
        }

        string[] areas = [.. active.Select(i => AreaOf(items[i])).Distinct(StringComparer.Ordinal)];

        if (areas.Length > 1)
        {
            throw new GraphException(
                $"Focus is per area, and {areas.Length} areas hold it: " +
                Cursors(active.Select(i => items[i])) +
                ". An empty selector cannot mean all of them -- pass a topic to name the one " +
                "you meant.");
        }

        return active[0];
    }

    /// <summary>
    /// Parks whatever holds focus IN ONE AREA, and reports what it displaced.
    /// </summary>
    /// <remarks>
    /// The area argument is the change of 2026-09-06 and the whole point of it. Focus is one
    /// per area, not one per list: this list is shared by every repo on this machine, so a
    /// global park meant a session taking up its own work silently parked whatever three other
    /// sessions were holding. Measured that day: setting a JanetHome item active parked a
    /// gamehub session's item twice.
    ///
    /// Takes the RESOLVED area, so <see cref="Unfiled"/> is parked like any other group and
    /// there is no special case for the unlabelled items -- they are an area, and they get
    /// their own cursor.
    /// </remarks>
    private static string? ParkActive(List<ThreadItem> items, string area)
    {
        string? previous = null;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsActive && SameArea(items[i], area))
            {
                previous ??= items[i].Topic;
                items[i] = items[i] with { Status = Parked };
            }
        }

        return previous;
    }

    /// <summary>
    /// The ONE area a write's area argument names, or a refusal.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively by substring, as the read selectors are, but resolved to
    /// exactly one area or refused -- the same split <see cref="Find"/> and <see cref="Only"/>
    /// hold to. A write that cleared two areas' cursors because a substring reached both is the
    /// wrong-item write this whole file is arranged to prevent.
    /// </remarks>
    private static string SoleArea(IReadOnlyList<ThreadItem> items, string area)
    {
        string[] known =
        [
            .. Live(items).Select(AreaOf)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
        ];

        string[] matched =
            [.. known.Where(a => a.Contains(area, StringComparison.OrdinalIgnoreCase))];

        if (matched.Length == 1)
        {
            return matched[0];
        }

        if (matched.Length == 0)
        {
            throw new GraphException(
                $"No item is filed under an area matching '{area}'. " + (known.Length > 0
                    ? $"Areas in use: {string.Join(", ", known)}."
                    : "The list is empty, so no area is in use yet."));
        }

        throw new GraphException(
            $"Area '{area}' is ambiguous -- it matches {matched.Length} areas: " +
            string.Join(", ", matched) + ". Pass more of the one you meant.");
    }

    // ---- writing -----------------------------------------------------------------------

    /// <summary>
    /// Refuses a write whose notes or next would leave the item over its ceiling, or whose text
    /// is the body of a mangled tool call. Null means the field is not being written, and is
    /// neither measured nor scanned.
    /// </summary>
    /// <remarks>
    /// THE ONE PLACE. Add, Update with replacement notes and Update with appended notes each
    /// pass through here, so a check that one path forgot cannot exist.
    ///
    /// Two checks, two inputs. The ceiling measures <paramref name="notesAfter"/>, what the
    /// item WOULD hold -- an append's result -- because the bound is on the item. The markup
    /// guard scans <paramref name="notesWritten"/>, what the caller SENT -- an append's
    /// fragment -- because items whose notes describe the markup bug with the literal tags
    /// exist, the item tracking the guard among them, and must stay amendable.
    ///
    /// Only the fields being written are measured, deliberately. Items over the ceiling exist
    /// (the live list held several when this arrived, 2026-09-05) and must stay amendable in
    /// status, refs, area and next, or the ceiling would freeze exactly the items it was meant
    /// to shrink. The escape the refusal names -- move the long-form to a catalogued note and
    /// REPLACE notes with something shorter -- is itself a notes write, and it passes because
    /// the result is measured, not the item's past.
    ///
    /// The ceiling is read per call rather than cached, so <see cref="NotesBudget.EnvironmentVariable"/>
    /// takes effect on the next write without a restart, as the result budget's does.
    /// </remarks>
    private static void EnsureWritable(
        string topic, string? notesAfter, string? notesWritten, string? next, string? area)
    {
        MalformedInput.Ensure("notes", topic, notesWritten);
        MalformedInput.Ensure("next", topic, next);
        MalformedInput.Ensure("area", topic, area);

        int ceiling = NotesBudget.Current;

        if (notesAfter is not null && notesAfter.Length > ceiling)
        {
            throw new GraphException(NotesBudget.NotesRefusal(topic, notesAfter.Length, ceiling));
        }

        if (next is not null && next.Length > NotesBudget.NextCeiling)
        {
            throw new GraphException(NotesBudget.NextRefusal(topic, next.Length));
        }
    }

    private static T Write<T>(string? path, Func<List<ThreadItem>, T> operation)
        where T : IBatchedResult<T> =>
        WriteQueue.Submit(
            Resolve(path),
            text =>
            {
                List<ThreadItem> items = [.. Parse(text)];
                T result = operation(items);

                return (Serialize(items), result);
            },
            LiveCount,

            // A missing list is an ordinary starting state, not an error: nothing has been
            // recorded yet. The catalog is the opposite and says so.
            whenMissing: "[]");

    public static ThreadAddResult Add(
        string? path, string topic, string notes = "", string next = "",
        IReadOnlyList<string>? refs = null, bool active = false, string? area = null)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new GraphException("A thread item needs a topic.");
        }

        // Before the queue: nothing here depends on what is already on disk. Topic is scanned
        // on add alone; on update it is the stored item's, not text the caller sent.
        MalformedInput.Ensure("topic", topic, topic);
        EnsureWritable(topic, notesAfter: notes, notesWritten: notes, next, area);

        // Absent stays absent. An add that names no area produces an unfiled item, rather than
        // one filed under whatever the caller was last working on -- and (unfiled) is the area
        // whose cursor this add competes for, exactly as a labelled one would be.
        string filed = (area ?? string.Empty).Trim();

        return Write(path, items =>
        {
            if (items.Any(i => string.Equals(i.Topic, topic, StringComparison.OrdinalIgnoreCase)))
            {
                throw new GraphException(
                    $"An item with topic '{topic}' already exists. Update it instead of adding a second.");
            }

            if (active)
            {
                // The NEW item's own area, and no other. An add that takes focus in JanetHome
                // leaves a gamehub session's cursor exactly where it was.
                ParkActive(items, AreaOf(filed));
            }

            items.Add(new ThreadItem
            {
                Topic = topic,
                Status = active ? Active : Parked,
                Refs = refs ?? [],
                Next = next,
                Notes = notes,
                Area = filed,
            });

            IReadOnlyList<ThreadItem> live = Live(items);

            return new ThreadAddResult(topic, ActiveTopic(live, AreaOf(filed)), live.Count);
        });
    }

    /// <summary>
    /// Amends one item. Null means untouched; empty string means clear.
    /// </summary>
    /// <remarks>
    /// The distinction is the point: clearing 'next' is a legitimate request, so "not supplied"
    /// and "supplied as empty" cannot be the same thing. The PowerShell read
    /// $PSBoundParameters to tell them apart; here it is nullability.
    /// </remarks>
    public static ThreadUpdateResult Update(
        string? path, ThreadSelector selector, string? notes = null, string? next = null,
        IReadOnlyList<string>? refs = null, string? status = null,
        bool appendNotes = false, bool appendRefs = false, string? area = null)
    {
        if (notes is null && next is null && refs is null && area is null
            && string.IsNullOrEmpty(status))
        {
            throw new GraphException("Nothing to change. Pass notes, next, refs, status, or area.");
        }

        if (!string.IsNullOrEmpty(status) && !Statuses.Contains(status, StringComparer.OrdinalIgnoreCase))
        {
            throw new GraphException(
                $"Unknown status '{status}'. Valid statuses: {string.Join(", ", Statuses)}.");
        }

        return Write(path, items =>
        {
            if (items.Count == 0)
            {
                throw new GraphException("The thread item list is empty.");
            }

            int target = Find(items, selector);
            ThreadItem item = items[target];
            List<string> changed = [];

            // The RESULT of an append is what is measured, not the fragment: the ceiling is on
            // what the item holds, and an append is how an item gets there a paragraph at a
            // time. Measured inside the queue, against the text this batch actually read.
            string? resultingNotes = notes is null
                ? null
                : appendNotes && item.Notes.Length > 0 ? item.Notes + "\n\n" + notes : notes;

            EnsureWritable(item.Topic, notesAfter: resultingNotes, notesWritten: notes, next, area);

            if (resultingNotes is not null)
            {
                item = item with { Notes = resultingNotes };
                changed.Add("notes");
            }

            if (next is not null)
            {
                item = item with { Next = next };
                changed.Add("next");
            }

            if (refs is not null)
            {
                // Concatenated, not merged: unlike catalog tags, a repeated ref is the caller's
                // to decide about. Deduplicating here would quietly drop one they meant twice.
                item = item with { Refs = appendRefs ? [.. item.Refs, .. refs] : refs };
                changed.Add("refs");
            }

            if (area is not null)
            {
                // Empty clears, as everywhere else here: an item filed by mistake has to be
                // returnable to (unfiled), and there is no other way to say that.
                item = item with { Area = area.Trim() };
                changed.Add("area");
            }

            if (!string.IsNullOrEmpty(status))
            {
                if (string.Equals(status, Active, StringComparison.OrdinalIgnoreCase))
                {
                    // Scoped to the STORED area rather than a pending one: the re-read below
                    // is what the item ends up as, so parking any other area's cursor would
                    // park a group this item is not going to join.
                    ParkActive(items, AreaOf(items[target]));
                    item = items[target];
                }

                item = item with { Status = status };
                changed.Add("status");
            }

            items[target] = item;

            return new ThreadUpdateResult(item.Topic, changed, Live(items).Count);
        });
    }

    public static ThreadCompleteResult Complete(string? path, ThreadSelector selector) =>
        Write(path, items =>
        {
            if (items.Count == 0)
            {
                throw new GraphException("The thread item list is empty; there is nothing to complete.");
            }

            int target = Find(items, selector);

            if (items[target].IsDone)
            {
                throw new GraphException($"'{items[target].Topic}' is already completed.");
            }

            // A status change, not a removal. Completing used to delete the item, so finishing
            // work erased the record of having done it.
            items[target] = items[target] with { Status = Done };

            IReadOnlyList<ThreadItem> live = Live(items);

            // THIS item's area, and only that one. Completing is how an area's cursor is
            // cleared, and before 2026-09-06 it cleared every area's -- finishing a JanetHome
            // item told three other sessions that nothing was in hand.
            return new ThreadCompleteResult(
                items[target].Topic, ActiveTopic(live, AreaOf(items[target])), live.Count);
        });

    /// <summary>
    /// Moves focus within ONE area, or clears one area's focus when the selector is null.
    /// </summary>
    /// <remarks>
    /// The invariant is at most one active item PER AREA since 2026-09-06, so both halves of
    /// this are scoped. Taking focus parks only the target's own area; clearing takes an
    /// <paramref name="area"/>, and without one falls back to the ambiguity rule
    /// <see cref="IndexOfSoleActive"/> states -- act when exactly one area holds focus, refuse
    /// with every cursor named when several do.
    ///
    /// Find runs BEFORE the park now, where it used to run after. It has to: the area to park
    /// is the target's, and there is no way to know it before the target is resolved.
    /// </remarks>
    /// <param name="path">List file, or null for the well-known one.</param>
    /// <param name="selector">The item to focus on, or null to clear focus.</param>
    /// <param name="area">Which area's focus to clear. Only meaningful with a null selector.</param>
    public static ThreadActiveResult SetActive(
        string? path, ThreadSelector? selector, string? area = null) =>
        Write(path, items =>
        {
            if (selector is not null)
            {
                if (!string.IsNullOrWhiteSpace(area))
                {
                    throw new GraphException(
                        "area names which cursor to CLEAR and applies only with none. An item " +
                        "takes focus in the area it is filed under, so passing both says two " +
                        "different things about which area is meant.");
                }

                int target = Find(items, selector);

                if (items[target].IsDone)
                {
                    throw new GraphException(
                        $"'{items[target].Topic}' is completed. Reopen it by setting its status to parked first.");
                }

                string? displaced = ParkActive(items, AreaOf(items[target]));

                items[target] = items[target] with { Status = Active };

                return new ThreadActiveResult(items[target].Topic, displaced, Live(items).Count);
            }

            return new ThreadActiveResult(null, ClearFocus(items, area), Live(items).Count);
        });

    /// <summary>Parks one area's cursor: the named one, or the only one there is.</summary>
    /// <remarks>
    /// Nothing in focus anywhere stays what it always was -- a success reporting no previous
    /// topic. Clearing focus that is not there is not a failure, and refusing it would make
    /// "leave me with nothing active" depend on what the last session did.
    /// </remarks>
    private static string? ClearFocus(List<ThreadItem> items, string? area)
    {
        if (!string.IsNullOrWhiteSpace(area))
        {
            return ParkActive(items, SoleArea(items, area));
        }

        List<ThreadItem> active = InFocus(items);
        string[] areas = [.. active.Select(AreaOf).Distinct(StringComparer.Ordinal)];

        if (areas.Length > 1)
        {
            throw new GraphException(
                $"Focus is per area, and {areas.Length} areas hold it: " + Cursors(active) +
                ". Clearing without an area cannot mean all of them -- pass area to name the " +
                "one you meant.");
        }

        return areas.Length == 0 ? null : ParkActive(items, areas[0]);
    }

    /// <summary>
    /// Reads the list without writing it. Never throws for a BAD FILE; a bad selector is
    /// different, and does.
    /// </summary>
    /// <remarks>
    /// The two failures are not the same kind of thing, and collapsing them would make both
    /// unreadable. A corrupt or unreadable list is reported in-band through 'error', because
    /// this runs in the startup path and a mangled temp file must not stop a session from
    /// beginning -- 'error' means, and only means, "the list could not be read". A topic that
    /// matches nothing, or an area nothing is filed under, is a CALLER error HERE: there is no
    /// degraded answer Show could give, and returning an empty list would say "no such work is
    /// open", which is a different and false claim. Those throw <see cref="GraphException"/>,
    /// which Surfaced.Filter re-throws as McpException so the message survives the MCP boundary
    /// intact.
    ///
    /// 'active' names the focus of THIS ANSWER'S SCOPE since 2026-09-06: the area selector's
    /// cursor when one was passed, and null when none was. Show has no areas map to carry the
    /// other cursors, so an unnarrowed read here says only "you did not narrow" -- pass area,
    /// or read <see cref="Report"/>, whose envelope carries every area's cursor at once.
    ///
    /// Show refuses an unknown area and <see cref="Report"/> does not, since 2026-09-04, and
    /// the asymmetry is deliberate rather than an oversight -- see Report's own remarks for
    /// why the two envelopes can afford different answers to the same miss.
    ///
    /// A read failure wins over a selector: with nothing read there is nothing to select from,
    /// and "no item matches 'x'" would name the wrong cause.
    ///
    /// Neither selector is capped. An explicit selector means the caller already knows what
    /// they asked for, and truncating it would hide answers -- the rule CatalogQuery and ApiDoc
    /// both state where they cap free-text ranking and nothing else.
    ///
    /// Notes come back as a LEAD by default since 2026-09-05 -- the same <see cref="Lead"/> the
    /// report carries, so the two views agree -- with the stored size and a truncation flag
    /// beside each. <paramref name="full"/> returns the whole text, and is allowed ONLY with a
    /// topic. That is the invariant note.thread-item-projection records: notes are returned one
    /// item at a time, and a flag that expanded them across an area or the whole list would
    /// re-create the original defect exactly, one option away from the default.
    /// </remarks>
    /// <param name="path">List file, or null for the well-known one.</param>
    /// <param name="all">Include completed items.</param>
    /// <param name="topic">Case-insensitive substring naming exactly ONE item.</param>
    /// <param name="area">Case-insensitive substring narrowing to one area's items.</param>
    /// <param name="full">Carry the one selected item's notes whole. Refused without a topic.</param>
    public static ThreadShowResult Show(
        string? path, bool all = false, string? topic = null, string? area = null, bool full = false)
    {
        if (full && string.IsNullOrWhiteSpace(topic))
        {
            throw new GraphException(
                "full expands notes, and notes are returned one item at a time: pass topic to " +
                "name the ONE item you want whole. Expanding notes across an area or the whole " +
                "list is refused because it re-creates the oversized read that the lead and " +
                "thread_report exist to prevent (note.thread-item-projection). Read the map " +
                "with thread_report, then ask for one item by topic.");
        }

        (IReadOnlyList<ThreadItem> items, string? error) = TryRead(path);

        Projection shown = Project(items, error, all, topic, area);

        return new ThreadShowResult(
            shown.Items.Count, shown.Active, [.. shown.Items.Select(i => Shown(i, full))], shown.Error);
    }

    /// <summary>One stored item as Show carries it: lead or whole notes, and the size either way.</summary>
    private static ThreadShownItem Shown(ThreadItem item, bool full)
    {
        string notes = full ? item.Notes : Lead(item.Notes);

        return new ThreadShownItem(
            item.Topic, item.Status, item.Refs, item.Next, notes, item.Area,
            item.Notes.Length, !string.Equals(notes, item.Notes, StringComparison.Ordinal));
    }

    /// <summary>
    /// What both readers derive their answer from: focus, then the filtered and selected items,
    /// still as stored.
    /// </summary>
    /// <remarks>
    /// Private, and not <see cref="ThreadShowResult"/>, because Show projects notes on the way
    /// out and Report needs them whole to measure and lead them. One shared projection with two
    /// renderings is what keeps the two views agreeing about membership and focus.
    /// </remarks>
    private sealed record Projection(string? Active, IReadOnlyList<ThreadItem> Items, string? Error);

    /// <summary>
    /// The list, or an empty one plus the reason it could not be read.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="Show"/> so that <see cref="Report"/> can read the file ONCE and
    /// derive both its narrowed answer and its whole-list map from the same bytes. Two reads
    /// could straddle another session's write and hand back a map that disagrees with the items
    /// beside it.
    /// </remarks>
    private static (IReadOnlyList<ThreadItem> Items, string? Error) TryRead(string? path)
    {
        try
        {
            return (Read(path), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>Show's projection of an already-read list: focus, then filter, then select.</summary>
    /// <param name="refuseUnknownArea">
    /// False lets an area nothing is filed under narrow to nothing instead of throwing. Only
    /// <see cref="Report"/> passes false: its envelope carries the areas map, so an empty answer
    /// there still says where the work is. <see cref="Show"/> keeps the default.
    /// </param>
    private static Projection Project(
        IReadOnlyList<ThreadItem> items,
        string? error,
        bool all,
        string? topic,
        string? area,
        bool refuseUnknownArea = true)
    {
        if (error is not null)
        {
            return new Projection(null, [], error);
        }

        IReadOnlyList<ThreadItem> shown = all ? items : Live(items);

        // 'active' is THIS ANSWER'S scope since 2026-09-06, and the scope is the AREA selector
        // -- not the topic one, which narrows to an item rather than to a group of them.
        // Unnarrowed it is null, because focus is now one cursor per area and a scalar cannot
        // carry four: reporting any one of them is precisely how the startup brief came to
        // narrow to JanetHome and name a gamehub item beside it. The complete answer when
        // nothing narrowed is the report's 'areas' map, which carries every cursor.
        //
        // Computed over the area-filtered list rather than looked up by label, so a selector
        // broad enough to span two areas reports focus from the set it actually returned.
        string? active = null;

        if (!string.IsNullOrWhiteSpace(area))
        {
            shown = InArea(shown, area, refuseUnknownArea);
            active = ActiveTopic(shown);
        }

        if (!string.IsNullOrWhiteSpace(topic))
        {
            shown = [Only(shown, topic)];
        }

        return new Projection(active, shown, error);
    }

    /// <summary>
    /// Narrows to one area, refusing an area nothing is filed under unless told to tolerate it.
    /// </summary>
    /// <remarks>
    /// A NARROWING selector, so case-insensitive Contains -- the house split is that an
    /// identity selector uses equality and a narrowing one uses containment. Matched against
    /// the RESOLVED area, so '(unfiled)' reaches the unlabelled items with no special case in
    /// the matcher: they are a group like any other, and the point of the design is that they
    /// stay one group rather than being distributed into plausible neighbours.
    ///
    /// A miss names the areas actually in use, because the likeliest cause is a label that
    /// reads differently from how it was stored. That message is the best answer available to
    /// a caller whose envelope has nowhere to put "and here is where the work actually is";
    /// <paramref name="refuseUnknown"/> false is for the caller whose envelope does.
    /// </remarks>
    /// <param name="items">Already filtered by 'all'.</param>
    /// <param name="area">Case-insensitive substring matched against the resolved area.</param>
    /// <param name="refuseUnknown">
    /// True throws on a miss; false returns the empty list. Decided 2026-09-04 by observation:
    /// startup narrows the report to the session's own project, so opening a session in a repo
    /// with nothing on the list turned the whole run entry into status=error with an exception
    /// text where a report belonged.
    /// </param>
    private static IReadOnlyList<ThreadItem> InArea(
        IReadOnlyList<ThreadItem> items, string area, bool refuseUnknown = true)
    {
        IReadOnlyList<ThreadItem> matched =
            [.. items.Where(i => AreaOf(i).Contains(area, StringComparison.OrdinalIgnoreCase))];

        if (matched.Count > 0 || !refuseUnknown)
        {
            return matched;
        }

        string[] known =
        [
            .. items.Select(AreaOf)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
        ];

        throw new GraphException(
            $"No item is filed under an area matching '{area}'. " + (known.Length > 0
                ? $"Areas in use: {string.Join(", ", known)}. Set one with thread_update."
                : "The list is empty, so no area is in use yet."));
    }

    /// <summary>
    /// Resolves a topic to exactly ONE item, or throws.
    /// </summary>
    /// <remarks>
    /// The same contract <see cref="Find"/> holds the writing verbs to, and deliberately so:
    /// ambiguity is refused with every candidate named, never resolved to a first match. A
    /// reader that quietly showed the first of two matches would teach its caller that the
    /// topic they typed identifies one item, which is exactly the belief that makes the next
    /// update rewrite the wrong one.
    /// </remarks>
    private static ThreadItem Only(IReadOnlyList<ThreadItem> items, string topic)
    {
        List<int> matched = Matching(items, topic);

        if (matched.Count == 1)
        {
            return items[matched[0]];
        }

        if (matched.Count == 0)
        {
            throw new GraphException(
                $"No item matches topic '{topic}'. Call this without a topic to see what is " +
                "on the list, or pass all=true if you meant a completed item.");
        }

        throw new GraphException(
            $"Topic '{topic}' is ambiguous -- it matches {matched.Count} items: " +
            string.Join("; ", matched.Select(i => items[i].Topic)) +
            ". Pass more of the one you meant.");
    }

    /// <summary>How much of a note the reporter carries before it is doing the list's job for it.</summary>
    private const int LeadLength = 200;

    /// <summary>
    /// The first non-empty line of a note, trimmed and capped.
    /// </summary>
    /// <remarks>
    /// First NON-EMPTY, not first: notes accumulated by appending start with a blank line, so the
    /// literal first line is empty for most items that have anything worth reading.
    /// </remarks>
    public static string Lead(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return string.Empty;
        }

        string? line = notes
            .Replace("\r\n", "\n")
            .Split('\n')
            .FirstOrDefault(l => l.Trim().Length > 0);

        if (line is null)
        {
            return string.Empty;
        }

        string trimmed = line.Trim();

        return trimmed.Length > LeadLength ? trimmed[..LeadLength] + "..." : trimmed;
    }

    /// <summary>
    /// The list without the note bodies. Never throws, for the same reason Show does not.
    /// </summary>
    /// <remarks>
    /// Reads through Show so the two cannot disagree about what "live" means, about which item
    /// is in focus, about which selector matched, or about how a corrupt file is reported. The
    /// only difference is what is carried back -- and, since 2026-09-04, what an unmatched
    /// AREA does. An unresolvable 'topic' still throws from here for the same reason it throws
    /// from there: there is exactly one right answer to "show me this item", and no envelope
    /// can stand in for it. An area with nothing filed under it is not that. It is a legitimate
    /// empty answer, and THIS envelope can say so completely, because 'areas' is computed over
    /// the whole list before any selector: zero items, plus a map naming every area that does
    /// have open work. Show has no such field, so the same empty answer there would carry no
    /// information at all and the throw's "Areas in use: ..." is strictly better. Lori's call
    /// on 2026-09-04, from opening a session in a repo with nothing on the list: startup
    /// narrows this report to the session's own project, and the run entry came back
    /// status=error with an exception text where a report belonged -- a repo with nothing in it
    /// should return a json like everything else.
    ///
    /// 'notesLength' totals the items ACTUALLY RETURNED, so a narrowed report states what its
    /// own answer withheld rather than what the whole list holds. 'areas' is the opposite: it
    /// is computed over the whole open list, before either selector, so the narrowed answer
    /// still carries a map of what it left out. 'active' followed that rule until 2026-09-06
    /// and now does not: focus is one cursor per area, so the scalar reports the narrowed
    /// scope's cursor and null when nothing narrowed, while 'areas' carries all of them.
    ///
    /// 'lead' false drops notesLead from every item. Measured 2026-09-04 on this machine's
    /// list narrowed to JanetHome: the leads were 1,827 of a 5,430-character report inside a
    /// 9,969-character startup brief, and the brief's budget is about 8,000. notesLength stays,
    /// so what was withheld is still counted; 'next' stays, because it is the field the report
    /// exists to deliver.
    /// </remarks>
    public static ThreadReportResult Report(
        string? path, bool all = false, string? topic = null, string? area = null, bool lead = true)
    {
        (IReadOnlyList<ThreadItem> items, string? error) = TryRead(path);

        Projection shown = Project(items, error, all, topic, area, refuseUnknownArea: false);

        return new ThreadReportResult(
            shown.Items.Count,
            shown.Active,
            AreaCounts(items),
            [.. shown.Items.Select(i => new ThreadReportItem(
                i.Topic, i.Status, AreaOf(i), i.Refs, i.Next, lead ? Lead(i.Notes) : null, i.Notes.Length))],
            shown.Items.Sum(i => i.Notes.Length),
            shown.Error);
    }

    /// <summary>
    /// One entry per area with OPEN items -- its count and its cursor -- sorted by name, over
    /// the whole list.
    /// </summary>
    /// <remarks>
    /// Open items only, whatever 'all' says: the map answers "where is the rest of the backlog",
    /// and finished work is not backlog. Grouped on the RESOLVED area so the unlabelled items
    /// are one group named <see cref="Unfiled"/>, and absent entirely when none are open -- a
    /// zero row would read as a category that exists, which is the guess this field avoids.
    /// Ordered case-insensitively, the way <see cref="InArea"/> lists the areas in use, so the
    /// two views of the same set agree.
    ///
    /// The cursor comes from the group, which is what makes this map the complete answer to
    /// "what is in focus" now that the envelope's own 'active' describes only a narrowed scope.
    /// Grouping and focus scoping share the Ordinal comparer deliberately: were they different,
    /// an item could hold focus in a group no row corresponds to, and the map would be missing
    /// a cursor while claiming to carry all of them.
    /// </remarks>
    public static IReadOnlyList<ThreadAreaCount> AreaCounts(IReadOnlyList<ThreadItem> items) =>
    [
        .. Live(items)
            .GroupBy(AreaOf, StringComparer.Ordinal)
            .Select(g => new ThreadAreaCount(g.Key, g.Count(), ActiveTopic(g)))
            .OrderBy(a => a.Area, StringComparer.OrdinalIgnoreCase)
    ];
}
