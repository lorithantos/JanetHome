# JanetHome

**Tooling and design patterns for making AI coding agents reliable: manifest-driven
startup, a queryable research graph, graph-first code analysis, and contracts that
fail loudly instead of degrading quietly.** Everything here is a runnable tool —
an MCP server, a command-line twin, or a PowerShell utility — or a written-up
pattern with the reasoning attached.

New here? Jump to the [quick start](#quick-start). Want the ideas rather than the
tools? Read [`DESIGN-NOTES.md`](DESIGN-NOTES.md) — it stands alone.

## Why Janet exists

An agent's context window is its scarcest resource, and most of it gets spent
badly: re-reading files to rediscover what a catalog could have answered in one
query, carrying every instruction for every task on every turn, scraping build
scrollback for a pass/fail a structured envelope states outright. Wasted tokens
are not just cost — they crowd out the context the actual problem needed, and
quality degrades with them.

Janet is a tooling platform built on one trade: **spend effort once, in tooling,
so the agent spends tokens only on judgment.** Retrieval is ranked and reports its
own truncation. Facts come from contracts and compilers instead of being
plausibly guessed. Mechanical work is executed deterministically from a reviewed
plan rather than performed token by token. Every failure path is designed to be
loud, because a quiet failure costs more context to detect than any tool costs
to run.

The aim is not a faster agent — it is a place where a codebase can become its
best self: correct by evidence rather than by confidence, with the agent's
capacity pointed at the decisions that deserve it.

The name has a story, and it turned out to be a spec:
[Janet — the origin story](notes/janet-origin.md).

## What this is for

Agents fail in characteristic ways: they summarise stale documents as if current,
guess at code structure that a compiler could tell them, lose their place in
depth-first debugging, silently truncate, and trust their own output. Each artifact
in this repo exists to close one of those gaps:

| Problem | Answer here |
|---|---|
| Prose context files drift silently | `startup-manifest.json` — a checkable startup contract; a broken entry fails loudly |
| "What exists?" answered by grepping | `research.json` + `research_query` / `janet research` — a ranked, self-truncation-reporting catalog of every script, pattern, and note |
| Agents guess at code semantics | The graph-first pattern (DESIGN-NOTES §4), implemented as [RazorGraphTool](https://github.com/lorithantos/RazorGraphTool) |
| Debugging loses the unwind path | Thread items — `thread_add` / `janet thread`, a focus-explicit investigation list where nothing is ever deleted |
| Guessing a library's API costs a build per wrong guess | `api_doc_query` / `assembly_api` — ranked search of a package's XML docs, and what a compiled assembly actually declares |
| Mechanical multi-file edits drift | `Invoke-SurgicalEdit.ps1` — the model plans as JSON, a script executes deterministically |
| Build/test output gets scraped | `dotnet_check` / `janet check` — structured, contract-numbered JSON instead of scrollback |

The common thread: **prefer contracts that fail loudly over documents that degrade
quietly**, and give the agent evidence instead of the opportunity to guess.

## Quick start

```powershell
git clone https://github.com/lorithantos/JanetHome
cd JanetHome

# Build and install the two tools. Nothing is on nuget.org yet, so pack locally:
dotnet pack JanetHome.slnx -c Release -o .janet-bin
dotnet tool install --global --add-source .janet-bin Janet.Cli   # -> janet
dotnet tool install --global --add-source .janet-bin Janet.Mcp   # -> janet-mcp

# The catalog is the entry point — ask it what exists before opening anything:
janet research query --base .                       # kinds + tag index (cheap)
janet research query --query 'thread items'         # scored search, top 5
janet research query --id pattern.graph-first-analysis --expand

# Running an agent session against this repo? Let startup run the contract:
./scripts/Invoke-JanetStartup.ps1
```

To reach the same catalog from an MCP client, start the server and point the client
at it. Installing as a global tool puts it on `PATH`, so the config carries no
absolute path:

```powershell
janet-mcp --http --port 7717 --base D:/Repos/JanetHome
```

```json
{ "mcpServers": { "janet": { "type": "http", "url": "http://127.0.0.1:7717/" } } }
```

The HTTP transport is the one to develop against: the server is a separate process
the client dials, so it can be killed, rebuilt and restarted while a session stays
open — `scripts/Update-McpServer.ps1` does that rotation in about two seconds.
`janet-mcp` with no `--http` speaks stdio instead, which every MCP client supports
and no client reconnects to.

The tools need the **.NET 10 SDK**; the scripts target **PowerShell 7**. Some carry
known limitations; retrieval always prints a node's `caveats`, so ask the catalog
rather than trusting a tool blind.

## The catalog is the entry point

Everything in this repo is a node in `research.json`, and querying it is the
starting move for any question — not reading files, not grepping. That is why this
README carries no script inventory: it would be a copy that drifts from the thing
it describes, which is the exact failure the manifest pattern exists to prevent.
The catalog answers *what exists*; `-?` answers *how to call it*; a node's
`caveats` answer *what will bite you*.

Don't hand-edit `research.json` — `research_add` adds, `research_update` changes,
`research_rename` moves a node and every link pointing at it. `janet research
add|update|rename` and the `Add-`/`Update-`/`Rename-ResearchNode.ps1` scripts are
the same operations; all of them validate, then *splice into the file's existing
text* rather than reserializing it, which is what keeps the grouping and comment
keys a serializer would flatten.

The three front ends share one implementation (`src/Janet.Core`) so they cannot
disagree, and the scripts are now shims over the CLI. The CLI is not a convenience:
hooks run as separate processes and cannot speak MCP, so a command-line entry point
has to exist for them to call.

Shimming costs something real, though: a script you can read and run is worth more than
one that forwards to a binary you have to build first. So the last self-contained version
of every shimmed script is kept in [`standalone/`](standalone/) — the catalog and the
thread-item list in PowerShell 7 alone, no SDK, no build, nothing to install. They are
frozen rather than maintained, and `scripts/Test-StandaloneScripts.ps1` checks they are
still what they claim to be.

## Layout

| Path | Contents |
|---|---|
| `DESIGN-NOTES.md` | The transferable patterns, with the failures that motivated them |
| `research.json` | The node graph. Queried on demand, never loaded wholesale |
| `src/` | `Janet.Core` (all behaviour), `Janet.Mcp` (the server), `Janet.Cli` (its twin) |
| `tests/` | `Janet.Tests`, and `Janet.Goldens` — which records what the PowerShell answered so the tests need no PowerShell |
| `scripts/` | Domain-agnostic PowerShell utilities — ask the catalog for the inventory |
| `standalone/` | The nine shimmed scripts, in their last self-contained form. Frozen; no SDK needed |
| `notes/` | Research notes, original analysis, PowerShell house rules |
| `startup-manifest.json` | The startup contract: what to read, what to run, the rules in force |
| `PROVENANCE.md` | What this repo deliberately does not contain, and why |

## License

MIT. If the patterns are useful, take them — the design notes exist to be reused.
