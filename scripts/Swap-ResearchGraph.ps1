<#
.SYNOPSIS
    Cuts the research catalog over from the PowerShell writers to the C# port, once.

.DESCRIPTION
    While the port is being built, two implementations write two files: the PowerShell
    scripts write research.json, and Janet.Core writes research.candidate.json. They
    diverge as soon as an ordinary session adds a node, and that divergence is expected
    -- integrating it is this script's last step, not a reason to refuse.

    The transition is staged and then atomic, never gradual:

      1. preserve   research.json          -> research.previous.json   (rename, kept)
      2. swap       research.candidate.json -> research.json           (rename)
      3. integrate  the nodes the live graph gained while the port was built, replayed
                    forward through the NEW writer

    Step 3 is a three-way comparison against research.candidate.base.json, the common
    ancestor frozen at seed time. A hash can only say THAT the live graph moved; knowing
    WHICH nodes moved needs the ancestor. Where a node changed on both sides, the
    preserved side wins -- those are nodes real sessions authored, and the candidate's
    copy is at best a stale seed -- and every such conflict is reported.

    Integration deliberately runs through the new writer rather than the old one. It is
    that code's first run against real accumulated content, which is the point: if it
    mangles anything, research.previous.json is sitting beside it, unmodified.

.PARAMETER Approve
    Actually perform the swap. Without it the script validates, prints the plan, and
    stops -- which is where the first run is supposed to end.

.PARAMETER AllowRemovals
    Permit the integration to proceed when the live graph has nodes the candidate does
    not, and the base says they were deleted rather than never added.

.PARAMETER IgnoreRecentActivity
    Proceed even though the research trace shows a session queried the catalog moments
    ago. The trace is a hint, not a lock; this is the escape hatch when you know better.

.PARAMETER JanetCommand
    The janet CLI to integrate through. Defaults to 'janet' on PATH. Required rather
    than optional: cutting over to an implementation you cannot run is not a cutover.

.NOTES
    [SAFETY] Run this manually, from a plain shell, with every agent session closed.

    Its precondition is that no other writer is running, and a session cannot quiesce
    itself -- an agent running this from inside one of two concurrent sessions is a
    writer trying to prove there are no writers. The checks below catch the ordinary
    case; your own shutdown is what actually establishes the precondition.
#>
[CmdletBinding()]
param(
    [switch]$Approve,
    [switch]$AllowRemovals,
    [switch]$IgnoreRecentActivity,
    [string]$Base,
    [string]$JanetCommand = 'janet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Base) { $Base = Split-Path $PSScriptRoot -Parent }

$livePath = Join-Path $Base 'research.json'
$candidatePath = Join-Path $Base 'research.candidate.json'
$basePath = Join-Path $Base 'research.candidate.base.json'
$previousPath = Join-Path $Base 'research.previous.json'
$seedPath = Join-Path $Base 'research.candidate.seed.json'

# ---- Helpers ---------------------------------------------------------------

function Read-Graph {
    param([string]$Path, [string]$Label)

    if (-not (Test-Path -LiteralPath $Path)) { throw "$Label not found: $Path" }

    try { $graph = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "$Label is not valid JSON ($Path): $($_.Exception.Message)" }

    if ($graph.PSObject.Properties.Name -notcontains 'nodes') { throw "$Label has no 'nodes' array: $Path" }

    $nodes = @($graph.nodes)
    if ($nodes.Count -eq 0) { throw "$Label has no nodes: $Path" }

    return $nodes
}

function Get-NodeMap {
    param($Nodes)

    $map = @{}
    foreach ($node in $Nodes) { $map[$node.id] = $node }
    return $map
}

# Compared as serialised JSON so a field reordering counts as a change. Conservative on
# purpose: over-reporting a change costs a line of output, under-reporting loses a node.
function Get-NodeFingerprint {
    param($Node)
    return ($Node | ConvertTo-Json -Depth 10 -Compress)
}

function Get-Delta {
    param($BaseMap, $CurrentMap)

    $added = @()
    $changed = @()
    $removed = @()

    foreach ($id in $CurrentMap.Keys) {
        if (-not $BaseMap.ContainsKey($id)) { $added += $id; continue }
        if ((Get-NodeFingerprint $CurrentMap[$id]) -ne (Get-NodeFingerprint $BaseMap[$id])) { $changed += $id }
    }

    foreach ($id in $BaseMap.Keys) {
        if (-not $CurrentMap.ContainsKey($id)) { $removed += $id }
    }

    return @{
        added   = @($added | Sort-Object)
        changed = @($changed | Sort-Object)
        removed = @($removed | Sort-Object)
    }
}

function Assert-Quiesced {
    $problems = @()

    $running = @(Get-Process -Name 'janet-mcp' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $problems += "janet-mcp is running (pid $($running.Id -join ', ')). Stop it: it holds the graph this swap renames."
    }

    foreach ($path in @($livePath, $candidatePath)) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        try {
            $stream = [System.IO.File]::Open($path, 'Open', 'ReadWrite', 'None')
            $stream.Dispose()
        }
        catch {
            $problems += "File is locked by another process: $path"
        }
    }

    if (-not $IgnoreRecentActivity) {
        $tracePath = Join-Path ([System.IO.Path]::GetTempPath()) 'janet-research-trace.json'
        if (Test-Path -LiteralPath $tracePath) {
            $age = (Get-Date) - (Get-Item -LiteralPath $tracePath).LastWriteTime
            if ($age.TotalMinutes -lt 5) {
                $problems += ("A session queried the catalog {0:N1} minutes ago (research trace). " -f $age.TotalMinutes) +
                    'Close every session, or pass -IgnoreRecentActivity if you know better.'
            }
        }
    }

    return , @($problems)
}

function Resolve-Janet {
    $command = Get-Command $JanetCommand -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "janet CLI not found ('$JanetCommand'). Integration replays the live graph's " +
              'changes through the new writer, so the cutover cannot run without it. ' +
              'Install it, or pass -JanetCommand with a full path.'
    }
    return $command.Source
}

function Invoke-Janet {
    param([string]$Exe, [string[]]$Arguments)

    $output = & $Exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "janet $($Arguments -join ' ') failed: $output" }
    return $output
}

# ---- 0. Verify quiesce -----------------------------------------------------

$blockers = Assert-Quiesced
if ($blockers.Count -gt 0) {
    throw "Refusing to swap -- something else may be writing:$([Environment]::NewLine)" +
        (($blockers | ForEach-Object { "  - $_" }) -join [Environment]::NewLine)
}

# ---- 1. Parse --------------------------------------------------------------

if (Test-Path -LiteralPath $previousPath) {
    throw "$previousPath already exists. A previous swap has run; move or delete it before swapping again."
}

$liveNodes = Read-Graph $livePath 'Live graph'
$candidateNodes = Read-Graph $candidatePath 'Candidate graph'
$baseNodes = Read-Graph $basePath 'Base snapshot'

$liveMap = Get-NodeMap $liveNodes
$candidateMap = Get-NodeMap $candidateNodes
$baseMap = Get-NodeMap $baseNodes

# ---- 2. Delta --------------------------------------------------------------

$liveDelta = Get-Delta $baseMap $liveMap
$candidateDelta = Get-Delta $baseMap $candidateMap

$conflicts = @($liveDelta.changed | Where-Object { $candidateDelta.changed -contains $_ })

# ---- 3. Validate the candidate ---------------------------------------------

$problems = @()
$warnings = @()

$sandbox = @($candidateMap.Keys | Where-Object { $_ -like 'sandbox.*' } | Sort-Object)
if ($sandbox.Count -gt 0) {
    $problems += "candidate still holds $($sandbox.Count) sandbox node(s): $($sandbox -join ', '). " +
        'These were written while testing and must not become catalog entries.'
}

$duplicateIds = @($candidateNodes | Group-Object id | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
if ($duplicateIds.Count -gt 0) { $problems += "candidate has duplicate ids: $($duplicateIds -join ', ')" }

foreach ($node in $candidateNodes) {
    if ($node.id -notmatch '^[a-z]+\.[a-z0-9-]+$') { $warnings += "id does not match convention: $($node.id)" }

    # A warning, not a blocker. Add-ResearchNode.ps1 warns on a missing link target and adds
    # the node anyway, so the catalog legitimately carries dangling links -- there is one in
    # it today. A swap stricter than the writer that produced the data is a swap that can
    # never run.
    $links = if ($node.PSObject.Properties.Name -contains 'links' -and $null -ne $node.links) { @($node.links) } else { @() }
    foreach ($link in $links) {
        if (-not $candidateMap.ContainsKey($link) -and -not $liveMap.ContainsKey($link)) {
            $warnings += "dangling link: $($node.id) -> $link"
        }
    }

    $nodePath = Join-Path $Base $node.path
    if (-not (Test-Path -LiteralPath $nodePath)) { $warnings += "path does not exist: $($node.path) (on $($node.id))" }
}

# A node present in base but gone from the candidate was dropped by the port, not by a
# session. That is data loss unless it was deliberate.
$candidateRemovals = @($candidateDelta.removed)
if ($candidateRemovals.Count -gt 0 -and -not $AllowRemovals) {
    $problems += "candidate is missing $($candidateRemovals.Count) node(s) present at seed time: " +
        "$($candidateRemovals -join ', '). Pass -AllowRemovals if that is intended."
}

# ---- 4. Report -------------------------------------------------------------

# Wrapped: with nothing to integrate the pipeline yields $null, and $null.Count is a
# terminating error under StrictMode (house rule 1). The empty case is the common one on a
# dry run, so this is the path that runs first, not an edge.
$integrate = @(@($liveDelta.added) + @($liveDelta.changed) | Sort-Object -Unique)

Write-Host ''
Write-Host 'Cutover plan' -ForegroundColor Cyan
Write-Host "  base      $($baseNodes.Count) nodes  $basePath"
Write-Host "  live      $($liveNodes.Count) nodes  $livePath"
Write-Host "  candidate $($candidateNodes.Count) nodes  $candidatePath"
Write-Host ''
Write-Host "  live gained since seed   : $($liveDelta.added.Count) added, $($liveDelta.changed.Count) changed, $($liveDelta.removed.Count) removed"
Write-Host "  candidate gained         : $($candidateDelta.added.Count) added, $($candidateDelta.changed.Count) changed, $($candidateDelta.removed.Count) removed"
Write-Host "  to integrate forward     : $($integrate.Count)"
foreach ($id in $integrate) { Write-Host "      $id" -ForegroundColor DarkGray }

if ($conflicts.Count -gt 0) {
    Write-Host ''
    Write-Host "  CONFLICTS -- changed on both sides; the preserved (live) version wins:" -ForegroundColor Yellow
    foreach ($id in $conflicts) { Write-Host "      $id" -ForegroundColor Yellow }
}

if ($liveDelta.removed.Count -gt 0) {
    Write-Host ''
    Write-Host '  Removed from the live graph since seed. NOT replayed -- removals are not' -ForegroundColor Yellow
    Write-Host '  integrated automatically; delete them by hand afterwards if intended:' -ForegroundColor Yellow
    foreach ($id in $liveDelta.removed) { Write-Host "      $id" -ForegroundColor Yellow }
}

# Path warnings are per-node and can run to dozens; everything else is rare and specific.
# Printed together, the volume buries the conflict list -- which is the one thing here that
# needs a decision. Capped, with the count of what was held back, never silently dropped.
$pathWarnings = @($warnings | Where-Object { $_ -like 'path does not exist:*' })
$otherWarnings = @($warnings | Where-Object { $_ -notlike 'path does not exist:*' })

foreach ($w in $otherWarnings) { Write-Host "  ! $w" -ForegroundColor Yellow }

if ($pathWarnings.Count -gt 0) {
    Write-Host ''
    Write-Host "  $($pathWarnings.Count) node(s) point at a path that does not exist:" -ForegroundColor Yellow
    foreach ($w in @($pathWarnings | Select-Object -First 5)) { Write-Host "      $w" -ForegroundColor DarkGray }
    if ($pathWarnings.Count -gt 5) {
        Write-Host "      ... and $($pathWarnings.Count - 5) more (-Verbose for all)" -ForegroundColor DarkGray
        foreach ($w in @($pathWarnings | Select-Object -Skip 5)) { Write-Verbose $w }
    }
}

if ($problems.Count -gt 0) {
    Write-Host ''
    throw "Candidate failed validation:$([Environment]::NewLine)" +
        (($problems | ForEach-Object { "  - $_" }) -join [Environment]::NewLine)
}

$janet = Resolve-Janet
Write-Host ''
Write-Host "  integrating through      : $janet"

if (-not $Approve) {
    Write-Host ''
    Write-Host 'Dry run. Nothing was changed. Re-run with -Approve to perform the swap.' -ForegroundColor Cyan
    Write-Host ''
    return
}

# ---- 5/6. Preserve and swap, by rename -------------------------------------

Write-Host ''
Move-Item -LiteralPath $livePath -Destination $previousPath
Write-Host "  preserved -> $previousPath" -ForegroundColor Green

try {
    Move-Item -LiteralPath $candidatePath -Destination $livePath
    Write-Host "  swapped   -> $livePath" -ForegroundColor Green
}
catch {
    Move-Item -LiteralPath $previousPath -Destination $livePath
    throw "Swap failed and was reversed; $livePath is unchanged. $($_.Exception.Message)"
}

# ---- 7. Integrate the live-side delta forward ------------------------------

$integrated = @()
$failed = @()

foreach ($id in $integrate) {
    $node = $liveMap[$id]
    $json = $node | ConvertTo-Json -Depth 10 -Compress
    $verb = if ($candidateMap.ContainsKey($id)) { 'update' } else { 'add' }

    try {
        $null = Invoke-Janet $janet @('research', $verb, '--graph', $livePath, '--json', $json)
        $integrated += $id
        # "attempted", not "integrated": an exit code is the writer's claim, not proof the
        # node landed. A stand-in that exits 0 without doing anything reported success here
        # while writing nothing. The re-read below is what actually decides, so this line
        # must not out-claim it.
        Write-Host "  integrating ($verb) $id" -ForegroundColor DarkGray
    }
    catch {
        $failed += $id
        Write-Warning "integration failed for '$id': $($_.Exception.Message)"
    }
}

# ---- 8. Re-validate, or reverse --------------------------------------------

function Restore-Previous {
    param([string]$Reason)

    if (Test-Path -LiteralPath $livePath) { Move-Item -LiteralPath $livePath -Destination $candidatePath -Force }
    Move-Item -LiteralPath $previousPath -Destination $livePath
    throw "$Reason The swap was reversed: $livePath is the file it was, and the candidate is back at $candidatePath."
}

try { $final = Read-Graph $livePath 'Swapped graph' }
catch { Restore-Previous "The swapped graph does not parse. $($_.Exception.Message)" }

$finalMap = Get-NodeMap $final
$missing = @($integrate | Where-Object { -not $finalMap.ContainsKey($_) })
if ($missing.Count -gt 0) { Restore-Previous "Nodes missing after integration: $($missing -join ', ')." }
if ($failed.Count -gt 0) { Restore-Previous "Integration failed for: $($failed -join ', ')." }

# The base and seed describe a port that has now landed; leaving them in place would
# invite a second swap against a stale ancestor.
foreach ($stale in @($basePath, $seedPath)) {
    if (Test-Path -LiteralPath $stale) { Move-Item -LiteralPath $stale -Destination "$stale.done" -Force }
}

Write-Host ''
Write-Host "Swapped. $($final.Count) nodes, $($integrated.Count) integrated forward." -ForegroundColor Green
Write-Host "  preserved: $previousPath  (left on disk deliberately -- this is the rollback path)" -ForegroundColor DarkGray
Write-Host '  to reverse: rename it back over research.json and revert the shim commit.' -ForegroundColor DarkGray
Write-Host ''
