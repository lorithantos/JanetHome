using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Janet.Core;

public sealed record AddRequest
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public required string NodePath { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Links { get; init; } = [];
    public IReadOnlyList<string> Caveats { get; init; } = [];
    public IReadOnlyList<string> Params { get; init; } = [];
    public string? Section { get; init; }
    public bool DryRun { get; init; }
}

public sealed record UpdateRequest
{
    public required string Id { get; init; }

    /// <summary>Fields to set. Absent means untouched -- this is a patch, not a template that blanks what it forgot.</summary>
    public System.Collections.Generic.OrderedDictionary<string, JsonNode?> Set { get; init; } = [];

    public IReadOnlyList<string> Remove { get; init; } = [];

    /// <summary>Merge array values into what is already there instead of replacing, dropping repeats.</summary>
    public bool Append { get; init; }

    public bool DryRun { get; init; }
}

public sealed record FieldChange(string Field, string From, string To);

public sealed record AddResult(
    bool Added,
    bool DryRun,
    string Id,
    int TotalNodes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> ReverseLinks,
    string? NodeText);

public sealed record UpdateResult(
    bool Updated,
    bool DryRun,
    string Id,
    IReadOnlyList<FieldChange> Changes,
    IReadOnlyList<string> Warnings,
    int TotalNodes,
    string? NodeText);

/// <summary>
/// Adds and updates nodes by splicing the graph's text. Validates before writing, and
/// re-parses the spliced result before it touches disk -- a splice that would corrupt the file
/// aborts and leaves the original alone.
/// </summary>
public static class GraphWriter
{
    private static readonly string[] Protected = ["id", "kind", "path", "summary"];
    private static readonly string[] ArrayFields = ["tags", "links", "caveats", "params"];

    private static readonly Regex IdConvention =
        new("^[a-z]+\\.[a-z0-9-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AddResult Add(string graphPath, AddRequest request)
    {
        ResearchGraph graph = ResearchGraph.Load(graphPath);
        string repoRoot = Path.GetDirectoryName(Path.GetFullPath(graphPath))!;

        if (!CatalogQuery.Kinds.Contains(request.Kind, StringComparer.OrdinalIgnoreCase))
        {
            throw new GraphException(
                $"Unknown kind '{request.Kind}'. Valid kinds: {string.Join(", ", CatalogQuery.Kinds)}.");
        }

        // A duplicate id is the one condition that stops the add outright: two nodes the
        // selectors cannot tell apart is worse than no node at all.
        if (graph.Contains(request.Id))
        {
            throw new GraphException($"Cannot add node:{Environment.NewLine}  - id '{request.Id}' already exists");
        }

        List<string> warnings = [];

        if (!IdConvention.IsMatch(request.Id))
        {
            warnings.Add($"id '{request.Id}' does not match the <kind>.<kebab-slug> convention");
        }
        else if (!request.Id.StartsWith(request.Kind + ".", StringComparison.Ordinal))
        {
            warnings.Add($"id '{request.Id}' does not start with its kind ('{request.Kind}.')");
        }

        if (!PathExists(repoRoot, request.NodePath))
        {
            warnings.Add($"path does not exist: {request.NodePath}");
        }

        foreach (string link in request.Links.Where(l => !graph.Contains(l)))
        {
            warnings.Add($"link target does not exist: {link}");
        }

        string newline = NodeText.NewlineOf(graph.Text);
        string nodeText = NodeText.RenderNew(
            request.Id, request.Kind, request.NodePath, request.Summary,
            request.Tags, request.Links, request.Caveats, request.Params, request.Section, newline);

        if (request.DryRun)
        {
            return new AddResult(false, true, request.Id, graph.Nodes.Count, warnings,
                [.. request.Links.Where(graph.Contains)], nodeText);
        }

        // The ']' closing the nodes array is the last one in the file: the array is the final
        // structure before the closing brace.
        int closeIndex = graph.Text.LastIndexOf(']');
        if (closeIndex < 0)
        {
            throw new GraphException($"Could not find the end of the nodes array in {graphPath}");
        }

        string before = graph.Text[..closeIndex];
        string after = graph.Text[closeIndex..];
        string trimmed = before.TrimEnd();

        if (!trimmed.EndsWith('}'))
        {
            throw new GraphException(
                $"Unexpected structure before the end of the nodes array; refusing to edit {graphPath}");
        }

        string updated = trimmed + "," + newline + newline + nodeText + newline + "  " + after.TrimStart();

        int newCount = VerifySplice(updated, graphPath, graph.Nodes.Count + 1, "Splice");
        NodeText.WriteUtf8NoBom(graphPath, updated);

        // The graph's convention is bidirectional, so writing only the forward direction
        // leaves the new node invisible from its own neighbours. Done here rather than left
        // as a reminder in the output: that is the difference between an invariant and a habit.
        List<string> reverseLinked = [];
        foreach (string link in request.Links)
        {
            // Missing targets already warned during validation; the node was still added, so a
            // dangling link stays visible rather than being silently dropped.
            if (!graph.Contains(link))
            {
                continue;
            }

            try
            {
                UpdateResult back = Update(graphPath, new UpdateRequest
                {
                    Id = link,
                    Append = true,
                    Set = new System.Collections.Generic.OrderedDictionary<string, JsonNode?>
                    {
                        ["links"] = new JsonArray(request.Id),
                    },
                });

                if (back.Updated)
                {
                    reverseLinked.Add(link);
                }
            }
            catch (GraphException ex)
            {
                // A failed back-link must not undo a successful add: report and continue.
                warnings.Add($"reverse link on '{link}' failed: {ex.Message}");
            }
        }

        return new AddResult(true, false, request.Id, newCount, warnings, reverseLinked, null);
    }

    public static UpdateResult Update(string graphPath, UpdateRequest request)
    {
        ResearchGraph graph = ResearchGraph.Load(graphPath);
        string repoRoot = Path.GetDirectoryName(Path.GetFullPath(graphPath))!;

        if (!graph.TryGet(request.Id, out ResearchNode node))
        {
            throw new GraphException(
                $"No node with id '{request.Id}'. Use a catalog query to find the right id.");
        }

        (int start, int end) = NodeText.FindSpan(graph.Text, request.Id);

        List<FieldChange> changes = [];
        List<string> warnings = [];

        // Start from every existing property so unknown fields survive untouched.
        System.Collections.Generic.OrderedDictionary<string, JsonNode?> fields = [];
        foreach (KeyValuePair<string, JsonNode?> property in node.Json)
        {
            fields[property.Key] = property.Value?.DeepClone();
        }

        foreach ((string name, JsonNode? incoming) in request.Set)
        {
            JsonNode? value = incoming;

            if (ArrayFields.Contains(name, StringComparer.Ordinal))
            {
                value = ResolveArray(incoming, fields.GetValueOrDefault(name), request.Append);
            }

            if (name == "kind")
            {
                string kind = NodeText.AsText(value);
                if (!CatalogQuery.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
                {
                    throw new GraphException(
                        $"Unknown kind '{kind}'. Valid kinds: {string.Join(", ", CatalogQuery.Kinds)}.");
                }
            }

            if (name == "path" && !PathExists(repoRoot, NodeText.AsText(value)))
            {
                warnings.Add($"path does not exist: {NodeText.AsText(value)}");
            }

            if (name == "links" && value is JsonArray links)
            {
                foreach (JsonNode? link in links)
                {
                    string target = NodeText.AsText(link);
                    if (!graph.Contains(target))
                    {
                        warnings.Add($"link target does not exist: {target}");
                    }
                }
            }

            string oldText = NodeText.AsText(fields.GetValueOrDefault(name));
            string newText = NodeText.AsText(value);
            if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                changes.Add(new FieldChange(name, oldText, newText));
            }

            fields[name] = value;
        }

        foreach (string field in request.Remove)
        {
            if (Protected.Contains(field, StringComparer.Ordinal))
            {
                throw new GraphException($"Refusing to remove required field '{field}'");
            }

            if (fields.TryGetValue(field, out JsonNode? existing))
            {
                changes.Add(new FieldChange(field, NodeText.AsText(existing), "(removed)"));
                fields.Remove(field);
            }
            else
            {
                warnings.Add($"field not present, nothing to remove: {field}");
            }
        }

        if (changes.Count == 0)
        {
            return new UpdateResult(false, request.DryRun, request.Id, [],
                [.. warnings, "no changes requested"], graph.Nodes.Count, null);
        }

        string newline = NodeText.NewlineOf(graph.Text);
        string nodeText = NodeText.RenderUpdated(fields, newline);

        if (request.DryRun)
        {
            return new UpdateResult(false, true, request.Id, changes, warnings, graph.Nodes.Count, nodeText);
        }

        string updated = graph.Text[..start] + nodeText.TrimStart() + graph.Text[(end + 1)..];

        int newCount = VerifySplice(updated, graphPath, graph.Nodes.Count, "Update");
        if (!ResearchGraphContains(updated, request.Id))
        {
            throw new GraphException(
                $"Updated node '{request.Id}' not found after splice; {graphPath} left unchanged");
        }

        NodeText.WriteUtf8NoBom(graphPath, updated);

        return new UpdateResult(true, false, request.Id, changes, warnings, newCount, null);
    }

    /// <summary>Merges or replaces an array value, dropping repeats case-insensitively as Select-Object -Unique does.</summary>
    private static JsonArray ResolveArray(JsonNode? incoming, JsonNode? existing, bool append)
    {
        List<string> values = [.. AsList(incoming)];

        if (append)
        {
            List<string> merged = [.. AsList(existing), .. values];
            values = [.. merged.Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        JsonArray array = [];
        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static IEnumerable<string> AsList(JsonNode? value) => value switch
    {
        JsonArray array => array.Select(NodeText.AsText).Where(v => !string.IsNullOrWhiteSpace(v)),
        null => [],
        _ => [NodeText.AsText(value)],
    };

    private static bool PathExists(string repoRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        // Node paths are written with Windows separators; normalise so the check is honest
        // on a platform where '\' is an ordinary filename character.
        string normalised = relativePath.Replace('\\', Path.DirectorySeparatorChar)
                                        .Replace('/', Path.DirectorySeparatorChar);
        string full = Path.Combine(repoRoot, normalised);

        return File.Exists(full) || Directory.Exists(full);
    }

    /// <summary>Re-parses the spliced text and checks the node count. Never write a file we just broke.</summary>
    private static int VerifySplice(string updated, string graphPath, int expectedCount, string operation)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(updated);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new GraphException(
                $"{operation} would produce invalid JSON; {graphPath} left unchanged. {ex.Message}", ex);
        }

        int count = parsed?["nodes"]?.AsArray().Count ?? 0;
        if (count != expectedCount)
        {
            throw new GraphException(
                $"{operation} produced {count} nodes, expected {expectedCount}; {graphPath} left unchanged");
        }

        return count;
    }

    private static bool ResearchGraphContains(string text, string id) =>
        JsonNode.Parse(text)?["nodes"]?.AsArray()
            .Any(n => string.Equals(n?["id"]?.GetValue<string>(), id, StringComparison.Ordinal)) ?? false;
}
