<#
.SYNOPSIS
    Pop the active topic from the thread stack and activate the next one.

.PARAMETER Text
    Formatted output instead of JSON.

.OUTPUTS
    JSON: { popped, active, remaining } -- 'popped' and 'active' are null when there
    was nothing to pop or nothing left. Popping an empty stack is not an error.

.EXAMPLE
    & "$env:JanetBase\scripts\Pop-ThreadStack.ps1"

.EXAMPLE
    & "$env:JanetBase\scripts\Pop-ThreadStack.ps1" -Keep
#>
[CmdletBinding()]
param(
    [string]$Path = "$env:TEMP\Janet\thread-stack.json",
    [switch]$Keep,
    [switch]$Text
)

function Write-Result {
    param($Popped, $Active, [int]$Remaining)
    if ($Text) {
        if ($Popped) { Write-Host "Done: $Popped" } else { Write-Host 'Nothing active.' }
        if ($Active) { Write-Host "Active: $Active" }
        elseif ($Popped) { Write-Host 'Stack empty -- nothing left.' }
        Write-Host "Remaining: $Remaining"
        return
    }
    [PSCustomObject]@{
        popped    = $Popped
        active    = $Active
        remaining = $Remaining
    } | ConvertTo-Json -Depth 3 -Compress
}

# No file, or a zero-length one left by a previous pop, both mean "empty".
$stack = @()
if (Test-Path $Path) {
    $raw = Get-Content $Path -Raw
    if ($raw -and $raw.Trim()) {
        $parsed = $raw | ConvertFrom-Json
        if ($null -ne $parsed) { $stack = @($parsed) }
    }
}

if ($stack.Count -eq 0) { Write-Result $null $null 0; return }

$active = $stack | Where-Object { $_.status -eq 'active' } | Select-Object -First 1
if (-not $active) { Write-Result $null $null @($stack | Where-Object { $_.status -ne 'done' }).Count; return }

$active.status = 'done'
$poppedTopic = $active.topic

# Activate next parked item
$nextTopic = $null
$next = $stack | Where-Object { $_.status -eq 'parked' } | Select-Object -First 1
if ($next) {
    $next.status = 'active'
    $nextTopic = $next.topic
}

# Optionally keep done items or filter them out
if (-not $Keep) {
    $stack = @($stack | Where-Object { $_.status -ne 'done' })
}

# See Push-ThreadStack.ps1: utf8NoBOM is PowerShell 7+ only and fails silently
# on 5.1, losing the write.
$json = $stack | ConvertTo-Json -Depth 3
[System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding $false))

$remaining = @($stack | Where-Object { $_.status -ne 'done' })
Write-Result $poppedTopic $nextTopic $remaining.Count
