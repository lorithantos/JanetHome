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
reader, and it carries the contract's text without the project context that backs it —
a pasted brief once claimed two `ENFORCED` rules in a session where neither hook was
loaded. `CLAUDE.md` states this for the agent; `notes\startup-brief-budget.md` has the
incident. Every run also writes `.janet\last-brief.json` for ingestion without a re-run.

Reads `startup-manifest.json`, sets `$env:JanetBase`, verifies every entry resolves
before running anything, and emits the session brief as JSON. A broken entry is a hard
failure, not a quiet degradation — see DESIGN-NOTES §1. Use `-SkipRun` to lint the
manifest, `-Text` to read it at a terminal, `-Pretty` to indent the JSON.

The brief is **trimmed** by default: it carries the contract and drops reference material
the session can retrieve on demand. `-Full` restores everything. The untrimmed brief was
measured at 4525 characters with 84% of it prose that wasn't the contract — see
`notes\startup-brief-budget.md`, which also records the switch-parameter collision that
adding `-Full` exposed.

The brief deliberately does **not** include the tool inventory. Everything in the repo is
a node in `research.json`; startup carries only a pointer, and you pull what you need:

```powershell
& .\scripts\Get-Research.ps1                            # kinds + tag index (cheap)
& .\scripts\Get-Research.ps1 -Query 'thread stack'      # scored top 5; -First N / -All to widen
& .\scripts\Get-Research.ps1 -Id pattern.thread-stack -Expand   # node plus its neighbours
```

Free-text queries are **ranked**, not just filtered — terms score independently, tag hits
outweigh prose hits, and the result is a shortlist you can choose from without ever
opening `research.json`. Truncation is always reported (`top 5 of 10 matches`); explicit
`-Id`/`-Tag` lookups are never capped.

### Output is JSON by default

These scripts are read by a model far more often than by a person, so the machine form
is the default and the formatted view is the opt-in. Add `-Text` to read at a terminal,
`-Pretty` to indent the JSON.

Queries return an envelope — `{ returned, totalMatches, truncated, nodes[] }` — so check
`truncated` before concluding you've seen everything. An empty result uses the same
shape, so consumers never special-case it.

Both forms forward fields they don't recognize, so growing the node schema never
silently loses data. That's the main reason not to format for humans first: it makes the
schema hostage to a formatter someone has to remember to update.

That's DESIGN-NOTES §2 applied to the index itself — a full catalog spends tokens every
session to answer a question most sessions never ask.

## Add to the graph

Growing the graph is the point of the graph, so adding costs one command. **Don't
hand-edit `research.json`.**

```powershell
& .\scripts\Add-ResearchNode.ps1 -Id note.some-finding -Kind note `
    -NodePath 'notes\some-finding.md' -Summary 'What I learned.' `
    -Tags research,powershell -Links pattern.thread-stack -DryRun
```

To change one:

```powershell
& .\scripts\Update-ResearchNode.ps1 -Id script.foo -Caveats 'Needs csharp-ls.' -Append
```

Duplicate ids are a hard stop; a missing file or dangling link warns; a splice that would
produce invalid JSON aborts without writing. Insertion and update are both textual, so
the file keeps its comments, grouping, and blank lines instead of every edit becoming a
whole-file diff.

`Update-ResearchNode` preserves every field you don't name — **including fields it has
never heard of** — and returns the before/after of each change. `-Append` adds to arrays
instead of replacing them; `-Remove` deletes a field, except the required ones.

### Caveats

Nodes carry a `caveats` array — missing dependencies, external services contacted,
platform assumptions, outright breakage. Retrieval **always** prints them, prefixed `!`;
a warning behind a flag is not a warning.

In ranking, caveats **demote and never select.** A term appearing in what's wrong with a
node is weaker evidence than the same term in what it's for, and nothing should surface
purely for describing its own breakage.

Several scripts here are known-broken and say so — `Test-PreCommit.ps1` and
`New-RepoStatusPage.ps1` both dot-source a `lib\Invoke-External.ps1` that never came
across in the extraction.

## Layout

| Path | Contents |
|---|---|
| `DESIGN-NOTES.md` | The transferable patterns — manifest-driven startup, progressive disclosure, thread stack, graph-first code analysis, deterministic edits, circuit-breakers, the handoff-corpus format. Start here |
| `PROVENANCE.md` | What was carried over from the departure corpus, from where, and on what basis. Also records what was deliberately left behind |
| `startup-manifest.json` | The startup contract: what to read, what to run, the operating rules |
| `research.json` | Node graph of every script, pattern, note, and file. Queried on demand, never loaded wholesale |
| `scripts\` | Domain-agnostic PowerShell utilities. No employer coupling. Ask the graph for the current inventory rather than trusting a count here |
| `notes\` | Personal research notes, original analysis, and the PowerShell house rules |

## Scripts worth knowing about

- `Push-ThreadStack.ps1` / `Pop-ThreadStack.ps1` / `Show-ThreadStack.ps1` — investigation
  topic stack. Highest value-to-complexity ratio in the toolkit; see DESIGN-NOTES §3
- `Get-ApiDoc.ps1` — the retrieval contract above, pointed at a .NET XML doc file
  instead of `research.json`. Researching a library by grepping its docs is
  expensive and bad at the job: the file is one stream of hard-wrapped `<member>`
  elements, so a hit costs dozens of context lines and the answer arrives split
  across them. This returns parsed members — signature, summary, per-parameter
  docs — resolves the `.xml` out of the NuGet cache by package id, and follows
  `<inheritdoc/>` chains so a member documented on its interface still answers

  ```powershell
  & .\scripts\Get-ApiDoc.ps1 -Package LiveChartsCore                          # orientation
  & .\scripts\Get-ApiDoc.ps1 -Package LiveChartsCore -Query 'tooltip formatter'
  ```

- `New-TextFile.ps1` — writes files from PowerShell without here-string, escaping, or
  BOM pain. Base64 input mode sidesteps quoting entirely
- `Fix-FileEncoding.ps1` — two-pass encoding repair that survives raw Windows-1252
  bytes in the 0x80–0x9F range, which .NET's UTF-8 decoder otherwise turns into `?`
- `Invoke-SurgicalEdit.ps1` — executes a JSON plan of exact edit operations. Built for
  agent workflows: the model decides, the script performs; see DESIGN-NOTES §5
- `ConvertTo-MermaidEmbed.ps1` — Mermaid → inline `<img>`, renders in most markdown viewers
- `Test-PowerShellRules.ps1` — lints the house rules in `notes\powershell-house-rules.md`

`Invoke-JanetStartup.ps1` sets `$env:JanetBase` for you. Setting it by hand is only
needed if you invoke a script without going through startup.

## PowerShell

Scripts target **PowerShell 7** (pwsh, installed 2026-07-27). `notes\powershell-house-rules.md`
records the rules and why each one exists; `Test-PowerShellRules.ps1` enforces the
checkable subset. Add `-Target Ps51` before sharing a script — stock Windows still ships
5.1, and the differences fail in ways that print success.

Eight scripts still carry stale `$env:JanetBase\.github\scripts\...` paths in their
`.EXAMPLE` blocks, left from the previous repo layout. Documentation only; the code
resolves from `$PSScriptRoot`.

## Boundary

This repo contains no employer source code, internal architecture, production
telemetry, security-posture information, or personal data about former colleagues.
That boundary is deliberate and documented in `PROVENANCE.md`. Keep it.

Related: `D:\repos\Janet\JanetRuntimeContract.md`.
