using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// The parsed research graph, plus the raw text it was parsed from.
/// </summary>
/// <remarks>
/// The raw text is kept because writes splice into it rather than reserializing. research.json
/// is hand-curated -- comment keys, grouped sections, blank lines between kinds -- and a
/// round-trip through a serializer flattens all of it, turning every one-node add into a
/// whole-file diff. See <see cref="GraphWriter"/>.
/// </remarks>
public sealed class ResearchGraph
{
    private readonly Dictionary<string, ResearchNode> _byId;

    private ResearchGraph(string path, string text, JsonObject root, IReadOnlyList<ResearchNode> nodes)
    {
        Path = path;
        Text = text;
        Root = root;
        Nodes = nodes;

        // Last one wins on a duplicate id, matching the PowerShell's hashtable assignment.
        // Add-ResearchNode.ps1 refuses to create one, so a duplicate here means the file was
        // hand-edited -- which is what the edit guard exists to prevent.
        _byId = [];
        foreach (ResearchNode node in nodes)
        {
            _byId[node.Id] = node;
        }
    }

    public string Path { get; }
    public string Text { get; }
    public JsonObject Root { get; }
    public IReadOnlyList<ResearchNode> Nodes { get; }

    public bool TryGet(string id, out ResearchNode node) => _byId.TryGetValue(id, out node!);

    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>Loads and validates a graph file. Throws with the path named on every failure.</summary>
    public static ResearchGraph Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new GraphException($"Research graph not found: {path}");
        }

        string text = File.ReadAllText(path);

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new GraphException($"Research graph is not valid JSON ({path}): {ex.Message}", ex);
        }

        if (parsed is not JsonObject root)
        {
            throw new GraphException($"Research graph is not a JSON object: {path}");
        }

        if (root["nodes"] is not JsonArray nodesArray)
        {
            throw new GraphException($"Research graph has no 'nodes' array: {path}");
        }

        List<ResearchNode> nodes = [.. nodesArray.OfType<JsonObject>().Select(o => new ResearchNode(o))];

        // An empty graph is a corrupt graph, not an empty answer: every consumer of this file
        // expects a catalog, and returning zero nodes reads as "nothing matched" rather than
        // "the file you are querying is broken".
        if (nodes.Count == 0)
        {
            throw new GraphException($"Research graph has no nodes: {path}");
        }

        return new ResearchGraph(path, text, root, nodes);
    }
}

/// <summary>Signals a graph that could not be read, parsed, or trusted. Carries the path in its message.</summary>
public sealed class GraphException(string message, Exception? inner = null) : Exception(message, inner);
