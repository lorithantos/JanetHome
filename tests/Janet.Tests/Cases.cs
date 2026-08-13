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
