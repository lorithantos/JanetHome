using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// Serializes results in the shape every existing consumer already parses.
/// </summary>
/// <remarks>
/// JSON is the default because the consumer is a model, not a person: formatting for human
/// eyes first makes an unambiguous reader pay for line breaks it does not read, and makes the
/// node schema hostage to a formatter someone has to remember to update.
/// </remarks>
public static class CatalogJson
{
    /// <summary>
    /// Relaxed escaping, not the default. System.Text.Json escapes &lt;, &gt;, &amp;, ' and +
    /// to \uXXXX for HTML-embedding safety; PowerShell's ConvertTo-Json does not. Node prose
    /// is full of those characters -- "&lt;inheritdoc/&gt;", "A -&gt; B", "build &amp; test" -- so
    /// the default encoder would produce output that parses identically and diffs against the
    /// PowerShell on nearly every node. This is a serializer setting, not a safety decision:
    /// nothing here is ever interpolated into HTML.
    /// </summary>
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    private static readonly JsonSerializerOptions Compact = new() { Encoder = Encoder, WriteIndented = false };
    private static readonly JsonSerializerOptions Indented = new() { Encoder = Encoder, WriteIndented = true };

    public static string Serialize(QueryEnvelope envelope, bool pretty = false)
    {
        JsonArray nodes = [];
        foreach (ResearchNode node in envelope.Nodes)
        {
            // DeepClone because a JsonNode has exactly one parent, and these are still
            // attached to the graph they were read from.
            nodes.Add(node.Json.DeepClone());
        }

        JsonObject root = new()
        {
            ["returned"] = envelope.Returned,
            ["totalMatches"] = envelope.TotalMatches,
            ["truncated"] = envelope.Truncated,
            ["nodes"] = nodes,
        };

        return root.ToJsonString(pretty ? Indented : Compact);
    }

    public static string Serialize(OrientationView view, bool pretty = false)
    {
        JsonObject kinds = [];
        foreach ((string kind, int count) in view.Kinds)
        {
            kinds[kind] = count;
        }

        JsonObject tags = [];
        foreach ((string tag, int count) in view.Tags)
        {
            tags[tag] = count;
        }

        JsonObject root = new()
        {
            ["total"] = view.Total,
            ["kinds"] = kinds,
            ["tags"] = tags,
        };

        return root.ToJsonString(pretty ? Indented : Compact);
    }
}
