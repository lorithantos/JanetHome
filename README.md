# JanetHome

Civilian rebuild of the Janet framework. Clean-room starting point: personal tooling
and portable design knowledge, with nothing carried over from employer systems.

Started 2026-07-27.

## Start a session

Launch with **this repo as the project dir**, then let the session run startup itself:

```powershell
& .\scripts\Invoke-JanetStartup.ps1
```

Do not paste the brief in on the command line. It truncates, it can't be re-run by the
reader, and it carries the contract's text without the project context that backs it --
a pasted brief once claimed two `ENFORCED` rules in a session where neither hook was
loaded. `CLAUDE.md` states this for the agent; `note.startup-brief-budget` has the
incident. Every run also writes `.janet\last-brief.json` for ingestion without a re-run.

Startup reads `startup-manifest.json`, sets `$env:JanetBase`, verifies every entry
resolves before running anything, and emits the session brief as JSON. A broken entry
is a hard failure, not a quiet degradation -- DESIGN-NOTES section 1.

## Research first -- that is the entry point

**Everything in this repo is a node in `research.json`, and querying it is the
starting move for any question.** Not reading files, not grepping: ask the graph what
exists, then open only what it points at.

```powershell
& .\scripts\Get-Research.ps1                            # kinds + tag index (cheap)
& .\scripts\Get-Research.ps1 -Query 'thread stack'      # scored top 5; -First N / -All
& .\scripts\Get-Research.ps1 -Id pattern.thread-items -Expand  # a node plus neighbours
```

That is why this README no longer carries a script inventory or a parameter manual.
Both used to sit here and both were already nodes, so every session paid for a copy
that could silently drift from the thing it described -- the failure DESIGN-NOTES section 1
exists to prevent, and the one `note.startup-brief-budget` caught a level up. The
catalog answers *what exists*; `-?` answers *how to call it*; the node's `caveats`
answer *what will bite you*. Ask, don't assume.

Queries are **ranked**, not just filtered, and always report their own truncation.
`Get-ApiDoc.ps1` applies the same contract to a .NET library's XML documentation, so
researching an unfamiliar API is a query rather than a grep.

**Don't hand-edit `research.json`** -- that is hook-enforced. `Add-ResearchNode.ps1`
adds, `Update-ResearchNode.ps1` changes, `-DryRun` previews, and both validate before
they splice.

## Layout

| Path | Contents |
|---|---|
| `DESIGN-NOTES.md` | The transferable patterns -- manifest startup, progressive disclosure, thread items, graph-first analysis, deterministic edits, circuit-breakers. The operating rules live here, not in a system prompt |
| `PROVENANCE.md` | What was carried over from the departure corpus, from where, and on what basis -- and what was deliberately left behind. Read before adding anything |
| `startup-manifest.json` | The startup contract: what to read, what to run, the operating rules |
| `research.json` | Node graph of every script, pattern, note, and file. Queried on demand, never loaded wholesale |
| `scripts\` | Domain-agnostic PowerShell utilities. Ask the graph for the inventory |
| `notes\` | Research notes, original analysis, and the PowerShell house rules |

## PowerShell

Scripts target **PowerShell 7** (installed 2026-07-27). `note.powershell-house-rules`
records the rules and why each exists; `Test-PowerShellRules.ps1` enforces the
checkable subset. Add `-Target Ps51` before sharing a script -- stock Windows still
ships 5.1, and the differences fail in ways that print success.

Several scripts are known-broken and say so in their `caveats`. Retrieval always
prints those; a warning behind a flag is not a warning.

## Boundary

This repo contains no employer source code, internal architecture, production
telemetry, security-posture information, or personal data about former colleagues.
That boundary is deliberate and documented in `PROVENANCE.md`. Keep it.

Related: `D:\repos\Janet\JanetRuntimeContract.md`.
