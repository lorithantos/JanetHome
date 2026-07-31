<#
.SYNOPSIS
    Executes startup-manifest.json: sets $env:JanetBase, verifies every entry
    resolves, runs the startup commands, and emits a session brief.

.DESCRIPTION
    The manifest-driven startup from DESIGN-NOTES section 1.  A prose context file
    drifts silently -- it stays syntactically valid while going factually wrong.
    This is checkable instead: every 'read' path and every 'run' command either
    resolves or startup fails, per the manifest's onMissing setting.

    Validation happens before anything executes, so a broken manifest reports all
    its problems at once rather than failing halfway through with side effects on
    disk.

.PARAMETER ManifestPath
    Manifest to execute.  Defaults to startup-manifest.json in the repo root
    (resolved from this script's own location, so there is no bootstrap
    dependency on $env:JanetBase already being set).

.PARAMETER Text
    Formatted output for reading at a terminal.  The default is JSON: the brief's
    consumer is the session model, and structure beats column alignment for that
    reader.  Captured command output lands in the 'captured' property either way.

.PARAMETER Pretty
    Indent the JSON. For debugging by eye.

.PARAMETER IncludeContent
    Include the full text of each 'read' entry in the output.  Off by default:
    the brief lists paths and reasons, and the reader decides what to open.
    Progressive disclosure (section 2) applies to startup too.

.PARAMETER SkipRun
    Validate and report without executing the 'run' entries.  Use to lint the
    manifest.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-JanetStartup.ps1"

.EXAMPLE
    & "D:\Repos\JanetHome\scripts\Invoke-JanetStartup.ps1" -Json | ConvertFrom-Json

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-JanetStartup.ps1" -SkipRun
    Lints the manifest: every path resolves, every command exists. No execution.
#>
[CmdletBinding()]
param(
    [string]$ManifestPath,
    [switch]$Text,
    [switch]$Pretty,
    [switch]$IncludeContent,
    [switch]$SkipRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $ManifestPath) { $ManifestPath = Join-Path $repoRoot 'startup-manifest.json' }

if (-not (Test-Path $ManifestPath)) {
    throw "Startup manifest not found: $ManifestPath"
}

try {
    $manifest = Get-Content $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "Startup manifest is not valid JSON ($ManifestPath): $($_.Exception.Message)"
}

# Set the framework variable before validation -- several scripts resolve paths
# through it, including ones the manifest is about to run.
$env:JanetBase = $repoRoot

function Get-Prop {
    # ConvertFrom-Json returns PSCustomObject; reading an absent property is a
    # hard error under StrictMode.  Optional manifest fields ('why',
    # 'captureAs') must not blow up before validation gets to report the real
    # problem, so every read goes through here.
    param($Object, [string]$Name, $Default = $null)
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name -and $null -ne $Object.$Name) {
        return $Object.$Name
    }
    return $Default
}

function Get-ManifestSection {
    # ASSIGN THE RESULT -- do not call this inline inside @(...).
    # The unary comma is load-bearing: 'return @()' unrolls the empty array to
    # nothing, so the caller gets $null and a later .Count throws under
    # StrictMode.  The wrap survives assignment (which unrolls one layer) but
    # NOT an inline @(...) call, which would hand you a 1-element array holding
    # the real one.
    param($Object, [string]$Name)
    $value = Get-Prop $Object $Name
    if ($null -eq $value) { return ,@() }
    return ,@($value)
}

$readEntries = Get-ManifestSection $manifest 'read'
$runEntries  = Get-ManifestSection $manifest 'run'
$rules       = Get-ManifestSection $manifest 'rules'
$retrieval   = Get-Prop $manifest 'retrieval'

$onMissing = Get-Prop $manifest 'onMissing' 'fail'

# ---- Validation pass: resolve everything before doing anything -------------

$problems = @()
$reads = @()
foreach ($entry in $readEntries) {
    $path = Get-Prop $entry 'path'
    if (-not $path) { $problems += "read: entry with no 'path'"; continue }
    $full = Join-Path $repoRoot $path
    $exists = Test-Path $full -PathType Leaf
    if (-not $exists) { $problems += "read: missing file '$path'" }
    $reads += [PSCustomObject]@{
        path   = $path
        full   = $full
        why    = (Get-Prop $entry 'why' '')
        exists = $exists
    }
}

$runs = @()
foreach ($entry in $runEntries) {
    $cmd = Get-Prop $entry 'cmd'
    if (-not $cmd) { $problems += "run: entry with no 'cmd'"; continue }
    $full = Join-Path $repoRoot $cmd
    $exists = Test-Path $full -PathType Leaf
    if (-not $exists) { $problems += "run: missing command '$cmd'" }
    $runs += [PSCustomObject]@{
        cmd       = $cmd
        full      = $full
        why       = (Get-Prop $entry 'why' '')
        captureAs = (Get-Prop $entry 'captureAs' '')
        exists    = $exists
    }
}

# The retrieval pointer replaces an eagerly-loaded inventory, so it has to be
# held to the same contract: a dead pointer is worse than no pointer, because
# the session believes it has a way to look things up.
if ($null -ne $retrieval) {
    foreach ($field in @('graph', 'via')) {
        $value = Get-Prop $retrieval $field
        if (-not $value) { $problems += "retrieval: missing '$field'"; continue }
        if (-not (Test-Path (Join-Path $repoRoot $value) -PathType Leaf)) {
            $problems += "retrieval: '$field' does not resolve -- '$value'"
        }
    }
}

if ($problems.Count -gt 0) {
    $detail = ($problems | ForEach-Object { "  - $_" }) -join [Environment]::NewLine
    $summary = "Startup manifest has $($problems.Count) unresolved entr$(if ($problems.Count -eq 1) {'y'} else {'ies'}):"
    if ($onMissing -eq 'fail') {
        throw "$summary$([Environment]::NewLine)$detail"
    }
    Write-Warning "$summary$([Environment]::NewLine)$detail"
}

# ---- Execution pass --------------------------------------------------------

$captured = [ordered]@{}
$runResults = @()

foreach ($run in $runs) {
    if ($SkipRun -or -not $run.exists) {
        $runResults += [PSCustomObject]@{
            cmd = $run.cmd; captureAs = $run.captureAs
            status = $(if ($SkipRun) { 'skipped' } else { 'missing' })
            output = ''
        }
        continue
    }

    # Startup must not be able to hang the session on one bad script, so failures
    # are captured and reported rather than thrown (section 8).
    try {
        $output = (& $run.full 6>&1 | Out-String).TrimEnd()
        $status = 'ok'
    }
    catch {
        $output = $_.Exception.Message
        $status = 'error'
    }

    if ($run.captureAs) { $captured[$run.captureAs] = $output }
    $runResults += [PSCustomObject]@{
        cmd = $run.cmd; captureAs = $run.captureAs
        status = $status; output = $output
    }
}

# ---- Brief -----------------------------------------------------------------

if (-not $Text) {
    $readOut = $reads | ForEach-Object {
        $o = [ordered]@{ path = $_.path; why = $_.why; exists = $_.exists }
        if ($IncludeContent -and $_.exists) { $o.content = (Get-Content $_.full -Raw -Encoding UTF8) }
        [PSCustomObject]$o
    }
    $brief = [PSCustomObject]@{
        janetBase = $repoRoot
        manifest  = $ManifestPath
        read      = @($readOut)
        run       = @($runResults)
        captured  = [PSCustomObject]$captured
        retrieval = $retrieval
        rules     = @($rules)
        problems  = @($problems)
    }
    if ($Pretty) { $brief | ConvertTo-Json -Depth 6 }
    else { $brief | ConvertTo-Json -Depth 6 -Compress }
    return
}

Write-Host ''
Write-Host "Janet startup -- $repoRoot" -ForegroundColor Cyan
Write-Host ("=" * 60) -ForegroundColor DarkGray
Write-Host "`$env:JanetBase set to $repoRoot"
Write-Host ''

Write-Host 'READ THESE' -ForegroundColor Cyan
foreach ($r in $reads) {
    $mark = if ($r.exists) { ' ' } else { '!' }
    Write-Host "$mark $($r.path)"
    if ($r.why) { Write-Host "    $($r.why)" -ForegroundColor DarkGray }
    if ($IncludeContent -and $r.exists) {
        Write-Host ''
        Write-Host (Get-Content $r.full -Raw -Encoding UTF8)
        Write-Host ''
    }
}
Write-Host ''

foreach ($res in $runResults) {
    $label = if ($res.captureAs) { $res.captureAs } else { $res.cmd }
    Write-Host $label.ToUpperInvariant() -ForegroundColor Cyan
    switch ($res.status) {
        'ok'      { if ($res.output) { Write-Host $res.output } else { Write-Host '(no output)' -ForegroundColor DarkGray } }
        'skipped' { Write-Host '(skipped)' -ForegroundColor DarkGray }
        'missing' { Write-Host "(missing: $($res.cmd))" -ForegroundColor Yellow }
        'error'   { Write-Host "(failed: $($res.output))" -ForegroundColor Yellow }
    }
    Write-Host ''
}

if ($null -ne $retrieval) {
    Write-Host 'RETRIEVAL' -ForegroundColor Cyan
    Write-Host "  The tool/note inventory is not loaded. Query $(Get-Prop $retrieval 'graph') via:"
    Write-Host "    $(Get-Prop $retrieval 'via')"
    $usage = Get-ManifestSection $retrieval 'usage'
    foreach ($u in $usage) { Write-Host "      $u" -ForegroundColor DarkGray }
    $add = Get-Prop $retrieval 'add'
    if ($add) {
        Write-Host "  Add:      $add" -ForegroundColor DarkGray
    }
    $update = Get-Prop $retrieval 'update'
    if ($update) {
        Write-Host "  Update:   $update" -ForegroundColor DarkGray
    }
    $envelope = Get-Prop $retrieval 'envelope'
    if ($envelope) {
        Write-Host "  Shape:    $envelope" -ForegroundColor DarkGray
    }
    $caveats = Get-Prop $retrieval 'caveats'
    if ($caveats) {
        Write-Host "  Caveats:  $caveats" -ForegroundColor DarkGray
    }
    $fallback = Get-Prop $retrieval 'fallback'
    if ($fallback) { Write-Host "  Fallback: $fallback" -ForegroundColor DarkGray }
    Write-Host ''
}

if ($rules.Count -gt 0) {
    Write-Host 'OPERATING RULES' -ForegroundColor Cyan
    foreach ($rule in $rules) { Write-Host "  - $rule" }
    Write-Host ''
}
