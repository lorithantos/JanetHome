# standalone

**The catalog and the thread-item list, in PowerShell 7 alone.** No .NET SDK, no build,
no MCP server, no global tool. Clone the repo and run them.

Twelve of the scripts in `scripts\` are now shims: they forward to the `janet` CLI, where
the implementation lives in `src\Janet.Core` so the three front ends cannot disagree.
That is the right trade for maintaining the tools, and a bad trade for someone who wants
to read a script, understand it, and use it. These are the last self-contained versions
of those twelve, kept so that path stays open.

## Use

```powershell
# The catalog. Resolves ..\research.json by default -- see "Where they live", below.
.\Get-Research.ps1                                  # orientation: kinds + tag index
.\Get-Research.ps1 -Query 'thread items' -First 5   # ranked search
.\Get-Research.ps1 -Id pattern.thread-items -Expand

.\Add-ResearchNode.ps1 -Id note.example -Kind note -Summary '...' -NodePath README.md
.\Update-ResearchNode.ps1 -Id note.example -Tags a,b -Append
.\Rename-ResearchNode.ps1 -Id note.example -NewId note.better

# Thread items. The list lives in %TEMP%\Janet\thread-stack.json, as it always has.
.\Show-ThreadItems.ps1
.\Add-ThreadItem.ps1 -Topic 'the thing I am not doing now'
.\Set-ActiveThread.ps1 -Topic 'the thing'
.\Update-ThreadItem.ps1 -Topic 'the thing' -Notes '...' -AppendNotes
.\Complete-ThreadItem.ps1 -Topic 'the thing'

# Library research. Neither of these needs anything else in the repo.
.\Get-ApiDoc.ps1 -Package LiveChartsCore                       # what is in this API
.\Get-ApiDoc.ps1 -Package LiveChartsCore -Query 'tooltip formatter'
.\Get-AssemblyApi.ps1 -Assembly MyLib -SearchRoot .\bin\Release -Type 'Options$'

# Build and test, as JSON rather than scrollback. Emits contract 3, not 4: no status field.
.\Invoke-DotnetCheck.ps1 -Target .\App.slnx -NoTests
```

Every one takes `-?` for its own help, and the readers take `-Text` for a terminal view.
The graph path is `-Path` on `Get-Research.ps1` and `-GraphPath` on the three writers --
an inconsistency that was in the originals and is preserved rather than fixed.

## Where they live, and why it matters

`Get-Research.ps1` and the three writers resolve the catalog as **`..\research.json`** --
the parent of the directory they sit in. That is why this directory is at the repo root
rather than inside `scripts\`, and it is the whole reason they work here unmodified.

Copy them somewhere else and that assumption goes with them. Pass `-Path` or `-GraphPath`
explicitly if you do. The thread-item scripts have no such assumption: their list is under
`%TEMP%` and they only need `ThreadItems.Common.ps1` beside them, which is why it is here
too.

## What these are not

**They are not maintained.** They are frozen at the commit before each was shimmed --
`51c7930` for the research four, `4d83dbf` for the thread five, `fa7ae39` for the two
library-research scripts, `02ee7b6` for the build check. Fixes and new behaviour land in
`src\Janet.Core`, and nothing propagates back. Expect them to diverge. Three divergences
already exist, all fixed in the port and not here:

- `Get-ApiDoc.ps1 -Text` writes with `Write-Host`, so `$x = ... -Text` yields nothing
  without `6>&1`.
- `Get-AssemblyApi.ps1` pins every assembly it loads for the life of the process, so
  re-running after a rebuild answers from the first load, and it throws rather than
  returning a partial answer when a member's type cannot be resolved.
- `Invoke-DotnetCheck.ps1` emits **contract 3**: no `status` field, and no way to hand back
  a handle for a build that outlasts a caller's patience. It also stamps the baseline file
  from the same number as the envelope, so a future envelope bump would discard every
  baseline on disk. Its baselines are otherwise interchangeable with the port's.

**They do not have the write queue.** This is the one difference worth knowing before you
use them on anything you care about, and it is not cosmetic:

- The **research writers** here do a read, a splice, and a `WriteAllText`, with nothing in
  between. Two writers that overlap lose one update silently, and because `WriteAllText`
  truncates before it writes, a reader can catch a half-written file. The shimmed path
  fixed both -- one read per batch, and an atomic rename so a reader sees the whole old
  file or the whole new one.
- The **thread-item scripts** here serialise on a named mutex, which does exclude across
  processes. They are the safer half.

If you are one person at one keyboard, neither is likely to bite. If you have concurrent
agent sessions writing the catalog, use the CLI. `note.graph-write-queue` in the catalog
has the measurements.

**They are not the goldens.** The C# tests record what these scripts answered, but they
generate from git history at those commits, not from these files -- a golden generated
from a file the implementation can edit is not a golden.

## Checking they are what they say

`origins.json` records the source commit and a SHA-256 for each file, and

```powershell
..\scripts\Test-StandaloneScripts.ps1          # JSON envelope
..\scripts\Test-StandaloneScripts.ps1 -Text
```

verifies two things separately: that each file matches its recorded hash, and that each
recorded hash still matches the blob at the recorded commit. The second check is there
because a manifest you can update to match whatever the file now says verifies nothing.
Both compare content normalised to LF; in the repository the copies are byte-identical
to their blobs.
