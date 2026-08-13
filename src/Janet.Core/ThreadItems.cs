using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// One investigation topic. Five fields, three of which are distinct roles rather than stages
/// of one idea.
/// </summary>
/// <remarks>
/// refs -- context that has earned a research.json node.
/// next -- the resume cursor: the one thing to do first on return.
/// notes -- detail too small or too fresh to be worth a node.
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

    public bool IsActive => string.Equals(Status, ThreadItems.Active, StringComparison.OrdinalIgnoreCase);

    public bool IsDone => string.Equals(Status, ThreadItems.Done, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Which item an operation acts on: an index, a topic, or whatever is active.</summary>
public sealed record ThreadSelector
{
    public string Topic { get; init; } = string.Empty;
    public int Index { get; init; } = -1;
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
    /// Parses the list, normalising every item to the five-field shape.
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

    private static ThreadItem Normalise(JsonObject entry) => new()
    {
        Topic = Text(entry, "topic", string.Empty),
        Status = Text(entry, "status", Parked),
        Refs = Strings(entry, "refs"),
        Next = Text(entry, "next", string.Empty),
        Notes = Text(entry, "notes", string.Empty),
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
        JsonSerializer.Serialize(
            items.Select(i => new JsonObject
            {
                ["topic"] = i.Topic,
                ["status"] = i.Status,
                ["refs"] = new JsonArray([.. i.Refs.Select(r => (JsonNode)JsonValue.Create(r)!)]),
                ["next"] = i.Next,
                ["notes"] = i.Notes,
            }).ToArray(),
            Options);

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
    /// </remarks>
    public static int Find(IReadOnlyList<ThreadItem> items, ThreadSelector selector)
    {
        if (selector.Index >= 0)
        {
            if (selector.Index >= items.Count)
            {
                throw new GraphException(
                    $"Index {selector.Index} is out of range; the list holds {items.Count} item(s).");
            }

            return selector.Index;
        }

        if (string.IsNullOrEmpty(selector.Topic))
        {
            int active = IndexOfActive(items);

            return active >= 0
                ? active
                : throw new GraphException(
                    "No item is active, so there is nothing to act on. Pass a topic or an index.");
        }

        List<int> matched =
        [
            .. Enumerable.Range(0, items.Count)
                .Where(i => items[i].Topic.Contains(selector.Topic, StringComparison.OrdinalIgnoreCase))
        ];

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
        IReadOnlyList<string>? refs = null, bool active = false)
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
        bool appendNotes = false, bool appendRefs = false)
    {
        if (notes is null && next is null && refs is null && string.IsNullOrEmpty(status))
        {
            throw new GraphException("Nothing to change. Pass notes, next, refs, or status.");
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
    /// Reads the list without writing it. Never throws for a bad file.
    /// </summary>
    /// <remarks>
    /// This runs in the startup path, so a corrupt list is reported in-band and startup carries
    /// on. Throwing here would mean a mangled temp file could stop a session from beginning.
    /// </remarks>
    public static ThreadShowResult Show(string? path, bool all = false)
    {
        IReadOnlyList<ThreadItem> items;
        string? error = null;

        try
        {
            items = Read(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            items = [];
            error = ex.Message;
        }

        if (!all)
        {
            items = Live(items);
        }

        return new ThreadShowResult(items.Count, ActiveTopic(items), items, error);
    }
}
