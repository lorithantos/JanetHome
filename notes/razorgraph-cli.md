# RazorGraph CLI — the same engine without the publish cycle

`D:\Repos\RazorGraphTool\src\RazorGraph.Cli` is the command-line twin of
`RazorGraph.Mcp` ([[note.razorgraph-mcp-server]]). Both sit on the same
`RazorGraph.Core` + `RazorGraph.Extractor` engine, so they answer the same questions
about the same graphs — but they differ in the one way that matters while the tool
itself is under development.

## Why it earns its own node

The MCP server launches a **published** copy out of `.mcp-bin`. A code change reaches
those tools only after closing every attached session, re-publishing, and restarting —
a three-step loop with a session restart in it. The CLI has no such loop:

```powershell
dotnet build src\RazorGraph.Cli\RazorGraph.Cli.csproj   # ~3s incremental
dotnet run --project src\RazorGraph.Cli -- query graph.json --deep 3
```

So **when the subject of the work is RazorGraph itself, the CLI is the only surface
that can see the change you just made.** The MCP tools are, by construction, reporting
on the last publish. This is not a preference between two front ends; querying the MCP
server about extractor behaviour mid-change gets you a confident answer about old code.

The corollary bites in the other direction too: a CLI result and an MCP result that
disagree is the expected state during development, not a bug. Check the publish date
before believing either.

## Surface (verified against `Program.cs`, 2026-08-08)

Six commands. `System.CommandLine`; every command's logic is a named static rather
than a `SetAction` lambda — deliberately, so the tool can see its own CLI in its own
graph (anonymous blocks compile under unrecoverable names).

| Command | Input | Does |
|---|---|---|
| `build` | `.csproj`, or `.sln` **with `--project`** | One project's graph → JSON |
| `build-solution` | `.sln` / `.slnx` | One graph spanning every project; the only source of cross-project edges |
| `query` | a built graph JSON | All the read queries |
| `body` | `.csproj` / `.sln` — **compiles** | One method's CFG as JSON on stdout |
| `body-diff` | `.csproj` / `.sln` — **compiles** | Flow-equivalence proof; exit 0 equivalent, 1 different, 2 error |
| `research` | a built graph JSON | Relevance-scored subgraph (1/(1+depth)) → file |

`query` modes: `--id` (with `--neighbors`, `--render-tree`, `--context`, `--trace`
`--depth` `--direction`, `--covering-tests`, `--covered-methods`), `--type` (+`--name`,
`--project`), `--uncovered`, `--deep N`, `--mismatches`, `--escapes` (+`--entry-kind`,
`--exception`).

## What will bite you

**Two different things point at a `.sln`.** `build App.sln --project Foo` builds
*Foo's* graph using the solution only for context; `build-solution App.sln` builds
*everything*, and is the only way to get an edge that crosses a project boundary.
Reaching for `build` on a solution and getting a graph with no cross-project `Calls`
or `Covers` looks like missing data and is actually the wrong command.

**`query` mode precedence is silent.** The dispatch order is fixed and load-bearing
(`RunQueryAsync`, most specific first): `--mismatches` → `--escapes` → `--uncovered` →
`--deep` → `--id` → `--type`. Pass two and the earlier one wins with no warning that
the other was ignored.

**`--deep 0` is indistinguishable from not passing `--deep`.** The mode is gated on
`Deep > 0`, so a zero threshold silently falls through to the next mode.

**`research` defaults `-o` to `research.json` in the current directory.** Run it from
`D:\Repos\JanetHome` without `-o` and it overwrites the Janet research graph — the file
the ENFORCED never-hand-edit rule exists to protect. Different `research.json`, same
name, no relation. Always pass `-o`.

**Output is human text, not the Janet envelope.** `query` prints lines for a terminal
with no `--json` flag anywhere; only `body` and `body-diff` emit JSON, and `research`
writes a file. The MCP twin returns compact JSON with
`{ returned, totalMatches, truncated }`. So the CLI is the better *development* surface
and the worse *scripting* surface — parse its output at your peril, and use
[[script.search-json]] against the saved graph JSON instead.

**`--uncovered` truncates at 200** with a `... N more not shown` line. Reported, not
silent, but it is a cap.

**`body` / `body-diff` do not read a saved graph.** They take a project or solution and
run a real compile every invocation, so they are slow in a way the other query commands
are not. `build` and `build-solution` are slow for the same reason; `query` and
`research` load JSON and are fast. Build once, query many times.

## Conflicts found 2026-08-08

Recorded rather than silently reconciled, per the base-truth rule:

- **`Property` and `Field` node types are documented but never emitted.**
  **RESOLVED same day at `cd12114`:** the extractor now emits `prop:`/`field:` nodes
  (statics included, `isStatic`/`isConst` marked), `Reads`/`Writes` edges attributed
  to the accessing method/ctor/property, and member→declared-type `References` edges
  (through `List<>`/array wrappers). Eleven integration tests over the MultiProject
  fixture. The gap survives in every saved graph predating `cd12114` and in the
  published `.mcp-bin` until re-published — date-check before trusting an empty
  `incoming` list.
- **Tool count.** The `note.razorgraph-mcp-server` summary said 21 tools; the README
  and the live registered surface both say 22. The extra one is `exception_escapes`
  (2026-08-07). Summary drift, not a disagreement about what exists — node summary
  re-synced 2026-08-08.

Related: [[note.razorgraph-mcp-server]], [[pattern.graph-first-analysis]],
[[skill.christmas-tree-flattening]], [[script.search-json]].
