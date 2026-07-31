<#
.SYNOPSIS
    Show the current thread stack.

.DESCRIPTION
    Output is JSON by default -- this runs at session start and its reader is the
    session model, not a terminal. -Text for the formatted view.

    An empty stack is a valid, expected result and uses the same envelope shape as a
    populated one, so a consumer never special-cases it.

.PARAMETER Path
    Stack file. Defaults to $env:TEMP\Janet\thread-stack.json.

.PARAMETER All
    Include completed items.

.PARAMETER Text
    Formatted output instead of JSON.

.PARAMETER Pretty
    Indent the JSON.

.OUTPUTS
    JSON: { depth, active, items[] } where each item is { topic, status, notes }.
    'active' is the current topic, or null when nothing is active.

.EXAMPLE
    & "$env:JanetBase\scripts\Show-ThreadStack.ps1"

.EXAMPLE
    & "$env:JanetBase\scripts\Show-ThreadStack.ps1" -Text -All
#>
[CmdletBinding()]
param(
    [string]$Path = "$env:TEMP\Janet\thread-stack.json",
    [switch]$All,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest

function Write-Result {
    param($Items)
    $arr = @($Items)
    $activeItem = $arr | Where-Object { $_.status -eq 'active' } | Select-Object -First 1
    $result = [PSCustomObject]@{
        depth  = $arr.Count
        active = if ($activeItem) { $activeItem.topic } else { $null }
        items  = $arr
    }
    if ($Pretty) { $result | ConvertTo-Json -Depth 4 }
    else { $result | ConvertTo-Json -Depth 4 -Compress }
}

# Pop leaves a zero-length file once the last item is gone, so "no file" and "empty
# file" are both normal and must not look like failure.
$stack = @()
if (Test-Path $Path) {
    $raw = Get-Content $Path -Raw
    if ($raw -and $raw.Trim()) {
        $parsed = $raw | ConvertFrom-Json
        if ($null -ne $parsed) { $stack = @($parsed) }
    }
}

if (-not $All) { $stack = @($stack | Where-Object { $_.status -ne 'done' }) }

if (-not $Text) { Write-Result $stack; return }

if ($stack.Count -eq 0) { Write-Host 'Stack empty.'; return }

$stack | ForEach-Object {
    $icon = switch ($_.status) { 'active' { '>>>' } 'parked' { '   ' } 'done' { ' x ' } }
    Write-Host "$icon $($_.status.PadRight(7)) $($_.topic)"
    if ($_.notes) { Write-Host "           $($_.notes)" -ForegroundColor DarkGray }
}
