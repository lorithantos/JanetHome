<#
.SYNOPSIS
    Applies a batch of surgical code edits from a JSON plan file.

.DESCRIPTION
    Reads a JSON plan that describes exact file operations (delete, removeLines,
    removeParameter, removeProperty, removeArgument, replace, insertAfter) and
    executes them in order. Optionally runs build and test commands afterward.

    Designed for agent-driven workflows: an agent creates the plan (fast JSON
    output), then this script executes it deterministically.

.PARAMETER PlanFile
    Path to the JSON plan file describing the operations.

.PARAMETER DryRun
    Show what would change without modifying files.

.PARAMETER NoBuild
    Skip build verification after applying operations.

.PARAMETER NoTest
    Skip test verification after applying operations.

.EXAMPLE
    & "$env:JanetBase\.github\scripts\Invoke-SurgicalEdit.ps1" -PlanFile plan.json -DryRun

.EXAMPLE
    & "$env:JanetBase\.github\scripts\Invoke-SurgicalEdit.ps1" -PlanFile plan.json -NoBuild -NoTest
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PlanFile,
    [switch]$DryRun,
    [switch]$NoBuild,
    [switch]$NoTest
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

# --- Helpers ---

function Resolve-OpPath {
    param([string]$RepoRoot, [string]$RelPath)
    Join-Path $RepoRoot $RelPath
}

function Read-FileLines {
    param([string]$FilePath)
    [System.IO.File]::ReadAllLines($FilePath)
}

function Write-FileLines {
    param([string]$FilePath, [string[]]$Lines)
    $text = ($Lines -join "`r`n") + "`r`n"
    [System.IO.File]::WriteAllText($FilePath, $text, $script:utf8NoBom)
}

function Write-OpResult {
    param([string]$Op, [string]$File, [string]$Detail)
    $rel = if ($script:repoRoot) { $File.Replace($script:repoRoot, '').TrimStart('\', '/') } else { $File }
    Write-Host "  [$Op] $rel -- $Detail"
}

# --- Operation handlers ---

function Invoke-DeleteOp {
    param($op)
    $full = Resolve-OpPath $script:repoRoot $op.path
    if ($DryRun) { Write-OpResult 'delete' $full 'would delete'; return 1 }
    if (-not (Test-Path $full)) {
        Write-Warning "File not found for delete (skipping): $($op.path)"
        return 0
    }
    Remove-Item $full -Force
    Write-OpResult 'delete' $full 'deleted'
    return 1
}

function Invoke-RemoveLinesOp {
    param($op)
    $full = Resolve-OpPath $script:repoRoot $op.path
    $lines = Read-FileLines $full
    $patterns = @($op.patterns)
    $removed = 0
    $kept = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $lines) {
        $match = $false
        foreach ($p in $patterns) {
            if ($line -match $p) { $match = $true; break }
        }
        if ($match) { $removed++ } else { $kept.Add($line) }
    }
    if ($removed -eq 0) { Write-Warning "No lines matched patterns in $($op.path)" }
    if ($DryRun) { Write-OpResult 'removeLines' $full "would remove $removed lines"; return 1 }
    Write-FileLines $full $kept.ToArray()
    Write-OpResult 'removeLines' $full "removed $removed lines"
    return 1
}

function Invoke-RemoveParameterOp {
    param($op)
    $full = Resolve-OpPath $script:repoRoot $op.path
    $content = [System.IO.File]::ReadAllText($full)
    $paramEscaped = [regex]::Escape($op.parameter)
    $changed = $false
    # Remove parameter with leading comma+whitespace
    $pattern1 = ',\s*' + $paramEscaped
    # Remove parameter with trailing comma+whitespace
    $pattern2 = $paramEscaped + '\s*,\s*'
    # Remove parameter as sole parameter (no comma)
    $pattern3 = $paramEscaped
    foreach ($method in $op.methods) {
        $methodEsc = [regex]::Escape($method)
        # Try leading comma first, then trailing, then sole
        foreach ($pat in @($pattern1, $pattern2, $pattern3)) {
            $before = $content
            $content = [regex]::Replace($content, $pat, '')
            if ($content -ne $before) { $changed = $true; break }
        }
    }
    if (-not $changed) { Write-Warning "Parameter '$($op.parameter)' not found in $($op.path)" }
    if ($DryRun) { Write-OpResult 'removeParameter' $full "would remove param from $($op.methods -join ', ')"; return 1 }
    [System.IO.File]::WriteAllText($full, $content, $script:utf8NoBom)
    Write-OpResult 'removeParameter' $full "removed param from $($op.methods -join ', ')"
    return 1
}

function Invoke-RemovePropertyOp {
    param($op)
    $full = Resolve-OpPath $script:repoRoot $op.path
    $lines = Read-FileLines $full
    $propEscaped = [regex]::Escape($op.property)
    $removed = 0
    $kept = [System.Collections.Generic.List[string]]::new()
    # Extract just the property name (last word) for assignment matching
    $propName = ($op.property -split '\s+')[-1]
    $propNameEsc = [regex]::Escape($propName)
    foreach ($line in $lines) {
        if ($line -match $propEscaped -or $line -match "^\s*$propNameEsc\s*=") {
            $removed++
        } else {
            $kept.Add($line)
        }
    }
    if ($removed -eq 0) { Write-Warning "Property '$($op.property)' not found in $($op.path)" }
    if ($DryRun) { Write-OpResult 'removeProperty' $full "would remove $removed lines"; return 1 }
    Write-FileLines $full $kept.ToArray()
    Write-OpResult 'removeProperty' $full "removed $removed lines"
    return 1
}

function Invoke-RemoveArgumentOp {
    param($op)
    $full = Resolve-OpPath $script:repoRoot $op.path
    $content = [System.IO.File]::ReadAllText($full)
    $argEscaped = [regex]::Escape($op.argument)
    # Match the named argument and its value up to next comma or closing paren
    # Handle trailing comma+whitespace or leading comma+whitespace
    $patterns = @(
        ",\s*$argEscaped\s*[^,)]*",
        "$argEscaped\s*[^,)]*\s*,\s*",
        "$argEscaped\s*[^,)]*"
    )
    $changed = $false
    foreach ($pat in $patterns) {
        $before = $content
        $content = [regex]::Replace($content, $pat, '')
        if ($content -ne $before) { $changed = $true; break }
    }
    if (-not $changed) { Write-Warning "Argument '$($op.argument)' not found in $($op.path)" }
    if ($DryRun) { Write-OpResult 'removeArgument' $full 'would remove argument'; return 1 }
    [System.IO.File]::WriteAllText($full, $content, $script:utf8NoBom)
    Write-OpResult 'removeArgument' $full 'removed argument'
    return 1
}

function Invoke-ReplaceOp {
    param($op)
    $full = Resolve-OpPath $script:repoRoot $op.path
    $content = [System.IO.File]::ReadAllText($full)
    if (-not $content.Contains($op.old)) {
        Write-Warning "Replace target not found in $($op.path)"
    }
    if ($DryRun) { Write-OpResult 'replace' $full 'would replace'; return 1 }
    $content = $content.Replace($op.old, $op.new)
    [System.IO.File]::WriteAllText($full, $content, $script:utf8NoBom)
    Write-OpResult 'replace' $full 'replaced'
    return 1
}

function Invoke-InsertAfterOp {
    param($op)
    $full = Resolve-OpPath $script:repoRoot $op.path
    $lines = Read-FileLines $full
    $result = [System.Collections.Generic.List[string]]::new()
    $inserted = $false
    foreach ($line in $lines) {
        $result.Add($line)
        if (-not $inserted -and $line -match [regex]::Escape($op.after)) {
            $result.Add($op.insert)
            $inserted = $true
        }
    }
    if (-not $inserted) { Write-Warning "Insert-after pattern '$($op.after)' not found in $($op.path)" }
    if ($DryRun) { Write-OpResult 'insertAfter' $full $(if ($inserted) { 'would insert' } else { 'pattern not found' }); return 1 }
    Write-FileLines $full $result.ToArray()
    Write-OpResult 'insertAfter' $full $(if ($inserted) { 'inserted' } else { 'skipped (no match)' })
    return 1
}

# --- Main ---

if (-not (Test-Path $PlanFile)) {
    Write-Error "Plan file not found: $PlanFile"
    return
}

$plan = Get-Content $PlanFile -Raw | ConvertFrom-Json
$script:repoRoot = $plan.repoRoot

Write-Host "`nPlan: $($plan.description)"
Write-Host "Root: $script:repoRoot"
if ($DryRun) { Write-Host '** DRY RUN **' }
Write-Host ''

# Validate paths (skip delete ops -- missing file is OK for those)
foreach ($op in $plan.operations) {
    if ($op.type -eq 'delete') { continue }
    $full = Resolve-OpPath $script:repoRoot $op.path
    if (-not (Test-Path $full)) {
        Write-Error "File not found: $full (operation: $($op.type))"
        return
    }
}

# Execute
$opsApplied = 0
$filesChanged = [System.Collections.Generic.HashSet[string]]::new()

foreach ($op in $plan.operations) {
    switch ($op.type) {
        'delete'          { $r = Invoke-DeleteOp $op }
        'removeLines'     { $r = Invoke-RemoveLinesOp $op }
        'removeParameter' { $r = Invoke-RemoveParameterOp $op }
        'removeProperty'  { $r = Invoke-RemovePropertyOp $op }
        'removeArgument'  { $r = Invoke-RemoveArgumentOp $op }
        'replace'         { $r = Invoke-ReplaceOp $op }
        'insertAfter'     { $r = Invoke-InsertAfterOp $op }
        default           { Write-Warning "Unknown operation type: $($op.type)"; continue }
    }
    $opsApplied += $r
    [void]$filesChanged.Add($op.path)
}

Write-Host "`n--- Summary ---"
Write-Host "Operations applied: $opsApplied"
Write-Host "Files affected: $($filesChanged.Count)"

if ($DryRun) {
    Write-Host 'Dry run complete -- no files were modified.'
    return
}

# Build
$buildOk = $true
if (-not $NoBuild -and $plan.buildCommand) {
    Write-Host "`nRunning build: $($plan.buildCommand)"
    Push-Location $script:repoRoot
    try {
        Invoke-Expression $plan.buildCommand
        if ($LASTEXITCODE -ne 0) { $buildOk = $false; Write-Error 'Build FAILED.' }
        else { Write-Host 'Build: PASSED' }
    } finally { Pop-Location }
} else {
    Write-Host 'Build: skipped'
}

# Test
if ($buildOk -and -not $NoTest -and $plan.testCommand) {
    Write-Host "`nRunning tests: $($plan.testCommand)"
    Push-Location $script:repoRoot
    try {
        Invoke-Expression $plan.testCommand
        if ($LASTEXITCODE -ne 0) { Write-Error 'Tests FAILED.' }
        else { Write-Host 'Tests: PASSED' }
    } finally { Pop-Location }
} elseif (-not $buildOk) {
    Write-Host 'Tests: skipped (build failed)'
} else {
    Write-Host 'Tests: skipped'
}