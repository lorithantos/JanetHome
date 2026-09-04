namespace Janet.Mcp;

/// <summary>
/// What to call instead when a tool's result is over the budget, per MCP tool name.
/// </summary>
/// <remarks>
/// ONE TABLE, AND EVERY TOOL HAS A ROW. The refusal in <see cref="Surfaced"/> reads this by
/// tool name, and a tool without a row would be refused with no way forward -- the failure
/// note.janet-mcp-port records for the exception arm, in a new place. So the hint is not
/// optional: a test enumerates every [McpServerTool] in this assembly and fails on a missing
/// row, and fails on a row for a tool that no longer exists, the way Test-OutputContracts
/// fails on a contract with no sampler.
///
/// Tools whose result is bounded by construction -- a write that echoes one node or one item,
/// a token's metadata -- carry <see cref="BoundedByConstruction"/>. It is still a row: if one
/// of them ever does exceed the budget, the refusal says the size was unexpected rather than
/// pointing at a selector that does not exist.
/// </remarks>
internal static class Narrowing
{
    /// <summary>
    /// The hint for a tool whose result cannot grow with the data. Over budget from one of
    /// these is a defect, and the hint says so instead of inventing a selector.
    /// </summary>
    public const string BoundedByConstruction =
        "This tool's result is bounded by construction, so a result this size is a defect in " +
        "the tool rather than a question too broad -- report it.";

    public static IReadOnlyDictionary<string, string> Hints { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["thread_show"] =
                "pass topic for one item's notes in full, or area for one project's items; " +
                "thread_report gives the whole map -- topics, focus and note sizes -- without " +
                "the note bodies.",
            ["thread_report"] =
                "pass area for one project's items or topic for one; drop all=true if you set it.",
            ["thread_add"] = BoundedByConstruction,
            ["thread_update"] = BoundedByConstruction,
            ["thread_complete"] = BoundedByConstruction,
            ["thread_set_active"] = BoundedByConstruction,
            ["research_query"] =
                "drop all=true, or select with id, tag or kind, or lower first; drop expand, or " +
                "lower depth, if you set them.",
            ["research_add"] = BoundedByConstruction,
            ["research_update"] = BoundedByConstruction,
            ["research_rename"] = BoundedByConstruction,
            ["api_doc_query"] =
                "drop all=true, or select with id, type or kind, or lower first; drop full if you " +
                "set it. A package with no query is the cheap orientation view.",
            ["assembly_api"] =
                "pass type (a regex over type names) and member (a regex over member names), " +
                "lower maxTypes, and leave inherited and static off.",
            ["dotnet_check"] =
                "pass testFilter to run one class or one test, or noTests to build only; the " +
                "CLI twin `janet check` has no result limit -- redirect it to a file and read the " +
                "failures you need.",
            ["az_token"] = BoundedByConstruction,
        };

    /// <summary>
    /// The hint for a tool, or a generic one for a name the table does not know. The generic
    /// arm exists for a tool added without a row, which the conformance test should have caught
    /// first; a refusal with a weak hint still beats a refusal with none.
    /// </summary>
    public static string For(string toolName) =>
        Hints.TryGetValue(toolName, out string? hint)
            ? hint
            : "this tool has no narrowing hint recorded -- add its row to Narrowing.Hints; use " +
              "whatever selectors its description offers.";
}
