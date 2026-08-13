using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// Renders node text in the file's hand-written layout, and locates an existing node's span.
/// </summary>
/// <remarks>
/// Writes splice into the file text; they do not reserialize it. research.json is curated --
/// comment keys, grouped sections, blank lines between kinds -- and a round-trip through any
/// serializer flattens all of that, turning a one-node change into a whole-file diff. That
/// makes review impossible exactly where review matters, so the layout is reproduced by hand.
/// </remarks>
public static class NodeText
{
    private static readonly JsonSerializerOptions ScalarOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Canonical field order for a rewritten node, then anything the schema has grown.</summary>
    private static readonly string[] UpdateOrder =
        ["id", "kind", "path", "section", "summary", "params", "caveats", "tags", "links"];

    /// <summary>Detects the file's own line ending rather than assuming the platform's.</summary>
    public static string NewlineOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    public static string Scalar(string value) => JsonSerializer.Serialize(value, ScalarOptions);

    public static string Array(IReadOnlyList<string> values) =>
        values.Count == 0 ? "[]" : "[" + string.Join(", ", values.Select(Scalar)) + "]";

    /// <summary>
    /// Renders a brand-new node, in the canonical field order.
    /// </summary>
    /// <remarks>
    /// Same order as <see cref="RenderUpdated"/>. The two write paths used to disagree -- add
    /// emitted tags before caveats, the rewrite path the reverse -- so a node created by one
    /// and later touched by the other came back with its fields reordered, turning a one-field
    /// change into a whole-node diff. Settled on the order 37 of the 51 nodes carrying both
    /// already used, and changed in Add-ResearchNode.ps1 at the same time so the PowerShell
    /// and the port still agree byte for byte.
    /// </remarks>
    public static string RenderNew(
        string id,
        string kind,
        string path,
        string summary,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> links,
        IReadOnlyList<string> caveats,
        IReadOnlyList<string> parameters,
        string? section,
        string newline)
    {
        List<string> lines =
        [
            "    {",
            "      \"id\": " + Scalar(id) + ",",
            "      \"kind\": " + Scalar(kind) + ",",
            "      \"path\": " + Scalar(path) + ",",
        ];

        if (!string.IsNullOrEmpty(section))
        {
            lines.Add("      \"section\": " + Scalar(section) + ",");
        }

        lines.Add("      \"summary\": " + Scalar(summary) + ",");

        if (parameters.Count > 0)
        {
            lines.Add("      \"params\": " + Array(parameters) + ",");
        }

        if (caveats.Count > 0)
        {
            lines.AddRange(RenderCaveats(caveats, trailing: ","));
        }

        lines.Add("      \"tags\": " + Array(tags) + ",");
        lines.Add("      \"links\": " + Array(links));
        lines.Add("    }");

        return string.Join(newline, lines);
    }

    /// <summary>Renders an existing node from its full field set, preserving unknown fields.</summary>
    public static string RenderUpdated(
        System.Collections.Generic.OrderedDictionary<string, JsonNode?> fields,
        string newline)
    {
        List<string> emitOrder = [.. UpdateOrder.Where(fields.ContainsKey)];
        emitOrder.AddRange(fields.Keys.Where(k => !UpdateOrder.Contains(k, StringComparer.Ordinal)));

        List<string> lines = ["    {"];

        for (int i = 0; i < emitOrder.Count; i++)
        {
            string name = emitOrder[i];
            JsonNode? value = fields[name];
            string comma = i < emitOrder.Count - 1 ? "," : string.Empty;

            if (value is JsonArray array)
            {
                List<string> items = [.. array.Select(a => a?.GetValue<string>() ?? string.Empty)];

                if (name == "caveats" && items.Count > 0)
                {
                    lines.AddRange(RenderCaveats(items, comma));
                }
                else
                {
                    lines.Add("      " + Scalar(name) + ": " + Array(items) + comma);
                }

                continue;
            }

            lines.Add("      " + Scalar(name) + ": " + Scalar(AsText(value)) + comma);
        }

        lines.Add("    }");
        return string.Join(newline, lines);
    }

    /// <summary>One caveat per line: they are prose, and a joined line is unreadable exactly where readability matters.</summary>
    private static IEnumerable<string> RenderCaveats(IReadOnlyList<string> caveats, string trailing)
    {
        yield return "      \"caveats\": [";

        for (int i = 0; i < caveats.Count; i++)
        {
            yield return "        " + Scalar(caveats[i]) + (i < caveats.Count - 1 ? "," : string.Empty);
        }

        yield return "      ]" + trailing;
    }

    /// <summary>Flattens a scalar the way PowerShell's [string] cast does, so change detection agrees.</summary>
    public static string AsText(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonArray array => string.Join(", ", array.Select(AsText)),
        _ => value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString(),
    };

    /// <summary>
    /// Finds the text span of one node, by string-aware brace matching.
    /// </summary>
    /// <remarks>
    /// Has to be string-aware: summaries and caveats contain braces and escaped quotes, and a
    /// naive depth counter lands in the wrong place and corrupts a neighbouring node.
    /// </remarks>
    public static (int Start, int End) FindSpan(string text, string nodeId)
    {
        string needle = "\"id\": \"" + nodeId + "\"";
        int index = text.IndexOf(needle, StringComparison.Ordinal);

        if (index < 0)
        {
            throw new GraphException($"Could not locate '{nodeId}' in the graph text");
        }

        if (text.IndexOf(needle, index + 1, StringComparison.Ordinal) >= 0)
        {
            throw new GraphException($"id '{nodeId}' appears more than once in the graph text");
        }

        int start = text.LastIndexOf('{', index);
        if (start < 0)
        {
            throw new GraphException($"Malformed graph: no opening brace before '{nodeId}'");
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char ch = text[i];

            if (escaped) { escaped = false; continue; }
            if (ch == '\\') { if (inString) { escaped = true; } continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) { continue; }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return (start, i);
                }
            }
        }

        throw new GraphException($"Malformed graph: unterminated node object for '{nodeId}'");
    }

    /// <summary>Writes UTF-8 without a BOM, matching what every other tool in the repo emits.</summary>
    public static void WriteUtf8NoBom(string path, string content) =>
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
