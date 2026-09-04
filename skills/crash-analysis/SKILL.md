---
name: crash-analysis
description: Diagnose an app that dies without a trace — exits with no dialog or log, crashes intermittently, only on first run, or only outside the debugger. Use when the user reports an app "just closing", an unexplained process exit, or asks where an exception could be escaping; also before shipping a UI host, to audit where its escapes go.
---

# Crash-escape analysis

This skill is a pointer, not a store. The full workflow, the war story it was
distilled from, and its blind spots live in the JanetHome research graph —
query it instead of trusting this file's age.

```powershell
$janet = if ($env:JanetBase) { $env:JanetBase } else { 'C:\repos\lorithantos\JanetHome' }
```

## Invariants (the only content that belongs here)

1. Treat the crash as a reachability question: somewhere a throw reaches a
   process boundary with no catch that handles it. Find the path, not the blame.
2. Check the process-level exception surface FIRST — it is one graph query
   (`get_node` on the `Application`/host class), and if it is empty, every
   later finding is fatal until a backstop exists.
3. Graph before reading: `exception_escapes` (RazorGraph) for the shortlist,
   `trace_data_flow` for the closed candidate list; read source only to rule
   sites out. Mind the tool's `caveats` — what it cannot see (BCL, lambdas,
   virtual dispatch) still needs a manual pass over callback surfaces.
4. Fix in layers, all four: guard at the throw site, catch at the operation,
   backstop at the process, crash log so "somewhere" has a name next time.

## Everything else: query the graph

```powershell
& $janet\scripts\Get-Research.ps1 -Id skill.crash-escape-analysis -Expand   # the full workflow + neighbours
& $janet\scripts\Get-Research.ps1 -Id note.razorgraph-mcp-server            # graph-tool caveats that change conclusions
& $janet\scripts\Get-Research.ps1 -Query 'crash exception escape'           # anything newer than this file
```

New crash-analysis lessons go into the graph (`Add-ResearchNode.ps1` /
`Update-ResearchNode.ps1`), NOT into this file.
