<#
.SYNOPSIS
    Wires this checkout into the user profile: JanetBase at User scope, the scripts
    directory on the User PATH, and a junction per skill under ~\.claude\skills.

.DESCRIPTION
    Everything here is per-user machine state that a repo cannot carry, which is why it
    is a script you run once rather than something startup does. All three were found
    missing or stale on 2026-09-03: JanetBase was set at neither User nor Machine scope,
    ~\.claude\skills did not exist at all so neither skill enumerated in any session, and
    the startup script could only be run by full path.

    PATH is edited at User scope only, and read from User scope only -- reading $env:PATH
    would merge the Machine entries into the user's own copy, which is how a PATH gets
    quietly duplicated and then diverges. Stale entries pointing at another checkout's
    scripts directory are dropped, since that is what a machine move leaves behind.

    Idempotent by design -- run it again after a move, a reclone, or a profile reset and
    it reports what was already correct rather than redoing it.

    It never deletes a real directory. A skill path that exists as a genuine folder
    rather than a junction is reported as a problem and left alone, because the one thing
    worse than an unwired skill is a deleted one.

.PARAMETER Base
    The JanetHome checkout to wire in. Defaults to this script's parent, so the copy you
    ran is the copy you get.

.PARAMETER SkillRoot
    Where the harness looks for skills. Defaults to ~\.claude\skills.

.PARAMETER DryRun
    Report every action without taking any.

.PARAMETER Text
    Human-readable output instead of JSON.

.PARAMETER Pretty
    Indent the JSON.

.EXAMPLE
    .\Install-JanetEnvironment.ps1 -Text

.EXAMPLE
    .\Install-JanetEnvironment.ps1 -DryRun -Text
#>
[CmdletBinding()]
param(
    [string]$Base,
    [string]$SkillRoot,
    [switch]$DryRun,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolved from $PSScriptRoot rather than a hardcoded layout: house rule 6. The script
# that wires a checkout in should wire in the checkout it lives in.
if (-not $Base) { $Base = Split-Path -Parent $PSScriptRoot }
if (-not $SkillRoot) { $SkillRoot = Join-Path $env:USERPROFILE '.claude\skills' }

$actions = [System.Collections.Generic.List[object]]::new()
$problems = [System.Collections.Generic.List[string]]::new()

function Get-LinkTarget {
    # $null for anything that is not a reparse point, so a real directory is never
    # mistaken for a junction pointing at itself.
    param([string]$Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) { return $null }
    if (-not ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { return $null }
    if ($item.PSObject.Properties.Name -notcontains 'Target') { return $null }

    $target = $item.Target
    if ($null -eq $target) { return $null }
    # .Target is a collection on some hosts and a string on others.
    if ($target -is [string]) { return $target }
    return @($target)[0]
}

function Test-SamePath {
    param([string]$Left, [string]$Right)

    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) { return $false }
    return [string]::Equals($Left.TrimEnd('\'), $Right.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}

if (-not (Test-Path -LiteralPath $Base)) {
    $problems.Add("Base does not exist: $Base")
}

# --- 1. JanetBase at User scope -------------------------------------------------------
# Set for the USER, not the process: a skill invoked from another project resolves $janet
# through this, and a process-scope variable dies with the shell that set it.
$was = [Environment]::GetEnvironmentVariable('JanetBase', 'User')
$janetBaseChanged = $false

if (Test-SamePath $was $Base) {
    $janetBaseState = 'already-correct'
}
else {
    $janetBaseState = if ([string]::IsNullOrWhiteSpace($was)) { 'set' } else { 'repointed' }
    if (-not $DryRun) {
        [Environment]::SetEnvironmentVariable('JanetBase', $Base, 'User')
        $janetBaseChanged = $true
    }
}

# --- 2. The scripts directory on the User PATH ----------------------------------------
# Read from User scope, never from $env:PATH: $env:PATH is Machine + User merged, so
# writing it back to User duplicates every machine entry into the user's own copy, where
# it then stops tracking the machine's.
$scriptsDir = Join-Path $Base 'scripts'
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$pathEntries = @()
if (-not [string]::IsNullOrWhiteSpace($userPath)) {
    $pathEntries = @($userPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

$keptPath = [System.Collections.Generic.List[string]]::new()
$stalePath = [System.Collections.Generic.List[string]]::new()
$scriptsOnPath = $false

foreach ($entry in $pathEntries) {
    if (Test-SamePath $entry $scriptsDir) {
        if (-not $scriptsOnPath) { $keptPath.Add($entry) }  # collapse an accidental duplicate
        $scriptsOnPath = $true
        continue
    }
    # Another checkout's scripts directory -- including the old .github\scripts layout.
    # This is exactly what a machine move strands: never consulted, so never failing.
    if ($entry -match '(?i)JanetHome[\\/]+(\.github[\\/]+)?scripts[\\/]*$') {
        $stalePath.Add($entry)
        continue
    }
    $keptPath.Add($entry)
}

if ($scriptsOnPath -and $stalePath.Count -eq 0) {
    $pathState = 'already-correct'
}
else {
    $pathState = if ($scriptsOnPath) { 'repointed' } elseif ($stalePath.Count -gt 0) { 'repointed' } else { 'added' }
    if (-not $scriptsOnPath) { $keptPath.Add($scriptsDir) }
    if (-not $DryRun) {
        [Environment]::SetEnvironmentVariable('Path', ($keptPath -join ';'), 'User')
    }
}

# --- 3. A junction per skill ----------------------------------------------------------
$skillSource = Join-Path $Base 'skills'
$skillRootState = 'already-correct'

if (-not (Test-Path -LiteralPath $skillSource)) {
    $problems.Add("No skills directory to wire in: $skillSource")
}
else {
    if (-not (Test-Path -LiteralPath $SkillRoot)) {
        $skillRootState = 'created'
        if (-not $DryRun) { New-Item -ItemType Directory -Path $SkillRoot | Out-Null }
    }

    foreach ($dir in @(Get-ChildItem -LiteralPath $skillSource -Directory)) {
        $link = Join-Path $SkillRoot $dir.Name
        $existing = Get-LinkTarget -Path $link
        $isPresent = Test-Path -LiteralPath $link

        if ($isPresent -and $null -eq $existing) {
            # A real directory, not a link. Never delete it -- say so and move on.
            $problems.Add("$link exists as a real directory, not a junction. Move or remove it by hand, then re-run.")
            $state = 'blocked'
        }
        elseif (Test-SamePath $existing $dir.FullName) {
            $state = 'already-correct'
        }
        else {
            $state = if ($isPresent) { 'repointed' } else { 'created' }
            if (-not $DryRun) {
                # Delete the reparse point only. Directory::Delete with recursive:$false
                # cannot follow the junction into the target, which Remove-Item has been
                # known to do.
                if ($isPresent) { [System.IO.Directory]::Delete($link, $false) }
                New-Item -ItemType Junction -Path $link -Target $dir.FullName | Out-Null
            }
        }

        # A skill whose hardcoded fallback names a different checkout still resolves
        # through $env:JanetBase, so this is a warning rather than a failure.
        $manifest = Join-Path $dir.FullName 'SKILL.md'
        if ((Test-Path -LiteralPath $manifest) -and -not (Select-String -LiteralPath $manifest -SimpleMatch $Base -Quiet)) {
            $problems.Add("$($dir.Name)\SKILL.md does not mention $Base -- check its `$janet fallback.")
        }

        $actions.Add([ordered]@{
            name   = $dir.Name
            target = $dir.FullName
            state  = $state
        })
    }
}

$note = if ($DryRun) {
    'Dry run: nothing was changed.'
}
else {
    $verb = if ($janetBaseChanged) { 'JanetBase set at User scope' } else { 'JanetBase already correct' }
    "$verb; PATH $pathState; $($actions.Count) skill(s) wired. Open a NEW session for any of it to take effect -- the harness enumerates skills once at session start, and a running process never sees a User-scope variable it did not start with."
}

$result = [ordered]@{
    ok        = ($problems.Count -eq 0)
    base      = $Base
    skillRoot = $SkillRoot
    janetBase = [ordered]@{
        scope = 'User'
        was   = $was
        now   = $Base
        state = $janetBaseState
    }
    path      = [ordered]@{
        scope   = 'User'
        entry   = $scriptsDir
        state   = $pathState
        dropped = @($stalePath)
    }
    skillRootState = $skillRootState
    skills    = @($actions)
    problems  = @($problems)
    note      = $note
    error     = $null
}

if ($Text) {
    "Janet user environment -- $Base"
    "  JanetBase (User): $($result.janetBase.state)"
    "  PATH (User):      $pathState -> $scriptsDir"
    foreach ($s in $stalePath) { "    dropped stale:   $s" }
    "  Skill root:       $SkillRoot ($skillRootState)"
    foreach ($a in $actions) { "    {0,-18} {1}" -f $a.name, $a.state }
    if ($problems.Count -gt 0) {
        "  PROBLEMS:"
        foreach ($p in $problems) { "    - $p" }
    }
    "  $($result.note)"
}
else {
    $result | ConvertTo-Json -Depth 6 -Compress:(-not $Pretty)
}

if ($problems.Count -gt 0) { exit 1 }
