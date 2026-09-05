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
    int Count, string? Active, IReadOnlyList<ThreadItem> Items, string? Error);

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

/// <summary>One area and how many OPEN items are filed under it.</summary>
/// <remarks>
/// Open, never total: the map exists so a narrowed report still says where the rest of the
/// backlog is, and completed items are not backlog. Area is the resolved label, so the group of
/// unlabelled items appears here as <see cref="ThreadItems.Unfiled"/> like any other.
/// </remarks>
public sealed record ThreadAreaCount(string Area, int Open);

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
/// count, sorted by name -- and like Active it ignores the selectors. Added 2026-09-04 so that a
/// report narrowed to one project still carries the shape of the backlog it left out: the
/// startup brief narrows to the session's own area, and without this the other projects' work
/// would simply vanish from it, which is the silent omission the envelope otherwise avoids.
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
    public static string AreaOf(ThreadItem item) =>
        string.IsNullOrWhiteSpace(item.Area) ? Unfiled : item.Area;

    /// <summary>The topic in focus, or null. Nothing active is an ordinary state, not a fault.</summary>
    public static string? ActiveTopic(IEnumerable<ThreadItem> items) =>
        items.FirstOrDefault(i => i.IsActive)?.Topic;

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
            int active = IndexOfActive(items);

            return active >= 0
                ? active
                : throw new GraphException(
                    "No item is active, so there is nothing to act on. Pass a topic.");
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

    private static int IndexOfActive(IReadOnlyList<ThreadItem> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsActive)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Parks whatever is active. Focus is single, so taking it always means yielding it.</summary>
    private static string? ParkActive(List<ThreadItem> items)
    {
        string? previous = null;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsActive)
            {
                previous ??= items[i].Topic;
                items[i] = items[i] with { Status = Parked };
            }
        }

        return previous;
    }

    // ---- writing -----------------------------------------------------------------------

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

        return Write(path, items =>
        {
            if (items.Any(i => string.Equals(i.Topic, topic, StringComparison.OrdinalIgnoreCase)))
            {
                throw new GraphException(
                    $"An item with topic '{topic}' already exists. Update it instead of adding a second.");
            }

            if (active)
            {
                ParkActive(items);
            }

            items.Add(new ThreadItem
            {
                Topic = topic,
                Status = active ? Active : Parked,
                Refs = refs ?? [],
                Next = next,
                Notes = notes,

                // Absent stays absent. An add that names no area produces an unfiled item,
                // rather than one filed under whatever the caller was last working on.
                Area = (area ?? string.Empty).Trim(),
            });

            IReadOnlyList<ThreadItem> live = Live(items);

            return new ThreadAddResult(topic, ActiveTopic(live), live.Count);
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

            if (notes is not null)
            {
                item = item with
                {
                    Notes = appendNotes && item.Notes.Length > 0 ? item.Notes + "\n\n" + notes : notes,
                };

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
                    ParkActive(items);
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

            return new ThreadCompleteResult(items[target].Topic, ActiveTopic(live), live.Count);
        });

    /// <summary>Moves focus, or clears it when the selector is null.</summary>
    public static ThreadActiveResult SetActive(string? path, ThreadSelector? selector) =>
        Write(path, items =>
        {
            string? previous = ParkActive(items);
            string? active = null;

            if (selector is not null)
            {
                int target = Find(items, selector);

                if (items[target].IsDone)
                {
                    throw new GraphException(
                        $"'{items[target].Topic}' is completed. Reopen it by setting its status to parked first.");
                }

                items[target] = items[target] with { Status = Active };
                active = items[target].Topic;
            }

            return new ThreadActiveResult(active, previous, Live(items).Count);
        });

    /// <summary>
    /// Reads the list without writing it. Never throws for a BAD FILE; a bad selector is
    /// different, and does.
    /// </summary>
    /// <remarks>
    /// The two failures are not the same kind of thing, and collapsing them would make both
    /// unreadable. A corrupt or unreadable list is reported in-band through 'error', because
    /// this runs in the startup path and a mangled temp file must not stop a session from
    /// beginning -- 'error' means, and only means, "the list could not be read". A topic that
    /// matches nothing, or an area nothing is filed under, is a CALLER error: there is no
    /// degraded answer to give, and returning an empty list would say "no such work is open",
    /// which is a different and false claim. Those throw <see cref="GraphException"/>, which
    /// Surfaced.Filter re-throws as McpException so the message survives the MCP boundary
    /// intact. Startup passes no selector, so the never-throws property is preserved exactly
    /// where it is load-bearing.
    ///
    /// A read failure wins over a selector: with nothing read there is nothing to select from,
    /// and "no item matches 'x'" would name the wrong cause.
    ///
    /// Neither selector is capped. An explicit selector means the caller already knows what
    /// they asked for, and truncating it would hide answers -- the rule CatalogQuery and ApiDoc
    /// both state where they cap free-text ranking and nothing else.
    /// </remarks>
    /// <param name="path">List file, or null for the well-known one.</param>
    /// <param name="all">Include completed items.</param>
    /// <param name="topic">Case-insensitive substring naming exactly ONE item.</param>
    /// <param name="area">Case-insensitive substring narrowing to one area's items.</param>
    public static ThreadShowResult Show(
        string? path, bool all = false, string? topic = null, string? area = null)
    {
        (IReadOnlyList<ThreadItem> items, string? error) = TryRead(path);

        return Project(items, error, all, topic, area);
    }

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
    private static ThreadShowResult Project(
        IReadOnlyList<ThreadItem> items, string? error, bool all, string? topic, string? area)
    {
        // Over the UNPROJECTED list, and this is the whole point of the field. 'active' means
        // "the focus of the list", not "the focus of this answer": computing it after a
        // selector had run would report null whenever the caller asked about some other item,
        // and a reader would correctly conclude from that envelope that nothing is in focus.
        // Nothing catches this by accident -- no caller that passes a selector existed before
        // these selectors did.
        string? active = ActiveTopic(items);

        if (error is not null)
        {
            return new ThreadShowResult(0, active, [], error);
        }

        IReadOnlyList<ThreadItem> shown = all ? items : Live(items);

        if (!string.IsNullOrWhiteSpace(area))
        {
            shown = InArea(shown, area);
        }

        if (!string.IsNullOrWhiteSpace(topic))
        {
            shown = [Only(shown, topic)];
        }

        return new ThreadShowResult(shown.Count, active, shown, error);
    }

    /// <summary>
    /// Narrows to one area, refusing an area nothing is filed under.
    /// </summary>
    /// <remarks>
    /// A NARROWING selector, so case-insensitive Contains -- the house split is that an
    /// identity selector uses equality and a narrowing one uses containment. Matched against
    /// the RESOLVED area, so '(unfiled)' reaches the unlabelled items with no special case in
    /// the matcher: they are a group like any other, and the point of the design is that they
    /// stay one group rather than being distributed into plausible neighbours.
    ///
    /// A miss names the areas actually in use, because the likeliest cause is a label that
    /// reads differently from how it was stored.
    /// </remarks>
    private static IReadOnlyList<ThreadItem> InArea(IReadOnlyList<ThreadItem> items, string area)
    {
        IReadOnlyList<ThreadItem> matched =
            [.. items.Where(i => AreaOf(i).Contains(area, StringComparison.OrdinalIgnoreCase))];

        if (matched.Count > 0)
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
    /// only difference is what is carried back. That includes the selectors: they are Show's,
    /// unchanged, and a bad one throws from here for the same reason it throws from there.
    ///
    /// 'notesLength' totals the items ACTUALLY RETURNED, so a narrowed report states what its
    /// own answer withheld rather than what the whole list holds. 'areas' is the opposite: it
    /// is computed over the whole open list, before either selector, so the narrowed answer
    /// still carries a map of what it left out -- the same rule 'active' follows.
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

        ThreadShowResult shown = Project(items, error, all, topic, area);

        return new ThreadReportResult(
            shown.Count,
            shown.Active,
            AreaCounts(items),
            [.. shown.Items.Select(i => new ThreadReportItem(
                i.Topic, i.Status, AreaOf(i), i.Refs, i.Next, lead ? Lead(i.Notes) : null, i.Notes.Length))],
            shown.Items.Sum(i => i.Notes.Length),
            shown.Error);
    }

    /// <summary>
    /// One entry per area with OPEN items, sorted by name, over the whole list.
    /// </summary>
    /// <remarks>
    /// Open items only, whatever 'all' says: the map answers "where is the rest of the backlog",
    /// and finished work is not backlog. Grouped on the RESOLVED area so the unlabelled items
    /// are one group named <see cref="Unfiled"/>, and absent entirely when none are open -- a
    /// zero row would read as a category that exists, which is the guess this field avoids.
    /// Ordered case-insensitively, the way <see cref="InArea"/> lists the areas in use, so the
    /// two views of the same set agree.
    /// </remarks>
    public static IReadOnlyList<ThreadAreaCount> AreaCounts(IReadOnlyList<ThreadItem> items) =>
    [
        .. Live(items)
            .GroupBy(AreaOf, StringComparer.Ordinal)
            .Select(g => new ThreadAreaCount(g.Key, g.Count()))
            .OrderBy(a => a.Area, StringComparer.OrdinalIgnoreCase)
    ];
}
