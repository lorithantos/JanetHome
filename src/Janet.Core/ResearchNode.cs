using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// One node in the research graph.
/// </summary>
/// <remarks>
/// Wraps the parsed <see cref="JsonObject"/> rather than deserializing into a POCO, because
/// the graph grows fields faster than any type keeps up with, and a POCO drops what it does
/// not know on the next write. The PowerShell got this for free from PSCustomObject:
/// Update-ResearchNode.ps1 preserves unknown fields, and Get-Research.ps1's text view prints
/// them, precisely so a newly added field is visible before a formatter learns about it.
/// A typed model here would silently reintroduce the problem both were written to avoid.
/// </remarks>
public sealed class ResearchNode(JsonObject json)
{
    /// <summary>The underlying JSON, for round-tripping and for callers reading fields this type does not name.</summary>
    public JsonObject Json { get; } = json;

    public string Id => GetString("id") ?? string.Empty;
    public string Kind => GetString("kind") ?? string.Empty;
    public string Path => GetString("path") ?? string.Empty;
    public string Summary => GetString("summary") ?? string.Empty;
    public string? Section => GetString("section");

    public IReadOnlyList<string> Tags => GetStrings("tags");
    public IReadOnlyList<string> Links => GetStrings("links");
    public IReadOnlyList<string> Caveats => GetStrings("caveats");
    public IReadOnlyList<string> Params => GetStrings("params");

    /// <summary>
    /// Fields this type does not name, in file order. The text formatter prints these so a
    /// field the graph grows is visible without anyone remembering to update a view first.
    /// </summary>
    public IEnumerable<KeyValuePair<string, JsonNode?>> UnknownFields =>
        Json.Where(p => !KnownFields.Contains(p.Key));

    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "id", "kind", "path", "section", "summary", "caveats", "tags", "params", "links",
    };

    /// <summary>
    /// Reads an optional string. Absent and explicitly-null are the same answer -- the
    /// distinction the PowerShell's Get-Field helper also collapses, deliberately.
    /// </summary>
    private string? GetString(string name) =>
        Json.TryGetPropertyValue(name, out JsonNode? value) && value is not null
            ? value.GetValue<string>()
            : null;

    private IReadOnlyList<string> GetStrings(string name)
    {
        if (!Json.TryGetPropertyValue(name, out JsonNode? value) || value is not JsonArray array)
        {
            return [];
        }

        return [.. array.Where(item => item is not null).Select(item => item!.GetValue<string>())];
    }
}
