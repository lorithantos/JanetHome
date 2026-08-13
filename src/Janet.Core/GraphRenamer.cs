using System.Text.Json.Nodes;

namespace Janet.Core;

public sealed record RenameRequest
{
    public required string Id { get; init; }
    public required string NewId { get; init; }

    /// <summary>Also change the kind. Ids carry the kind as a prefix, so a rename is usually a re-kind.</summary>
    public string? Kind { get; init; }

    public bool DryRun { get; init; }
}

public sealed record RenameResult(
    bool Renamed,
    bool DryRun,
    string Id,
    string NewId,
    IReadOnlyList<string> Relinked,
    IReadOnlyList<string> BodyReferences,
    IReadOnlyList<string> Warnings,
    int TotalNodes,
    int Batched = 1) : IBatchedResult<RenameResult>
{
    public RenameResult WithBatch(int totalNodes, int batched) =>
        this with { TotalNodes = totalNodes, Batched = batched };
}

/// <summary>
/// Renames a node and moves every inbound link with it.
/// </summary>
/// <remarks>
/// An id change is not an edit to one node: every links array naming the old id has to move
/// too, and a rename that misses one leaves a dangling link that reads exactly like a deleted
/// node. That sweep is the whole reason this is not just an update.
///
/// The graph is this operation's jurisdiction; note bodies are not. Markdown that mentions the
/// old id is reported rather than rewritten -- silently editing prose is a different and much
/// larger promise than maintaining a link graph.
/// </remarks>
public static class GraphRenamer
{
    public static RenameResult Rename(string graphPath, RenameRequest request) =>
        WriteQueue.Submit(graphPath, text => ApplyRename(text, graphPath, request), GraphWriter.NodeCount);

    /// <summary>The rename itself, against text the queue holds. Returns the new text rather than writing it.</summary>
    internal static (string Text, RenameResult Result) ApplyRename(
        string text, string graphPath, RenameRequest request)
    {
        ResearchGraph graph = ResearchGraph.Parse(text, graphPath);

        if (!graph.TryGet(request.Id, out ResearchNode node))
        {
            throw new GraphException($"No node with id '{request.Id}'.");
        }

        if (graph.Contains(request.NewId))
        {
            throw new GraphException($"id '{request.NewId}' already exists; renaming onto it would merge two nodes.");
        }

        if (!string.IsNullOrEmpty(request.Kind) &&
            !CatalogQuery.Kinds.Contains(request.Kind, StringComparer.OrdinalIgnoreCase))
        {
            throw new GraphException(
                $"Unknown kind '{request.Kind}'. Valid kinds: {string.Join(", ", CatalogQuery.Kinds)}.");
        }

        List<string> warnings = [];
        string effectiveKind = request.Kind ?? node.Kind;

        if (!request.NewId.StartsWith(effectiveKind + ".", StringComparison.Ordinal))
        {
            warnings.Add($"new id '{request.NewId}' does not start with its kind ('{effectiveKind}.')");
        }

        // Every node whose links name the old id has to move with it.
        List<ResearchNode> inbound =
        [
            .. graph.Nodes.Where(n => n.Links.Any(l => string.Equals(l, request.Id, StringComparison.Ordinal)))
        ];

        string newline = NodeText.NewlineOf(graph.Text);
        List<(int Start, int End, string Text)> edits = [];

        // The renamed node itself.
        System.Collections.Generic.OrderedDictionary<string, JsonNode?> fields = CloneFields(node);
        fields["id"] = request.NewId;
        if (request.Kind is not null)
        {
            fields["kind"] = request.Kind;
        }

        // A node that links to itself must not be left pointing at the old id.
        RewriteLinks(fields, request.Id, request.NewId);

        (int start, int end) = NodeText.FindSpan(graph.Text, request.Id);
        edits.Add((start, end, NodeText.RenderUpdated(fields, newline).TrimStart()));

        foreach (ResearchNode other in inbound.Where(n => !string.Equals(n.Id, request.Id, StringComparison.Ordinal)))
        {
            System.Collections.Generic.OrderedDictionary<string, JsonNode?> otherFields = CloneFields(other);
            RewriteLinks(otherFields, request.Id, request.NewId);

            (int otherStart, int otherEnd) = NodeText.FindSpan(graph.Text, other.Id);
            edits.Add((otherStart, otherEnd, NodeText.RenderUpdated(otherFields, newline).TrimStart()));
        }

        List<string> relinked =
        [
            .. inbound.Where(n => !string.Equals(n.Id, request.Id, StringComparison.Ordinal))
                      .Select(n => n.Id)
                      .OrderBy(id => id, StringComparer.Ordinal)
        ];

        List<string> bodyReferences = FindBodyReferences(graphPath, request.Id);

        if (request.DryRun)
        {
            return (text, new RenameResult(false, true, request.Id, request.NewId, relinked, bodyReferences,
                warnings, graph.Nodes.Count));
        }

        // Applied back to front so each splice leaves every earlier offset valid. Doing it
        // forwards would shift every span after the first edit by the length delta, and the
        // second splice would land inside a neighbouring node.
        string updated = graph.Text;
        foreach ((int editStart, int editEnd, string replacement) in edits.OrderByDescending(e => e.Start))
        {
            updated = updated[..editStart] + replacement + updated[(editEnd + 1)..];
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(updated);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new GraphException(
                $"Rename would produce invalid JSON; {graphPath} left unchanged. {ex.Message}", ex);
        }

        JsonArray? nodes = parsed?["nodes"]?.AsArray();
        if (nodes is null || nodes.Count != graph.Nodes.Count)
        {
            throw new GraphException(
                $"Rename changed the node count; {graphPath} left unchanged");
        }

        List<string> ids = [.. nodes.Select(n => n?["id"]?.GetValue<string>() ?? string.Empty)];
        if (!ids.Contains(request.NewId, StringComparer.Ordinal))
        {
            throw new GraphException($"'{request.NewId}' missing after rename; {graphPath} left unchanged");
        }

        if (ids.Contains(request.Id, StringComparer.Ordinal))
        {
            throw new GraphException($"'{request.Id}' still present after rename; {graphPath} left unchanged");
        }

        // A rename that leaves a link behind is the failure this operation exists to prevent,
        // so it is checked rather than assumed.
        string stillLinking = string.Join(", ", nodes
            .Where(n => n?["links"]?.AsArray().Any(l => l?.GetValue<string>() == request.Id) == true)
            .Select(n => n?["id"]?.GetValue<string>()));

        if (stillLinking.Length > 0)
        {
            throw new GraphException(
                $"links to '{request.Id}' survive on: {stillLinking}; {graphPath} left unchanged");
        }

        return (updated, new RenameResult(true, false, request.Id, request.NewId, relinked, bodyReferences,
            warnings, nodes.Count));
    }

    private static System.Collections.Generic.OrderedDictionary<string, JsonNode?> CloneFields(ResearchNode node)
    {
        System.Collections.Generic.OrderedDictionary<string, JsonNode?> fields = [];
        foreach (KeyValuePair<string, JsonNode?> property in node.Json)
        {
            fields[property.Key] = property.Value?.DeepClone();
        }

        return fields;
    }

    private static void RewriteLinks(
        System.Collections.Generic.OrderedDictionary<string, JsonNode?> fields,
        string oldId,
        string newId)
    {
        if (fields.GetValueOrDefault("links") is not JsonArray links)
        {
            return;
        }

        JsonArray rewritten = [];
        foreach (JsonNode? link in links)
        {
            string value = NodeText.AsText(link);
            rewritten.Add(string.Equals(value, oldId, StringComparison.Ordinal) ? newId : value);
        }

        fields["links"] = rewritten;
    }

    /// <summary>
    /// Markdown that mentions the old id. Reported, never rewritten.
    /// </summary>
    private static List<string> FindBodyReferences(string graphPath, string id)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(graphPath))!;
        List<string> hits = [];

        foreach (string file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (File.ReadAllText(file).Contains(id, StringComparison.Ordinal))
                {
                    hits.Add(Path.GetRelativePath(root, file));
                }
            }
            catch (IOException)
            {
                // An unreadable file is not a reason to abandon a rename.
            }
        }

        hits.Sort(StringComparer.Ordinal);
        return hits;
    }
}
