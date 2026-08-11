# JanetHome

**Tooling and design patterns for making AI coding agents reliable: manifest-driven
startup, a queryable research graph, graph-first code analysis, and contracts that
fail loudly instead of degrading quietly.** Everything here is either a runnable
PowerShell utility or a written-up pattern with the reasoning attached.

New here? Jump to the [quick start](#quick-start). Want the ideas rather than the
tools? Read [`DESIGN-NOTES.md`](DESIGN-NOTES.md) — it stands alone.

## What this is for

Agents fail in characteristic ways: they summarise stale documents as if current,
guess at code structure that a compiler could tell them, lose their place in
depth-first debugging, silently truncate, and trust their own output. Each artifact
in this repo exists to close one of those gaps:

| Problem | Answer here |
|---|---|
| Prose context files drift silently | `startup-manifest.json` — a checkable startup contract; a broken entry fails loudly |
| "What exists?" answered by grepping | `research.json` + `Get-Research.ps1` — a ranked, self-truncation-reporting catalog of every script, pattern, and note |
| Agents guess at code semantics | The graph-first pattern (DESIGN-NOTES §4), implemented as [RazorGraphTool](https://github.com/lorithantos/RazorGraphTool) |
| Debugging loses the unwind path | Thread items — `Add-ThreadItem.ps1` and friends, a focus-explicit investigation list |
| Mechanical multi-file edits drift | `Invoke-SurgicalEdit.ps1` — the model plans as JSON, a script executes deterministically |
| Build/test output gets scraped | `Invoke-DotnetCheck.ps1` — structured, contract-numbered JSON instead of scrollback |

The common thread: **prefer contracts that fail loudly over documents that degrade
quietly**, and give the agent evidence instead of the opportunity to guess.

## Quick start

```powershell
git clone https://github.com/lorithantos/JanetHome
cd JanetHome

# The catalog is the entry point — ask it what exists before opening anything:
./scripts/Get-Research.ps1                          # kinds + tag index (cheap)
./scripts/Get-Research.ps1 -Query 'thread items'    # scored search, top 5
./scripts/Get-Research.ps1 -Id pattern.graph-first-analysis -Expand

# Running an agent session against this repo? Let startup run the contract:
./scripts/Invoke-JanetStartup.ps1
```

Scripts target **PowerShell 7**. Some carry known limitations; retrieval always
prints a node's `caveats`, so ask the catalog rather than trusting a script blind.

## The catalog is the entry point

Everything in this repo is a node in `research.json`, and querying it is the
starting move for any question — not reading files, not grepping. That is why this
README carries no script inventory: it would be a copy that drifts from the thing
it describes, which is the exact failure the manifest pattern exists to prevent.
The catalog answers *what exists*; `-?` answers *how to call it*; a node's
`caveats` answer *what will bite you*.

Don't hand-edit `research.json` — `Add-ResearchNode.ps1` adds,
`Update-ResearchNode.ps1` changes, both validate before splicing.

## Layout

| Path | Contents |
|---|---|
| `DESIGN-NOTES.md` | The transferable patterns, with the failures that motivated them |
| `research.json` | The node graph. Queried on demand, never loaded wholesale |
| `scripts/` | Domain-agnostic PowerShell utilities — ask the catalog for the inventory |
| `notes/` | Research notes, original analysis, PowerShell house rules |
| `startup-manifest.json` | The startup contract: what to read, what to run, the rules in force |
| `PROVENANCE.md` | What this repo deliberately does not contain, and why |

## License

MIT. If the patterns are useful, take them — the design notes exist to be reused.
