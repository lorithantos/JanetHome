using System.ComponentModel;
using System.Text.Json.Nodes;
using Janet.Core;
using ModelContextProtocol.Server;

namespace Janet.Mcp;

/// <summary>Holds the graph this server instance was pointed at.</summary>
public sealed class GraphContext(string graphPath)
{
    public string GraphPath { get; } = graphPath;
}

[McpServerToolType]
public static class ResearchTools
{
    [McpServerTool(Name = "research_query")]
    [Description(
        "Query the Janet research catalog: every script, pattern, note, and skill, with what " +
        "each is for and what will bite you. Call this BEFORE investigating a question or " +
        "writing a tool -- the answer is often already a node. With no arguments it returns a " +
        "cheap orientation view (counts by kind plus the tag index) rather than the whole " +
        "catalog. Free-text queries come back as a ranked shortlist; check 'truncated' before " +
        "concluding the result set is complete.")]
    public static string Query(
        GraphContext context,
        [Description("Free text, matched over id, summary, and tags. Terms score independently.")]
        string? query = null,
        [Description("Exact node ids.")] string[]? id = null,
        [Description("Return nodes carrying any of these tags.")] string[]? tag = null,
        [Description("Filter to one kind: script, pattern, note, file, or skill.")] string? kind = null,
        [Description("Cap on ranked free-text results. Default 5.")] int first = 5,
        [Description("Return every match instead of a ranked shortlist.")] bool all = false,
        [Description("Also return nodes reachable by links from the matches.")] bool expand = false,
        [Description("How many link hops to follow when expand is set. Default 1.")] int depth = 1)
    {
        ResearchGraph graph = ResearchGraph.Load(context.GraphPath);

        CatalogQueryOptions options = new()
        {
            Id = id ?? [],
            Tag = tag ?? [],
            Kind = kind,
            Query = query,
            First = first,
            All = all,
            Expand = expand,
            Depth = depth,
        };

        // An MCP-only session is the case that most needs this: it asks the catalog here and
        // nowhere else, so without a trace the research guard blocks its next new script
        // insisting it never asked.
        ResearchTrace.Record(options);

        // Tried and removed: firing NotificationMethods.ToolListChangedNotification from here
        // did NOT refresh an attached session's view of the tool surface. The notification
        // reached the wire -- observed on the response stream -- and the descriptions a live
        // session sees stayed as they were at connection time. Recorded in note.janet-mcp-port
        // so nobody reaches for it again expecting a hot surface swap.

        return options.HasFilter
            ? CatalogJson.Serialize(CatalogQuery.Execute(graph, options))
            : CatalogJson.Serialize(CatalogQuery.Orient(graph));
    }

    [McpServerTool(Name = "research_add")]
    [Description(
        "Add a node to the research catalog. Validates the id, warns on dangling links and " +
        "missing paths, and writes the reverse link on every target so the new node is " +
        "reachable from its neighbours. Splices into the file text rather than reserializing, " +
        "so the catalog's hand-written grouping survives. Use dryRun to preview the exact text.")]
    public static string Add(
        GraphContext context,
        [Description("Node id, conventionally <kind>.<kebab-slug>.")] string id,
        [Description("script, pattern, note, file, or skill.")] string kind,
        [Description("One line. Write it as a claim, not a title -- this is what retrieval matches.")]
        string summary,
        [Description("Repo-relative path the node points at.")] string path,
        [Description("Tags for retrieval. Reuse existing ones where they fit.")] string[]? tags = null,
        [Description("Ids of related nodes. The reverse link is written automatically.")] string[]? links = null,
        [Description("What bites you: missing dependencies, platform assumptions, what is broken.")]
        string[]? caveats = null,
        [Description("For script nodes: parameter names.")] string[]? parameters = null,
        [Description("For pattern nodes: the DESIGN-NOTES section number.")] string? section = null,
        [Description("Validate and return the node text without writing.")] bool dryRun = false)
    {
        AddResult result = GraphWriter.Add(context.GraphPath, new AddRequest
        {
            Id = id,
            Kind = kind,
            Summary = summary,
            NodePath = path,
            Tags = tags ?? [],
            Links = links ?? [],
            Caveats = caveats ?? [],
            Params = parameters ?? [],
            Section = section,
            DryRun = dryRun,
        });

        return ResultJson.Serialize(result);
    }

    [McpServerTool(Name = "research_update")]
    [Description(
        "Change an existing catalog node. This is a PATCH: only the fields you pass are " +
        "touched, and fields this tool does not know about are preserved. Use append to merge " +
        "into an existing array rather than replacing it. Required fields (id, kind, path, " +
        "summary) cannot be removed.")]
    public static string Update(
        GraphContext context,
        [Description("Id of the node to change.")] string id,
        [Description("Replacement summary.")] string? summary = null,
        [Description("Replacement kind.")] string? kind = null,
        [Description("Replacement path.")] string? path = null,
        [Description("Replacement section.")] string? section = null,
        string[]? tags = null,
        string[]? links = null,
        string[]? caveats = null,
        [Description("For script nodes: parameter names.")] string[]? parameters = null,
        [Description("Merge array values into what is already there, dropping repeats.")] bool append = false,
        [Description("Field names to delete from the node.")] string[]? remove = null,
        [Description("Report the changes and the node text without writing.")] bool dryRun = false)
    {
        System.Collections.Generic.OrderedDictionary<string, JsonNode?> set = [];

        // Null means "not asked for". That is the distinction between clearing a field and
        // leaving it alone, and it is why unknown fields survive a patch.
        if (summary is not null) { set["summary"] = summary; }
        if (kind is not null) { set["kind"] = kind; }
        if (path is not null) { set["path"] = path; }
        if (section is not null) { set["section"] = section; }
        if (tags is not null) { set["tags"] = ToArray(tags); }
        if (links is not null) { set["links"] = ToArray(links); }
        if (caveats is not null) { set["caveats"] = ToArray(caveats); }
        if (parameters is not null) { set["params"] = ToArray(parameters); }

        UpdateResult result = GraphWriter.Update(context.GraphPath, new UpdateRequest
        {
            Id = id,
            Set = set,
            Remove = remove ?? [],
            Append = append,
            DryRun = dryRun,
        });

        return ResultJson.Serialize(result);
    }

    [McpServerTool(Name = "research_rename")]
    [Description(
        "Rename a catalog node, moving every inbound link with it. An id change is not an " +
        "edit to one node: a rename that misses a link leaves a dangling reference that reads " +
        "exactly like a deleted node. Ids carry the kind as a prefix, so pass kind to re-kind " +
        "in the same operation. Markdown still mentioning the old id is REPORTED in " +
        "bodyReferences, never rewritten -- fix those separately.")]
    public static string Rename(
        GraphContext context,
        [Description("Current id. Must exist.")] string id,
        [Description("New id. Must not already exist.")] string newId,
        [Description("Also change the kind -- the usual companion of a rename.")] string? kind = null,
        [Description("Report what would change without writing.")] bool dryRun = false)
    {
        RenameResult result = GraphRenamer.Rename(context.GraphPath, new RenameRequest
        {
            Id = id,
            NewId = newId,
            Kind = kind,
            DryRun = dryRun,
        });

        return ResultJson.Serialize(result);
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        JsonArray array = [];
        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }
}


