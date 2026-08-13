namespace Janet.Tests;

/// <summary>
/// The case list, shared by the tests and by the golden generator in tests/Janet.Goldens.
/// </summary>
/// <remarks>
/// One list, two consumers. The generator turns each case into a PowerShell command line and
/// records what the original script answered; the tests turn the same case into a Janet.Core
/// call and compare. Held here rather than duplicated on both sides so a case cannot be
/// regenerated under one set of arguments and asserted under another -- which would look like
/// a passing test and mean nothing.
///
/// Arrays are written as a repeated flag (<c>-Tag a -Tag b</c>) rather than with a separator
/// character. The generator groups the repeats back into PowerShell's comma syntax. Prose here
/// carries commas, apostrophes, quotes and ampersands on purpose: those are what separate an
/// escaping bug from a passing test.
/// </remarks>
public static class Cases
{
    public const string AwkwardSummary =
        "The parser's \"strict\" mode isn't: A -> B & C, and it drops <inheritdoc/> on the floor.";

    public const string AwkwardCaveat =
        "Fails on a path containing a comma, an apostrophe ('), or a quote (\") -- all three appear in real input.";

    /// <summary>A read of the catalog: the arguments go to Get-Research.ps1.</summary>
    public sealed record Read(string Label, string[] Args);

    /// <summary>A write: the arguments go to the named script, which is handed -GraphPath.</summary>
    public sealed record Write(string Label, string Script, string[] Args);

    /// <summary>Queries, compared as JSON envelopes against the full corpus.</summary>
    public static readonly Read[] Queries =
    [
        new("orientation", []),
        new("free text: thread items", ["-Query", "thread items"]),
        new("free text: encoding", ["-Query", "encoding"]),
        new("free text: research", ["-Query", "research"]),
        new("free text: graph", ["-Query", "graph"]),
        new("free text: powershell house rules", ["-Query", "powershell house rules"]),
        new("free text: assembly", ["-Query", "assembly"]),
        new("free text: razorgraph mcp server", ["-Query", "razorgraph mcp server"]),

        // 'git' is the caveat-penalty case: several nodes mention git only in what is wrong
        // with them, and must rank below the node that actually does git work.
        new("free text: git", ["-Query", "git"]),
        new("free text: json", ["-Query", "json"]),
        new("free text: no matches", ["-Query", "zzzzznotathing"]),
        new("free text uncapped", ["-Query", "agent", "-All"]),
        new("free text, wider cap", ["-Query", "agent", "-First", "12"]),

        new("kind: script", ["-Kind", "script"]),
        new("kind: pattern", ["-Kind", "pattern"]),
        new("kind: note", ["-Kind", "note"]),
        new("kind: skill", ["-Kind", "skill"]),

        new("tag: agent", ["-Tag", "agent"]),
        new("tag: retrieval + debugging", ["-Tag", "retrieval", "-Tag", "debugging"]),

        new("id", ["-Id", "pattern.thread-items"]),
        new("id, expanded", ["-Id", "pattern.thread-items", "-Expand"]),
        new("id, expanded two hops", ["-Id", "pattern.thread-items", "-Expand", "-Depth", "2"]),
        new("id, several", ["-Id", "script.get-research", "-Id", "script.search-json"]),
        new("id, several, expanded", ["-Id", "script.get-research", "-Id", "script.search-json", "-Expand"]),

        new("query narrowed by kind", ["-Query", "graph", "-Kind", "note"]),
        new("tag narrowed by kind", ["-Tag", "agent", "-Kind", "script"]),
        new("query expanded", ["-Query", "thread items", "-Expand"]),
    ];

    /// <summary>The same corpus through the formatted view. The generator adds -Text.</summary>
    public static readonly Read[] TextViews =
    [
        new("orientation", []),
        new("ranked free text", ["-Query", "thread items", "-First", "3"]),
        new("ranked, caveat-heavy", ["-Query", "encoding", "-First", "4"]),
        new("kind listing sorts by kind then id", ["-Kind", "pattern"]),
        new("tag listing", ["-Tag", "retrieval"]),
        new("id lookup", ["-Id", "script.get-research"]),
        new("expanded", ["-Id", "pattern.thread-items", "-Expand"]),
        new("no matches", ["-Query", "zzzzznotathing"]),
        new("uncapped", ["-Query", "agent", "-All"]),
    ];

    /// <summary>
    /// The fixture path exactly as it is handed to the script, and exactly as the envelope
    /// reports it back under 'source'.
    /// </summary>
    /// <remarks>
    /// Relative on purpose. Get-ApiDoc echoes -Path verbatim rather than resolving it, so an
    /// absolute path would bake one machine's directory layout into every golden.
    /// </remarks>
    public const string ApiDocSource = @"tests\Janet.Tests\Fixtures\apidoc.xml";

    /// <summary>
    /// API-doc queries against Fixtures/apidoc.xml. The generator adds -Path.
    /// </summary>
    /// <remarks>
    /// A hand-authored fixture rather than a real package's XML, for two reasons: a cached
    /// package pins the golden to one machine's NuGet folder and one version, and the shapes
    /// worth testing (a chain of inheritdoc, a bare one, a circular pair, a conversion operator,
    /// a generic argument carrying its own comma) are rare enough in any single real package that
    /// picking one would be picking which bugs to leave uncovered.
    /// </remarks>
    public static readonly Read[] ApiDocs =
    [
        new("orientation", []),

        new("free text: draw series", ["-Query", "draw series"]),
        new("free text: tooltip formatter", ["-Query", "tooltip formatter"]),
        new("free text: coordinate", ["-Query", "coordinate"]),
        new("free text: no matches", ["-Query", "zzzzznotathing"]),
        new("free text uncapped", ["-Query", "draw", "-All"]),
        new("free text, wider cap", ["-Query", "draw", "-First", "12"]),

        // The undocumented member matches on name alone and must lose to the documented ones.
        new("free text: undocumented loses ties", ["-Query", "undocumented"]),

        new("free text, full", ["-Query", "project", "-Full"]),

        new("id", ["-Id", "M:Sample.Charting.ISeries.Measure"]),
        new("id without the prefix", ["-Id", "Sample.Charting.ISeries.Measure"]),
        new("id, several", ["-Id", "T:Sample.Charting.Tooltip", "-Id", "P:Sample.Charting.Tooltip.Formatter"]),
        new("id, member with no declaring type", ["-Id", "M:Bare"]),

        new("kind: Type", ["-Kind", "Type"]),
        new("kind: Property", ["-Kind", "Property"]),
        new("kind: Event", ["-Kind", "Event"]),

        new("type filter", ["-Type", "BarSeries"]),
        new("type filter narrowed by kind", ["-Type", "Tooltip", "-Kind", "Method"]),
        new("query narrowed by type", ["-Query", "draw", "-Type", "BarSeries"]),
        new("query narrowed by kind", ["-Query", "draw", "-Kind", "Type"]),

        // Tooltip carries all three inheritdoc shapes: resolvable, unresolvable cref, and a bare
        // one with no cref at all -- which is NOT resolved, and must report as undocumented
        // rather than borrow from somewhere plausible.
        new("every inheritdoc shape", ["-Type", "Tooltip"]),
        new("circular inheritdoc", ["-Type", "Loop"]),
        new("conversion operator and generics", ["-Type", "Coordinate", "-Full"]),
    ];

    /// <summary>
    /// The pure halves of the build check, recorded by calling the original script's own
    /// functions against the fixtures.
    /// </summary>
    /// <remarks>
    /// Not whole-envelope goldens, and the reason is structural: an envelope carries the build
    /// duration, an absolute target path and the graph state of whatever repository it ran in,
    /// so a recorded one belongs to one machine at one moment. What CAN be pinned exactly is
    /// everything that is a function of text -- parsing MSBuild output, grouping it, diffing a
    /// census, reading a TRX -- which is also where the bugs live.
    /// </remarks>
    public static readonly string[] DotnetCheckCases =
    [
        "read-build-output",
        "group-warnings",
        "warning-keys",
        "compare-baseline",
        "baseline-path",
        "read-trx",
    ];

    /// <summary>The same fixture through the formatted view. The generator adds -Text.</summary>
    public static readonly Read[] ApiDocTextViews =
    [
        new("orientation", []),
        new("ranked free text", ["-Query", "draw series", "-First", "3"]),
        new("no matches", ["-Query", "zzzzznotathing"]),
        new("type listing", ["-Type", "BarSeries"]),
        new("full", ["-Query", "project", "-Full"]),
        new("id lookup", ["-Id", "T:Sample.Charting.Coordinate"]),
    ];

    /// <summary>
    /// Writes, compared byte for byte against the layout fixture after the operation.
    /// </summary>
    /// <remarks>
    /// Byte equality is the assertion that matters, not "both produce valid JSON". The whole
    /// reason writes splice instead of reserializing is that the graph is hand-curated and its
    /// layout is reviewed; an implementation that produced equivalent JSON with a different
    /// shape would pass a semantic check and still turn every future change into an unreadable
    /// diff.
    /// </remarks>
    public static readonly Write[] Writes =
    [
        new("add", "Add-ResearchNode.ps1",
        [
            "-Id", "sandbox.parity-add",
            "-Kind", "note",
            "-NodePath", "notes\\sandbox-parity.md",
            "-Summary", AwkwardSummary,
            "-Tags", "retrieval", "-Tags", "agent",
            "-Links", "pattern.thread-items", "-Links", "script.get-research",
            "-Caveats", AwkwardCaveat,
        ]),

        new("add with params and section", "Add-ResearchNode.ps1",
        [
            "-Id", "sandbox.parity-script",
            "-Kind", "script",
            "-NodePath", "scripts\\Sandbox-Parity.ps1",
            "-Summary", "A script node, to exercise the params branch.",
            "-ScriptParams", "Path", "-ScriptParams", "Force", "-ScriptParams", "WhatIf",
            "-Tags", "powershell",
            "-Section", "12",
        ]),

        new("update summary", "Update-ResearchNode.ps1",
        [
            "-Id", "script.get-research",
            "-Summary", AwkwardSummary,
        ]),

        // 'retrieval' is already on this node: -Append must drop the repeat rather than
        // writing it twice.
        new("update appending tags", "Update-ResearchNode.ps1",
        [
            "-Id", "script.get-research",
            "-Tags", "retrieval", "-Tags", "sandbox-new-tag",
            "-Append",
        ]),

        new("update removing caveats", "Update-ResearchNode.ps1",
        [
            "-Id", "script.get-research",
            "-Remove", "caveats",
        ]),

        new("update replacing caveats", "Update-ResearchNode.ps1",
        [
            "-Id", "script.search-json",
            "-Caveats", AwkwardCaveat, "-Caveats", "A second caveat, with its own comma.",
        ]),

        new("rename", "Rename-ResearchNode.ps1",
        [
            "-Id", "script.get-research",
            "-NewId", "script.get-research-renamed",
        ]),
    ];

    /// <summary>
    /// Thread-item operations, each run against a fresh copy of the threads fixture.
    /// </summary>
    /// <remarks>
    /// Only cases that succeed. A refusal leaves the file untouched and exits non-zero, so
    /// there is no resulting state to record; those are asserted directly in ThreadItemTests,
    /// where the message can be checked rather than just the exit code.
    ///
    /// Both halves are captured: the file after the operation, byte for byte, and what the
    /// script printed. The file is the state machine and the stdout is the contract callers
    /// read -- a port could match either one alone and still be wrong.
    /// </remarks>
    public static readonly Write[] Threads =
    [
        new("thread add", "Add-ThreadItem.ps1", ["-Topic", "something noticed in passing"]),

        new("thread add active", "Add-ThreadItem.ps1",
            ["-Topic", "urgent detour", "-Active"]),

        new("thread add furnished", "Add-ThreadItem.ps1",
        [
            "-Topic", "a topic with a comma, an apostrophe (') and a \"quote\"",
            "-Notes", "Notes that run on, with punctuation: A -> B & C.",
            "-Next", "do the next thing",
            "-Refs", "note.one", "-Refs", "note.two",
        ]),

        new("thread update notes", "Update-ThreadItem.ps1",
            ["-Topic", "cache eviction", "-Notes", "replaced wholesale"]),

        new("thread update appending notes", "Update-ThreadItem.ps1",
            ["-Topic", "cache eviction", "-Notes", "and then this", "-AppendNotes"]),

        // Appending to an item whose notes are empty must not leave a blank line on top.
        new("thread update appending to empty notes", "Update-ThreadItem.ps1",
            ["-Topic", "cache warming", "-Notes", "first note", "-AppendNotes"]),

        new("thread update appending refs", "Update-ThreadItem.ps1",
            ["-Topic", "cache eviction", "-Refs", "note.cache", "-Refs", "note.new", "-AppendRefs"]),

        // Supplied-as-empty is a request to clear, and is not the same as not supplied.
        new("thread update clearing next", "Update-ThreadItem.ps1",
            ["-Topic", "cache eviction", "-Next", ""]),

        new("thread update status parks the other", "Update-ThreadItem.ps1",
            ["-Topic", "cache eviction", "-Status", "active"]),

        new("thread update by index", "Update-ThreadItem.ps1",
            ["-Index", "1", "-Next", "reached by position"]),

        new("thread complete by topic", "Complete-ThreadItem.ps1",
            ["-Topic", "cache eviction"]),

        // No selector means whatever is active.
        new("thread complete the active one", "Complete-ThreadItem.ps1", []),

        new("thread set active", "Set-ActiveThread.ps1", ["-Topic", "cache eviction"]),

        new("thread clear active", "Set-ActiveThread.ps1", ["-None"]),

        new("thread show", "Show-ThreadItems.ps1", []),

        new("thread show all", "Show-ThreadItems.ps1", ["-All"]),
    ];

    /// <summary>Turns a label into a file name. Stable, because it names a committed golden.</summary>
    public static string Slug(string label)
    {
        char[] characters = [.. label.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')];
        string slug = new(characters);

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }
}
