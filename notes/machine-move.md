# The machine move, and what it broke quietly

The work moved to a different machine. The old one kept its repositories under
`D:\Repos`; this one keeps them under `C:\repos`. Absolute paths naming the old
location sat in the catalog and in a permission allow-list for weeks afterwards,
all of them dead, none of them ever failing loudly.

Swept 2026-09-01. Recorded because two things here are worth more than the
rename: **which kinds of reference degrade silently**, and **why a machine move is
not a path rewrite**.

## A machine move is not a drive rename

The tempting reading is that `D:\Repos\X` became `C:\repos\X` and a
find-and-replace closes it. That is true for what was *carried over* and false
for everything else, and the difference is invisible in the text. A path is
equally dead in both cases; only checking tells you which.

On this machine:

- `RazorGraphTool` and `ImageSelectorV2` were carried over (the latter cloned
  back on 2026-09-01) and their paths were genuinely just wrong.
- **`DriveSurvey` is not here at all.** Searched `C:\repos`, `C:\Users\lori_`,
  `F:\repos` and `D:\` to depth 2 — nothing. It is also **not on GitHub** under
  `lorithantos/DriveSurvey`, nor as `drive-survey`, `DriveSurveyTool`, `Survey`,
  `drivesurvey-cli` or `DriveSurveyCli`; `ImageSelectorV2` resolves from the same
  shell, so that is a real absence and not a credential failure. It is on the old
  machine, or unpushed, or under a name not guessed here. **Recovering it needs
  the remote, which only Lori has.**
- `git` is not installed independently — only Visual Studio's bundled copy, and
  not on `PATH` in a session that loads no profile. `scripts\git.ps1` is what
  resolves it. See `script.git`.

So the sweep corrected two paths, and *removed* the third rather than replacing
it with a guess. A corrected path that is also wrong is worse than an obviously
dead one, because it looks maintained.

## What was corrected

| Node | Where the dead path was | Now |
|---|---|---|
| `pattern.graph-first-analysis` | summary and a caveat | `C:\repos\RazorGraphTool` |
| `note.razorgraph-mcp-server` | summary | `C:\repos\RazorGraphTool\src\RazorGraph.Mcp` |
| `note.razorgraph-cli` | summary | `C:\repos\RazorGraphTool\src\RazorGraph.Cli` |
| `file.lightroom-api-index` | summary | `C:\repos\ImageSelectorV2\tools\Build-LightroomApiIndex.ps1` |
| `script.get-drive-survey` | summary | *removed — repo not on this machine* |

Every replacement was `Test-Path` checked before it was written.

`RetirementCore\.claude\settings.json` held two permission rules naming
`D:\Repos\JanetHome\scripts\Get-Research.ps1`. They granted nothing — the pattern
could never match — so the effect was an allow-list that read as more permissive
than it was, and a script that prompted when it was meant to be pre-approved.
`Test-ConfigPaths` had been naming them since the move and deliberately refusing
to apply the fix, because editing an allow-list is a privilege change and belongs
to a person. Corrected on an explicit instruction, not on the script's suggestion.

## Why none of it failed loudly

Three silent-failure shapes, worth recognising because they recur:

- **A path inside prose.** A summary or caveat naming a directory is read, not
  resolved, so nothing ever checks it. It stays syntactically fine and factually
  wrong indefinitely — the degradation the manifest pattern (DESIGN-NOTES §1)
  prevents for startup, and which the catalog's own free text is not protected
  against.
- **A permission pattern that cannot match.** An allow-list entry for a path that
  does not exist is not an error. It is never consulted, and the only symptom is
  an unexpected prompt long after the cause.
- **An existence check standing in for a correctness check.** The parked thread
  item on RazorGraphTool's `.mcp.json` records the same trap from the other side:
  `Test-ConfigPaths` reported ok because a stale flat publish happened to leave a
  file where the config pointed. Existence is not correctness.

## What is still unswept

Absolute paths are not confined to the catalog. `script.ensure-razorgraph-server`
carries a machine-specific `-Repo` default of `C:\repos\RazorGraphTool`, declared
in its own caveats — correct on this machine, and a landmine on the next one. The
sweep did not try to remove machine-specific values, only to make them visible.
The next move will find them the same way this one did.
