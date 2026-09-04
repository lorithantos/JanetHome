# JanetHome

## Start every session by running startup yourself

```powershell
& .\scripts\Invoke-JanetStartup.ps1
```

Then read every file it lists under `read`, in order, and treat the entries under
`rules` as in force.

**Do not accept the brief as pasted text.** Running it yourself is not a convenience:

- A pasted brief truncates. It has, mid-entry, silently.
- A pasted brief cannot be re-run. If you need it again, or need `-Full`, you are
  stuck with whatever text arrived.
- A pasted brief carries the *text* of the contract without the *context* it
  describes. On 2026-08-01 a brief asserted two `ENFORCED` rules in a session where
  neither backing hook was loaded, because the session's project dir was a different
  repo. `$env:JanetBase` was inherited; the hooks were not.

If you were handed a brief on the command line, run the script anyway and use its
output instead.

## The hooks are wired at two levels

The edit guard and the lint hook load from whichever settings the harness reads:

- **Project-level** (`.claude\settings.json` in this repo) -- resolves the scripts
  through `$env:CLAUDE_PROJECT_DIR`, so it is live only when this repo *is* the
  project dir.
- **User-level** (`~\.claude\settings.json`, wired 2026-08-01) -- runs the same
  scripts by absolute path whenever the project dir is *not* this repo, so Janet
  sessions launched from other directories on this machine keep both guards. It
  skips when the project dir is this repo, so the two levels never double-fire.

If neither is wired -- another machine, or the user settings pruned -- startup still
runs; it does not depend on where you launched it from. What it will not do is lie:
each hook-backed rule is relabelled `ADVISORY (claims ENFORCED; hook not wired
here)`, and an `enforcement:` entry appears under the brief's `enforcement` field
naming the project dir it checked and the scripts it could not find.
(`enforcement`, not `problems`: consumers such as Start-Janet.ps1 treat `problems`
as fatal, and an unwired hook must not be.) Treat that as a live warning: the rules
still hold, only the mechanism that would catch you breaking them is missing, so
honour them by hand.

A manifest entry that does not resolve is the other case, and that one is still a
hard stop.

## The brief is also on disk

Every run writes `.janet\last-brief.json`. Read that if you need the brief without
re-running startup -- but prefer re-running, since the file is only as current as the
last run. `-OutFile` changes the path; `-OutFile ''` skips writing.

## Working here

- `-Full` for every manifest field, `-Text` to read at a terminal, `-Pretty` to
  indent, `-SkipRun` to lint the manifest without executing anything.
- The tool inventory is deliberately not loaded. Query `research.json` and pull the
  two or three nodes you need -- through the `research_query` MCP tool if it is
  connected, otherwise `janet research query` or `scripts\Get-Research.ps1`.
- Never hand-edit `research.json` -- `research_add` / `research_update`, or their
  `janet research` and `Add-`/`Update-ResearchNode.ps1` equivalents. All three are
  the same code; the scripts shim to the CLI.
- Thread items are the backlog: `thread_report` on resuming, `thread_add` the moment
  you notice work you are not doing now. Adding does not take focus, so noting
  something costs nothing, and completing keeps the item rather than deleting it.
  `janet thread ...` and the `*-ThreadItem.ps1` scripts are the same code.
  Report, not show: the list is shared by every repo on this machine, so an
  unnarrowed `thread_show` returns every note in full and is refused over the
  result budget (100,000 characters; `JANET_RESULT_BUDGET` overrides) with a
  narrowing hint rather than cut. Narrow it -- `topic` for one item's notes, `area` for one
  project's -- and set `area` on what you add, or it lands in `(unfiled)`.
- Run `scripts\Test-PowerShellRules.ps1` on any `.ps1` you touch. The hook does this
  when it is wired; do it yourself when it is not.
- `dotnet test JanetHome.slnx` for the C# side. The catalog tests compare against
  recorded answers in `tests\Janet.Tests\Goldens`, captured from the PowerShell as it
  stood before the shims. Regenerate them with `dotnet run --project
  tests\Janet.Goldens`, not by hand -- a golden the implementation edits is not a
  golden.

Orientation lives in `README.md`; the patterns and their rationale in `DESIGN-NOTES.md`.
