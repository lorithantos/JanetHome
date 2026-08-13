using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// The formatted view -- Get-Research.ps1's -Text output, for reading at a terminal.
/// </summary>
/// <remarks>
/// Worth keeping rather than dropping in favour of JSON, and not only for humans: measured,
/// this is 75% of the JSON's size for a multi-node result, because it drops the field names
/// JSON repeats per node. For a model scanning a shortlist to pick from, that makes it the
/// cheaper projection, not the more expensive one. Choose by what the projection contains,
/// not by which reader the flag was named for.
///
/// One deliberate difference from the PowerShell: this returns a string that the caller writes
/// to stdout, where Get-Research.ps1 uses Write-Host. Write-Host goes to the information
/// stream, so its -Text output cannot be captured by a pipe, a redirect, or an assignment
/// without 6>&amp;1 -- a caveat the catalog records against that script. Returning a string
/// retires the caveat instead of reproducing it.
/// </remarks>
public static class CatalogText
{
    private static readonly string[] KnownFields =
        ["id", "kind", "path", "section", "summary", "caveats", "tags", "params", "links"];

    public static string Render(OrientationView view, string graphName = "research.json")
    {
        StringBuilder text = new();
        text.AppendLine();
        text.AppendLine($"{graphName} -- {view.Total} nodes");

        foreach ((string kind, int count) in view.Kinds)
        {
            text.AppendLine($"  {kind,-8} {count}");
        }

        text.AppendLine();
        text.AppendLine("TAGS");
        text.AppendLine("  " + string.Join("  ", view.Tags.Select(t => $"{t.Key}({t.Value})")));
        text.AppendLine();
        text.AppendLine("Query with -Query <text>, -Tag <tag>, -Id <id>, or -Kind <kind>. Add -Expand to follow links.");
        text.AppendLine();

        return text.ToString();
    }

    public static string Render(QueryEnvelope envelope, bool ranked, bool full)
    {
        if (envelope.Nodes.Count == 0)
        {
            return "No matching nodes. Run with no arguments to see kinds and tags." + Environment.NewLine;
        }

        // Ranked results keep rank order; everything else reads better grouped by kind.
        IEnumerable<ResearchNode> ordered = ranked
            ? envelope.Nodes
            : envelope.Nodes.OrderBy(n => n.Kind, StringComparer.Ordinal).ThenBy(n => n.Id, StringComparer.Ordinal);

        StringBuilder text = new();
        text.AppendLine();

        foreach (ResearchNode node in ordered)
        {
            string suffix = string.IsNullOrEmpty(node.Section) ? string.Empty : $" (section {node.Section})";

            text.AppendLine(node.Id);
            text.AppendLine("  " + (string.IsNullOrEmpty(node.Summary) ? "(no summary)" : node.Summary));
            text.AppendLine($"  {node.Path}{suffix}");

            // Always shown, in every view. The whole point of a caveat is that you see it
            // before you rely on the node, and a warning behind a flag is not a warning.
            foreach (string caveat in node.Caveats)
            {
                text.AppendLine($"  ! {caveat}");
            }

            // Tags on ranked results: choosing between three near-neighbours is mostly a tag
            // question, and re-querying to find that out defeats the point of a shortlist.
            if (!full && ranked && node.Tags.Count > 0)
            {
                text.AppendLine($"  tags:   {string.Join(", ", node.Tags)}");
            }

            // Anything the graph grows that this formatter does not know about still gets
            // printed. The alternative is a view that silently drops new fields until someone
            // remembers to update it -- which is how 'caveats' stayed invisible once already.
            foreach ((string name, JsonNode? value) in node.UnknownFields)
            {
                string rendered = Flatten(value);
                if (rendered.Length > 0)
                {
                    text.AppendLine($"  {name}: {rendered}");
                }
            }

            if (full)
            {
                if (node.Params.Count > 0) { text.AppendLine($"  params: {string.Join(", ", node.Params)}"); }
                if (node.Links.Count > 0) { text.AppendLine($"  links:  {string.Join(", ", node.Links)}"); }
                if (node.Tags.Count > 0) { text.AppendLine($"  tags:   {string.Join(", ", node.Tags)}"); }
            }

            text.AppendLine();
        }

        // Never truncate silently -- a shortlist that looks like the whole answer is worse
        // than a long list.
        text.AppendLine(envelope.Truncated
            ? $"top {envelope.Nodes.Count} of {envelope.TotalMatches} matches. -First N for more, -All for every match."
            : $"{envelope.Nodes.Count} node{(envelope.Nodes.Count != 1 ? "s" : string.Empty)}");

        text.AppendLine();
        return text.ToString();
    }

    private static string Flatten(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonArray array => string.Join(", ", array.Select(Flatten)),
        _ => value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString(),
    };
}
