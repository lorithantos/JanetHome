<#
.SYNOPSIS
    Push a topic onto the thread stack.

.PARAMETER Topic
    The topic name to push.

.PARAMETER Notes
    Optional notes for context.

.PARAMETER Text
    Formatted output instead of JSON.

.OUTPUTS
    JSON: { pushed, depth, active, parked }

.EXAMPLE
    & "$env:JanetBase\scripts\Push-ThreadStack.ps1" -Topic 'cache eviction investigation' -Notes 'Need to query the telemetry table'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Topic,
    [string]$Notes = '',
    [string]$Path = "$env:TEMP\Janet\thread-stack.json",
    [switch]$Text
)

$dir = Split-Path $Path -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

# Pop leaves a zero-length file once the last item goes, so treat empty as empty
# rather than letting ConvertFrom-Json decide.
$stack = @()
if (Test-Path $Path) {
    $raw = Get-Content $Path -Raw
    if ($raw -and $raw.Trim()) {
        $parsed = $raw | ConvertFrom-Json
        if ($null -ne $parsed) { $stack = @($parsed) }
    }
}

# Park the current active item
$parked = $null
$stack | ForEach-Object {
    if ($_.status -eq 'active') { $_.status = 'parked'; $parked = $_.topic }
}

# Push new item
$entry = [PSCustomObject]@{ topic = $Topic; status = 'active'; notes = $Notes }
$stack = @($entry) + @($stack)

# -Encoding utf8NoBOM is PowerShell 7+ only; under Windows PowerShell 5.1 it
# throws and the push is silently lost -- the script still prints "Pushed".
# WriteAllText with an explicit no-BOM encoder behaves identically in both.
$json = $stack | ConvertTo-Json -Depth 3
[System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding $false))

if ($Text) {
    Write-Host "Pushed: $Topic"
    Write-Host "Stack depth: $($stack.Count)"
    return
}

[PSCustomObject]@{
    pushed = $Topic
    depth  = $stack.Count
    active = $Topic
    parked = $parked   # what this push displaced, or null
} | ConvertTo-Json -Depth 3 -Compress
