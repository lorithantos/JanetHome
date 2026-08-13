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

    string command = args.Positional[0].ToLowerInvariant();

    if (command == "thread")
    {
        return Thread(args);
    }

    if (command == "api")
    {
        return ApiDocQuery(args);
    }

    if (command == "assembly")
    {
        return Assembly(args);
    }

    if (command == "check")
    {
        return Check(args);
    }

    if (command != "research")
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

/// <summary>
/// The thread-item list: what you were doing, and what to do first on return.
/// </summary>
/// <remarks>
/// --path rather than --graph: this list is session-scale working memory in %TEMP%, not a repo
/// artifact, so it does not resolve against JanetBase the way the catalog does.
/// </remarks>
static int Thread(Args args)
{
    string verb = args.Positional.Count > 1 ? args.Positional[1] : "show";
    string? path = args.Value("--path");
    bool pretty = args.Flag("--pretty");

    // A selector is a topic, an index, or nothing -- and nothing means whatever is active.
    ThreadSelector selector = new()
    {
        Topic = args.Value("--topic") ?? string.Empty,
        Index = int.TryParse(args.Value("--index"), out int index) ? index : -1,
    };

    switch (verb.ToLowerInvariant())
    {
        case "show":
            ThreadShowResult shown = ThreadItems.Show(path, args.Flag("--all"));

            Console.Out.WriteLine(args.Flag("--text")
                ? ThreadJson.Render(shown)
                : ThreadJson.Serialize(shown, pretty));

            return 0;

        case "add":
            Console.Out.WriteLine(ThreadJson.Serialize(ThreadItems.Add(
                path,
                args.Value("--topic") ?? throw new ArgumentException("--topic is required"),
                notes: args.Value("--notes") ?? string.Empty,
                next: args.Value("--next") ?? string.Empty,
                refs: args.Values("--ref"),
                active: args.Flag("--active")), pretty));

            return 0;

        case "update":
            Console.Out.WriteLine(ThreadJson.Serialize(ThreadItems.Update(
                path,
                selector,

                // Null means untouched and empty means clear, so these read the raw presence of
                // the flag rather than defaulting. Clearing the resume cursor is a real request.
                notes: args.Value("--notes"),
                next: args.Value("--next"),
                refs: args.Has("--ref") ? args.Values("--ref") : null,
                status: args.Value("--status"),
                appendNotes: args.Flag("--append-notes"),
                appendRefs: args.Flag("--append-refs")), pretty));

            return 0;

        case "complete":
            Console.Out.WriteLine(ThreadJson.Serialize(ThreadItems.Complete(path, selector), pretty));
            return 0;

        case "active":
            // --none clears focus; anything else moves it.
            Console.Out.WriteLine(ThreadJson.Serialize(
                ThreadItems.SetActive(path, args.Flag("--none") ? null : selector), pretty));

            return 0;

        default:
            Console.Error.WriteLine($"Unknown thread verb '{verb}'.");
            return Usage();
    }
}

/// <summary>
/// A library's XML documentation, queried the way the catalog is: ranked, flattened to prose, and
/// honest about truncation.
/// </summary>
static int ApiDocQuery(Args args)
{
    // --path wins over --package, matching the script: an explicit file bypasses cache resolution
    // entirely rather than being reconciled with it.
    string source = args.Value("--path")
        ?? ApiDoc.ResolvePath(
            args.Value("--package") ?? throw new ArgumentException("Pass --package or --path."),
            args.Value("--version"),
            args.Value("--tfm"));

    if (!File.Exists(source))
    {
        throw new GraphException($"Documentation file not found: {source}");
    }

    ApiDocRequest request = new()
    {
        Query = args.Value("--query"),
        Ids = args.Values("--id"),
        Kind = args.Value("--kind"),
        Type = args.Value("--type"),
        First = args.Int("--first", 5),
        All = args.Flag("--all"),
        Full = args.Flag("--full"),
    };

    IReadOnlyList<ApiMember> members = ApiDoc.Parse(File.ReadAllText(source), source, request.Full);
    bool pretty = args.Flag("--pretty");

    // No filter at all is the cheap orientation view rather than the whole surface: counts by
    // kind and the largest types, which is the shape of the API for a fraction of the cost.
    bool noFilter = request.Ids.Count == 0
        && string.IsNullOrEmpty(request.Query)
        && string.IsNullOrEmpty(request.Kind)
        && string.IsNullOrEmpty(request.Type);

    if (noFilter)
    {
        ApiDocOrientation orientation = ApiDoc.Orient(members, source);

        Console.Out.Write(args.Flag("--text")
            ? ApiDocJson.Render(orientation)
            : ApiDocJson.Serialize(orientation, pretty) + Environment.NewLine);

        return 0;
    }

    ApiDocResult result = ApiDoc.Query(members, source, request);

    Console.Out.Write(args.Flag("--text")
        ? ApiDocJson.Render(result, request.Full)
        : ApiDocJson.Serialize(result, pretty) + Environment.NewLine);

    return 0;
}

/// <summary>
/// A compiled assembly's real API surface, so "what is this actually called" costs one call
/// rather than a build per wrong guess.
/// </summary>
static int Assembly(Args args)
{
    string assembly = args.Value("--assembly")
        ?? (args.Positional.Count > 1 ? args.Positional[1] : null)
        ?? throw new ArgumentException("--assembly is required: a path, or a name to find under --search-root.");

    AssemblyApiRequest request = new()
    {
        Type = args.Value("--type"),
        Member = args.Value("--member"),
        Inherited = args.Flag("--inherited"),
        Static = args.Flag("--static"),
        MaxTypes = args.Int("--max-types", 40),
    };

    AssemblyApiResult result = AssemblyApi.Describe(assembly, args.Value("--search-root") ?? ".", request);

    Console.Out.Write(args.Flag("--text")
        ? AssemblyApiJson.Render(result)

        // Indented by default, which is what the script did. --compact is the opt-out.
        : AssemblyApiJson.Serialize(result, pretty: !args.Flag("--compact")) + Environment.NewLine);

    return 0;
}

/// <summary>
/// Build and test, reported as a structured answer instead of console scrollback.
/// </summary>
/// <remarks>
/// Always runs to completion, and never returns the schema's "running" arm. A handle is only
/// useful to a caller that can poll the same process later, and every CLI invocation is a fresh
/// one -- a shell or a hook simply waits. The handle exists for the resident MCP server, where a
/// long rebuild would otherwise outlast the client's call timeout.
///
/// The exit code means exactly one thing: 0 when the build succeeded and every test passed.
/// </remarks>
static int Check(Args args)
{
    CheckRequest request = new()
    {
        Target = args.Value("--target") ?? (args.Positional.Count > 1 ? args.Positional[1] : "."),
        Configuration = args.Value("--configuration") ?? "Debug",
        NoTests = args.Flag("--no-tests"),
        TestFilter = args.Value("--test-filter"),
        New = args.Flag("--new"),
        Full = args.Flag("--full"),
        NoGraph = args.Flag("--no-graph"),
    };

    CheckResult result = DotnetCheck.Run(request);

    Console.Out.Write(args.Flag("--text")
        ? DotnetCheckJson.Render(result)
        : DotnetCheckJson.Serialize(result, args.Flag("--pretty")) + Environment.NewLine);

    return result.Succeeded ? 0 : 1;
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

        janet thread show     [--all] [--text] [--pretty]
        janet thread add      --topic TEXT [--notes TEXT] [--next TEXT] [--ref ID]... [--active]
        janet thread update   [--topic TEXT | --index N] [--notes TEXT] [--next TEXT]
                              [--ref ID]... [--status active|parked|done]
                              [--append-notes] [--append-refs]
        janet thread complete [--topic TEXT | --index N]
        janet thread active   (--topic TEXT | --index N | --none)

        janet api             (--package ID | --path FILE) [--version V] [--tfm TFM]
                              [--query TEXT] [--id MEMBER]... [--kind Type|Method|Property|Field|Event]
                              [--type SUBSTRING] [--first N] [--all] [--full]
                              [--text] [--pretty]
        janet assembly        --assembly NAME|PATH [--search-root DIR] [--type REGEX]
                              [--member REGEX] [--inherited] [--static] [--max-types N]
                              [--text] [--compact]

        janet check           [--target PATH] [--configuration NAME] [--no-tests]
                              [--test-filter EXPR] [--new] [--full] [--no-graph]
                              [--text] [--pretty]
                              (exit 0 iff the build succeeded and every test passed)

        check reports build and tests as one structured answer. --new diffs the warning census
        against the previous --new run, which is the question "what did THIS change introduce";
        --full rebuilds everything without the baseline machinery, which is the question "does
        the whole thing still build" -- reach for it whenever the SHAPE of the build changed,
        because an incremental run that skipped a project entirely and one that had nothing to
        say about it produce the identical green.

        api with no filter gives the orientation view: counts by kind and the largest types.
        Free-text results are ranked and capped -- check 'truncated'. A selector (--type, --kind,
        --id) is never capped, because it is a request for a known set.
        assembly reports what a DLL actually declares. Point --search-root at a build or publish
        output, not a nuget lib folder: a folder holding one assembly cannot resolve its
        dependencies, and the answer comes back partial with 'siblingWarning' saying so.

        Thread commands: [--path FILE]   (defaults to Janet\thread-stack.json under %TEMP%)
        No selector means whatever is active. An ambiguous topic is refused, not guessed at.
        On update, an omitted --notes/--next/--ref leaves the field alone; --next '' clears it.

        Every research command: [--base DIR] [--graph PATH]
        (graph defaults to research.json in JanetBase)
        add/update also accept --json '<object>' or --json-path FILE, which is the form to use
        for anything prose-shaped: a shell's quoting rules are how prose gets corrupted.

        With no filter, query returns the cheap orientation view: counts by kind and the tag
        index. Ask for what you need; the full catalog is not the default for a reason.
        """);

    return 2;
}
