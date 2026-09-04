---
name: agent-kickoff
description: Shape the prompt before launching a subagent with the Agent tool. Use whenever you are about to call Agent, hand work to a subagent, or are told to "delegate this" -- and whenever a task would generate tool output (grep, sweeps, builds, tests, web searches, reading several files) that matters only in flight. Every prompt it produces carries explicit RazorGraph instructions, because delegated agents must reach for the graph first and grep second.
---

# Agent kickoff

This skill is a pointer, not a store. The doctrine is `note.subagent-delegation`; the
graph rule is `pattern.graph-first-analysis` and `skill.graph-first-code-analysis`; the
tool's blind spots are on `note.razorgraph-mcp-server`. Query those, not this file's age.

```powershell
$janet = if ($env:JanetBase) { $env:JanetBase } else { 'C:\repos\lorithantos\JanetHome' }
```

## When to delegate

1. The test is VOLUME, not size. A one-line question whose answer needs a repo-wide
   grep is an agent task; a long piece of reasoning that needs no tools is not.
2. The orientation pass counts. Reading four files to shape a prompt IS the volume --
   dispatch with the constraints you already know and let the agent read.
3. Keep what is cheap to state and expensive to get wrong: what a result means, what to
   do next, what to tell the user. Delegate what is expensive to read and cheap to
   summarise.
4. Not worth it: one known file, one known symbol, a two-line edit.

## Before writing the prompt

Answer DESIGN-NOTES section 7's two questions for the agent, then quote them into the
task: what exactly is it trying to learn, and what is the cheapest query that
distinguishes the hypotheses. Agents inherit the tool surface and NONE of the operating
rules, so every rule below must be written in, not assumed.

## Prompt template (fill every bracket; drop no section)

```
## Task
[What to do. What done looks like. The two questions above, answered.]

## Environment
Repo [C:\repos\...]; Windows 11; PowerShell 7 -- invoke scripts with pwsh; git on PATH;
today is [YYYY-MM-DD]. Scratchpad for temp files: [path]. Absolute paths everywhere.

## RazorGraph -- FIRST for any question about C# structure
Load the tools: ToolSearch("select:mcp__razorgraph__list_graphs,mcp__razorgraph__graph_summary,
mcp__razorgraph__find_nodes,mcp__razorgraph__get_node,mcp__razorgraph__find_path,
mcp__razorgraph__research[,mcp__razorgraph__covering_tests,mcp__razorgraph__exception_escapes,
mcp__razorgraph__method_body_graph,mcp__razorgraph__render_tree -- as the task needs]")
Get a graph: call list_graphs; use graphId "[janet]" (built from [C:\...\X.slnx]). If it is
not listed: load_graph path=[saved .json] if one exists, else build_solution
path=[C:\...\X.slnx] graphId=[name] (slow: a full Roslyn compile). Pass graphId on every
call. Check loadedAt against the edits you make -- rebuild after changing C#.
Graph first, grep second. Grep, Glob and Read are for (a) spot-checking a claim the graph
made and (b) text the graph does not index -- prose, JSON, .ps1, config. They are never
the first move on callers, implementers, blast radius or test reach. If ToolSearch
returns no razorgraph tools, say so in your report before falling back to text.
Per query: name what you are learning and the cheapest query that settles it. Check
'truncated' in every result. Ids are exact: m:Type.Name(paramTypes). Blind spots:
ExternalType holds attributes only; return and parameter types carry no edges -- use text
for those and say which tool found what.

## Janet
Research first: `janet research query --base [repo] --query "..."` before building a
tool or hand-rolling a technique; `--id <id> --expand` for a node and its neighbours.
Never hand-edit research.json -- `janet research add` / `update` only.
Work you surface but are not doing: `janet thread add --topic "..." --area "[area]"
--notes "..."`. Set --area or it lands in (unfiled).
Any .ps1 you touch: `pwsh [repo]\scripts\Test-PowerShellRules.ps1 -Path <file>`; fix
what it reports. Files are CRLF and ASCII-only (write `--`, not em dashes); the Write
tool emits LF, so convert, then verify with `Test-FileEncoding.ps1 -Path <f> -ExpectCrlf`.

## Testing
Mutation-check every test you add: break the subject, see the test fail, restore, see it
pass. Report the mutation you used. A test that never failed proves nothing.

## Report (under [N] words)
[What to bring back: findings, files changed with absolute paths, node ids.] Exact error
text on any failure. What you could not verify, stated as such.

## Do not
Commit or push unless told. Edit outside [scope]. Fix when asked only to investigate.
Report a narrative instead of findings.
```

## Checklist before sending

- Names the graphId (and the .slnx to build if absent)?
- The ToolSearch line lists exact `mcp__razorgraph__` tool names?
- Says grep is second, and what grep IS for?
- Sets `--area` for thread items?
- Gives the scratchpad path and the date?
- States the report shape, word budget, and the boundary (no commit/push, scope)?

If any answer is no, the agent will run text-first and speculate. Observed 2026-08-11.

## Everything else: query the catalog

```powershell
& $janet\scripts\Get-Research.ps1 -Id note.subagent-delegation -Expand     # doctrine, cost, the split
& $janet\scripts\Get-Research.ps1 -Id skill.graph-first-code-analysis      # the graph workflow to quote
& $janet\scripts\Get-Research.ps1 -Id note.razorgraph-mcp-server           # caveats that change conclusions
& $janet\scripts\Get-Research.ps1 -Query 'subagent delegation'             # anything newer than this file
```

New delegation lessons go into the catalog (`janet research update`), NOT into this file.
