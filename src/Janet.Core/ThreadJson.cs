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

    private static JsonNode Item(ThreadItem item) => new JsonObject
    {
        ["topic"] = item.Topic,
        ["status"] = item.Status,
        ["refs"] = Strings(item.Refs),
        ["next"] = item.Next,
        ["notes"] = item.Notes,
    };

    private static JsonArray Strings(IReadOnlyList<string> values) =>
        new([.. values.Select(v => (JsonNode)JsonValue.Create(v)!)]);

    private static string Render(JsonObject root, bool pretty) =>
        root.ToJsonString(pretty ? Indented : Compact);

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
            lines.Add($"{icon} {item.Status.PadRight(7)} {item.Topic}");

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
