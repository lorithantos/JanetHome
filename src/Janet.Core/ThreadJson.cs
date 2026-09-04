using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// The thread-item envelopes, in the shape the PowerShell emitted them.
/// </summary>
/// <remarks>
/// Field order is the order ConvertTo-Json produced from each PSCustomObject, and it is kept
/// because these envelopes have consumers: Show's is captured by startup itself, under
/// 'threadStack' in startup-manifest.json. An envelope that reordered or renamed a field would
/// break a session's first move, so the shape is asserted against recorded output rather than
/// re-derived.
///
/// One field is added rather than preserved: 'batched'. See the note on it below.
/// </remarks>
public static class ThreadJson
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        // Matches ConvertTo-Json -Compress, which leaves &, <, > and apostrophes alone.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };

    /// <summary>
    /// How many requests landed in the write this result came from.
    /// </summary>
    /// <remarks>
    /// The one addition to the PowerShell's envelopes, and deliberate: a caller reading a count
    /// of 3 should be able to tell that it counts other writers' items too. Always present, and
    /// 1 for an uncontended write, so the shape never varies.
    /// </remarks>
    private const string Batched = "batched";

    /// <summary>
    /// Format number of the reporter's envelope, stamped into every response.
    /// </summary>
    /// <remarks>
    /// Bumped by a format change and by nothing else. Test-OutputContracts reads it from the
    /// envelope and cross-checks it against contracts\thread-report.schema.json, so code and
    /// schema cannot drift apart silently -- a field added here without a bump there is caught,
    /// and so is the reverse. The older thread envelopes carry no such number; they are pinned
    /// by recorded goldens instead, which is why only this one stamps itself.
    ///
    /// 1 -> 2 on 2026-09-03: report items gained 'area', the stored project label the report
    /// can now be narrowed by. A field added to the envelope is a format change, so this moved
    /// and contracts\thread-report.schema.json moved with it.
    /// </remarks>
    private const int ReportContract = 2;

    public static string Serialize(ThreadAddResult result, bool pretty = false) => Render(new JsonObject
    {
        ["added"] = result.Added,
        ["count"] = result.Count,
        ["active"] = result.Active,
        [Batched] = result.Batched,
    }, pretty);

    public static string Serialize(ThreadUpdateResult result, bool pretty = false) => Render(new JsonObject
    {
        ["updated"] = result.Updated,
        ["changed"] = Strings(result.Changed),
        ["count"] = result.Count,
        [Batched] = result.Batched,
    }, pretty);

    public static string Serialize(ThreadCompleteResult result, bool pretty = false) => Render(new JsonObject
    {
        ["completed"] = result.Completed,
        ["active"] = result.Active,
        ["remaining"] = result.Remaining,
        [Batched] = result.Batched,
    }, pretty);

    public static string Serialize(ThreadActiveResult result, bool pretty = false) => Render(new JsonObject
    {
        ["active"] = result.Active,
        ["previous"] = result.Previous,
        ["count"] = result.Count,
        [Batched] = result.Batched,
    }, pretty);

    /// <summary>
    /// The read envelope. Carries no 'batched': nothing was written.
    /// </summary>
    /// <remarks>
    /// 'error' is always present and usually null. A corrupt list is reported here rather than
    /// thrown, because this runs in the startup path and a mangled temp file must not stop a
    /// session from beginning. Consumers should read it; one that ignores it sees an empty list,
    /// which is the honest degraded answer rather than a wrong one.
    /// </remarks>
    public static string Serialize(ThreadShowResult result, bool pretty = false) => Render(new JsonObject
    {
        ["count"] = result.Count,
        ["active"] = result.Active,
        ["items"] = new JsonArray([.. result.Items.Select(Item)]),
        ["error"] = result.Error,
    }, pretty);

    /// <summary>
    /// One item in the read envelope.
    /// </summary>
    /// <remarks>
    /// 'area' is APPENDED, after the five fields the PowerShell wrote, for the same reason
    /// 'batched' was: the recorded order of what already existed stays exactly as it was, so a
    /// consumer reading positionally -- or a golden comparing field by field -- sees an
    /// addition rather than a rearrangement. It is the resolved label, so it is always a
    /// non-empty string and a reader never has to know that unfiled is stored as "".
    /// </remarks>
    private static JsonNode Item(ThreadItem item) => new JsonObject
    {
        ["topic"] = item.Topic,
        ["status"] = item.Status,
        ["refs"] = Strings(item.Refs),
        ["next"] = item.Next,
        ["notes"] = item.Notes,
        ["area"] = ThreadItems.AreaOf(item),
    };

    /// <summary>
    /// The reporter's envelope: the list as a map, with the note bodies left on disk.
    /// </summary>
    /// <remarks>
    /// 'notesLength' appears twice by design -- per item, and totalled at the envelope. The total
    /// is what makes the omission legible: a reader sees at once how much was not sent, which is
    /// the catalog's rule that a response reports its own truncation rather than looking complete.
    ///
    /// Deliberately NOT the same shape as Show's envelope. A reader must not be able to mistake
    /// one for the other and conclude from an absent 'notes' field that an item has no notes.
    /// </remarks>
    public static string Serialize(ThreadReportResult result, bool pretty = false) => Render(new JsonObject
    {
        ["contract"] = ReportContract,
        ["count"] = result.Count,
        ["active"] = result.Active,
        ["items"] = new JsonArray([.. result.Items.Select(ReportItem)]),
        ["notesLength"] = result.NotesLength,
        ["error"] = result.Error,
    }, pretty);

    private static JsonNode ReportItem(ThreadReportItem item) => new JsonObject
    {
        ["topic"] = item.Topic,
        ["status"] = item.Status,
        ["refs"] = Strings(item.Refs),
        ["next"] = item.Next,
        ["notesLead"] = item.NotesLead,
        ["notesLength"] = item.NotesLength,
        ["area"] = item.Area,
    };

    /// <summary>
    /// An area as it prefixes a topic in the text views, or nothing where the item is unfiled.
    /// </summary>
    /// <remarks>
    /// Only shown when it says something. Most of the list is unfiled and will stay that way
    /// until items are labelled deliberately, and a column reading '(unfiled)' on every row is
    /// noise that pushes the topic -- the thing being read -- to the right for no gain. The
    /// JSON envelopes carry it unconditionally; this is the human view, and they answer to
    /// different rules.
    /// </remarks>
    private static string Filed(string area) =>
        string.Equals(area, ThreadItems.Unfiled, StringComparison.Ordinal) || area.Length == 0
            ? string.Empty
            : $"[{area}] ";

    private static JsonArray Strings(IReadOnlyList<string> values) =>
        new([.. values.Select(v => (JsonNode)JsonValue.Create(v)!)]);

    private static string Render(JsonObject root, bool pretty) =>
        root.ToJsonString(pretty ? Indented : Compact);

    /// <summary>The reporter's formatted view. Same information, read by a person.</summary>
    public static string Render(ThreadReportResult result)
    {
        if (result.Error is not null)
        {
            return $"List unreadable: {result.Error}";
        }

        if (result.Items.Count == 0)
        {
            return "No thread items.";
        }

        List<string> lines = [];

        foreach (ThreadReportItem item in result.Items)
        {
            string icon = string.Equals(item.Status, ThreadItems.Active, StringComparison.OrdinalIgnoreCase)
                ? ">>>"
                : string.Equals(item.Status, ThreadItems.Done, StringComparison.OrdinalIgnoreCase)
                    ? " x "
                    : "   ";

            string size = item.NotesLength > 0 ? $" ({item.NotesLength:N0} chars of notes)" : string.Empty;

            lines.Add($"{icon} {item.Status.PadRight(7)} {Filed(item.Area)}{item.Topic}{size}");

            if (item.Next.Length > 0)
            {
                lines.Add($"            next: {item.Next}");
            }

            if (item.NotesLead.Length > 0)
            {
                lines.Add($"            {item.NotesLead}");
            }
        }

        lines.Add(string.Empty);
        lines.Add($"{result.Items.Count} items; {result.NotesLength:N0} characters of notes not shown.");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The formatted view, for a terminal.
    /// </summary>
    /// <remarks>
    /// Returned as a string rather than written with Write-Host, which is what the PowerShell
    /// did and is why its text output could not be captured by a pipe, a redirect, or an
    /// assignment without 6>&amp;1. A caller that wants it on stdout can print it.
    /// </remarks>
    public static string Render(ThreadShowResult result)
    {
        if (result.Error is not null)
        {
            return $"List unreadable: {result.Error}";
        }

        if (result.Items.Count == 0)
        {
            return "No thread items.";
        }

        List<string> lines = [];

        foreach (ThreadItem item in result.Items)
        {
            string icon = item.IsActive ? ">>>" : item.IsDone ? " x " : "   ";
            lines.Add($"{icon} {item.Status.PadRight(7)} {Filed(ThreadItems.AreaOf(item))}{item.Topic}");

            if (item.Refs.Count > 0)
            {
                lines.Add($"            refs: {string.Join(", ", item.Refs)}");
            }

            if (item.Next.Length > 0)
            {
                lines.Add($"            next: {item.Next}");
            }

            if (item.Notes.Length > 0)
            {
                // First line only, truncated. The list is a map of where you were, not the
                // notes themselves -- reading those is what the JSON is for.
                string first = item.Notes.Replace("\r\n", "\n").Split('\n')[0];

                lines.Add("            " + (first.Length > 100 ? first[..100] + "..." : first));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
