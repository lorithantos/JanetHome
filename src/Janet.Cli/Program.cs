using System.Text.Json.Nodes;
using Janet.Cli;
using Janet.Core;

// The `janet` command. Exists alongside the MCP server because hooks and shells are separate
// processes that cannot speak MCP: Invoke-ResearchGuard.ps1 shells out to the catalog on every
// Write of a new script, and it needs something to call.
try
{
    return Run(Args.Parse(args));
}
catch (GraphException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static int Run(Args args)
{
    if (args.Flag("--help") || args.Positional.Count == 0)
    {
        return Usage();
    }

    if (!string.Equals(args.Positional[0], "research", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Unknown command '{args.Positional[0]}'.");
        return Usage();
    }

    string verb = args.Positional.Count > 1 ? args.Positional[1] : "query";
    string graphPath = GraphLocator.Resolve(args.Value("--graph"), args.Value("--base"));
    bool pretty = args.Flag("--pretty");

    switch (verb.ToLowerInvariant())
    {
        case "query": return Query(args, graphPath, pretty);
        case "add": return Add(args, graphPath, pretty);
        case "update": return Update(args, graphPath, pretty);
        case "rename": return Rename(args, graphPath, pretty);
        default:
            Console.Error.WriteLine($"Unknown research verb '{verb}'.");
            return Usage();
    }
}

static int Query(Args args, string graphPath, bool pretty)
{
    ResearchGraph graph = ResearchGraph.Load(graphPath);

    CatalogQueryOptions options = new()
    {
        Id = args.Values("--id"),
        Tag = args.Values("--tag"),
        Kind = args.Value("--kind"),
        Query = args.Value("--query"),
        First = args.Int("--first", 5),
        Depth = args.Int("--depth", 1),
        All = args.Flag("--all"),
        Expand = args.Flag("--expand"),
        Full = args.Flag("--full"),
    };

    // Leave the trace the research guard reads, unless this IS the guard's own lookup --
    // which must not clear the check it is about to perform.
    if (!args.Flag("--no-trace"))
    {
        ResearchTrace.Record(options);
    }

    // With no filter and no --full, the answer is the cheap orientation view: counts by kind
    // and the tag index. A full catalog costs tokens on every session to answer a question
    // most sessions never ask.
    bool text = args.Flag("--text");

    if (!options.HasFilter && !options.Full)
    {
        OrientationView orientation = CatalogQuery.Orient(graph);
        Console.Out.Write(text
            ? CatalogText.Render(orientation, Path.GetFileName(graphPath))
            : CatalogJson.Serialize(orientation, pretty) + Environment.NewLine);
        return 0;
    }

    QueryEnvelope envelope = CatalogQuery.Execute(graph, options);

    foreach (string dangling in envelope.DanglingLinks)
    {
        Console.Error.WriteLine($"warning: dangling link: {dangling}");
    }

    Console.Out.Write(text
        ? CatalogText.Render(envelope, ranked: !string.IsNullOrWhiteSpace(options.Query), full: options.Full)
        : CatalogJson.Serialize(envelope, pretty) + Environment.NewLine);

    return 0;
}

static int Add(Args args, string graphPath, bool pretty)
{
    JsonObject? blob = ReadBlob(args);

    AddRequest request = new()
    {
        Id = Required(blob, args, "id", "--id"),
        Kind = Required(blob, args, "kind", "--kind"),
        Summary = Required(blob, args, "summary", "--summary"),
        NodePath = Required(blob, args, "path", "--path"),
        Tags = List(blob, args, "tags", "--tag"),
        Links = List(blob, args, "links", "--link"),
        Caveats = List(blob, args, "caveats", "--caveat"),
        Params = List(blob, args, "params", "--param"),
        Section = Optional(blob, args, "section", "--section"),
        DryRun = args.Flag("--dry-run"),
    };

    AddResult result = GraphWriter.Add(graphPath, request);
    Console.Out.WriteLine(ResultJson.Serialize(result, pretty));
    return 0;
}

static int Update(Args args, string graphPath, bool pretty)
{
    JsonObject? blob = ReadBlob(args);

    string id = Optional(blob, args, "id", "--id")
        ?? throw new ArgumentException("An id is required: pass --id, or include \"id\" in the JSON.");

    System.Collections.Generic.OrderedDictionary<string, JsonNode?> set = [];

    // Only fields actually asked for are touched. This is a patch, not a template that blanks
    // the fields it forgot to mention -- the distinction between clearing a field and leaving
    // it alone is the whole reason unknown fields survive an update.
    Set(set, blob, args, "summary", "--summary");
    Set(set, blob, args, "kind", "--kind");
    Set(set, blob, args, "path", "--path");
    Set(set, blob, args, "section", "--section");
    SetList(set, blob, args, "tags", "--tag");
    SetList(set, blob, args, "links", "--link");
    SetList(set, blob, args, "caveats", "--caveat");
    SetList(set, blob, args, "params", "--param");

    UpdateRequest request = new()
    {
        Id = id,
        Set = set,
        Remove = args.Values("--remove"),
        Append = args.Flag("--append"),
        DryRun = args.Flag("--dry-run"),
    };

    UpdateResult result = GraphWriter.Update(graphPath, request);
    Console.Out.WriteLine(ResultJson.Serialize(result, pretty));
    return 0;
}

static int Rename(Args args, string graphPath, bool pretty)
{
    string id = args.Value("--id") ?? throw new ArgumentException("--id is required");
    string newId = args.Value("--new-id") ?? throw new ArgumentException("--new-id is required");

    RenameResult result = GraphRenamer.Rename(graphPath, new RenameRequest
    {
        Id = id,
        NewId = newId,
        Kind = args.Value("--kind"),
        DryRun = args.Flag("--dry-run"),
    });

    // Body references go to stderr as well as into the payload: they are the part a rename
    // deliberately does NOT fix, and a caller reading only stdout would never act on them.
    foreach (string reference in result.BodyReferences)
    {
        Console.Error.WriteLine($"warning: '{id}' still mentioned in {reference}");
    }

    Console.Out.WriteLine(ResultJson.Serialize(result, pretty));
    return 0;
}

static JsonObject? ReadBlob(Args args)
{
    string? raw = args.Value("--json");

    if (args.Value("--json-path") is string path)
    {
        if (raw is not null)
        {
            throw new ArgumentException("Give --json or --json-path, not both.");
        }

        if (!File.Exists(path))
        {
            throw new ArgumentException($"JSON file not found: {path}");
        }

        raw = File.ReadAllText(path);
    }

    if (raw is null)
    {
        return null;
    }

    // Prefer the blob for anything prose-shaped: a summary routed through a shell's quoting
    // rules is how this catalog once stored a doubled apostrophe that nothing downstream could
    // tell from intent. JSON has exactly one escaping rule and the parser enforces it.
    JsonNode? parsed;
    try
    {
        parsed = JsonNode.Parse(raw);
    }
    catch (System.Text.Json.JsonException ex)
    {
        throw new ArgumentException($"The node JSON does not parse: {ex.Message}");
    }

    return parsed as JsonObject
        ?? throw new ArgumentException(
            "The node JSON must be a single object: { \"id\": ..., \"kind\": ..., \"summary\": ..., \"path\": ... }");
}

static string Required(JsonObject? blob, Args args, string field, string option) =>
    Optional(blob, args, field, option)
        ?? throw new ArgumentException($"Missing required field: {option} (or \"{field}\" in the JSON)");

static string? Optional(JsonObject? blob, Args args, string field, string option)
{
    if (args.Value(option) is string value)
    {
        return value;
    }

    if (blob is not null && blob.TryGetPropertyValue(field, out JsonNode? node) && node is not null)
    {
        return node.GetValue<string>();
    }

    return null;
}

static IReadOnlyList<string> List(JsonObject? blob, Args args, string field, string option)
{
    if (args.Values(option) is { Count: > 0 } values)
    {
        return values;
    }

    if (blob is not null && blob.TryGetPropertyValue(field, out JsonNode? node) && node is JsonArray array)
    {
        // An absent list must stay empty: a null element would serialize as "" and write an
        // empty tag into the file.
        return [.. array.Where(a => a is not null)
                        .Select(a => a!.GetValue<string>())
                        .Where(v => !string.IsNullOrWhiteSpace(v))];
    }

    return [];
}

static void Set(
    System.Collections.Generic.OrderedDictionary<string, JsonNode?> set,
    JsonObject? blob,
    Args args,
    string field,
    string option)
{
    bool asked = args.Has(option) || (blob?.ContainsKey(field) ?? false);
    if (asked)
    {
        set[field] = Optional(blob, args, field, option);
    }
}

static void SetList(
    System.Collections.Generic.OrderedDictionary<string, JsonNode?> set,
    JsonObject? blob,
    Args args,
    string field,
    string option)
{
    bool asked = args.Has(option) || (blob?.ContainsKey(field) ?? false);
    if (!asked)
    {
        return;
    }

    JsonArray array = [];
    foreach (string value in List(blob, args, field, option))
    {
        array.Add(value);
    }

    set[field] = array;
}

static int Usage()
{
    Console.Error.WriteLine("""
        janet research query  [--query TEXT] [--id ID]... [--tag TAG]... [--kind KIND]
                              [--first N] [--all] [--expand] [--depth N] [--full]
                              [--text] [--pretty]   (--text is the formatted view)
        janet research add    --id ID --kind KIND --path PATH --summary TEXT
                              [--tag T]... [--link ID]... [--caveat TEXT]... [--param NAME]...
                              [--section N] [--dry-run]
        janet research update --id ID [--summary TEXT] [--kind KIND] [--path PATH] [--section N]
                              [--tag T]... [--link ID]... [--caveat TEXT]... [--param NAME]...
                              [--append] [--remove FIELD]... [--dry-run]
        janet research rename --id ID --new-id ID [--kind KIND] [--dry-run]
                              (moves every inbound link with the node; prose mentions are
                              reported on stderr, never rewritten)

        Every command: [--base DIR] [--graph PATH]   (graph defaults to research.json in JanetBase)
        add/update also accept --json '<object>' or --json-path FILE, which is the form to use
        for anything prose-shaped: a shell's quoting rules are how prose gets corrupted.

        With no filter, query returns the cheap orientation view: counts by kind and the tag
        index. Ask for what you need; the full catalog is not the default for a reason.
        """);

    return 2;
}
