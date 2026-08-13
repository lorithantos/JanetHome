<#
.SYNOPSIS
    Show the thread item list.

.DESCRIPTION
    Output is JSON by default -- this runs at session start and its reader is the
    session model, not a terminal. -Text for the formatted view.

    An empty list is a valid, expected result and uses the same envelope shape as
    a populated one, so a consumer never special-cases it.

    Runs in the startup path, so it does not throw: an unreadable or corrupt list
    is reported in-band via the 'error' field with an otherwise empty envelope,
    rather than taking the session down (house rule 6, DESIGN-NOTES section 8).
    Reported, not swallowed -- a corrupt list is worth seeing.

.PARAMETER Path
    List file. Defaults to $env:TEMP\Janet\thread-stack.json.

.PARAMETER All
    Include completed items. They are retained rather than dropped, so this is
    the project history, not just what is outstanding.

.PARAMETER Text
    Formatted output instead of JSON.

.PARAMETER Pretty
    Indent the JSON.

.OUTPUTS
    JSON: { count, active, items[], error } where each item is
    { topic, status, refs[], next, notes }. 'active' is the current topic, or
    null when nothing is active -- which is an ordinary state, not a fault.

.EXAMPLE
    & "$env:JanetBase\scripts\Show-ThreadItems.ps1"

.EXAMPLE
    & "$env:JanetBase\scripts\Show-ThreadItems.ps1" -Text -All
#>
[CmdletBinding()]
param(
    [string]$Path = '',
    [switch]$All,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'ThreadItems.Common.ps1')

$items = @()
$readError = $null
try {
    # Assign, never @(...) -- Read-ThreadItems comma-wraps, and re-wrapping it
    # yields one element holding the real array (house rule 1).
    $items = Read-ThreadItems -Path $Path
}
catch {
    $readError = $_.Exception.Message
    $items = @()
}

if (-not $All) { $items = @($items | Where-Object { $_.status -ne 'done' }) }

$activeTopic = $null
foreach ($item in $items) {
    if ($item.status -eq 'active') { $activeTopic = $item.topic; break }
}

if (-not $Text) {
    $result = [PSCustomObject]@{
        count  = $items.Count
        active = $activeTopic
        items  = @($items)
        error  = $readError
    }
    # Depth 6: result -> items -> item -> refs -> string, with headroom.
    if ($Pretty) { ConvertTo-Json -InputObject $result -Depth 6 }
    else { ConvertTo-Json -InputObject $result -Depth 6 -Compress }
    return
}

if ($null -ne $readError) { Write-Host "List unreadable: $readError" -ForegroundColor Red; return }
if ($items.Count -eq 0) { Write-Host 'No thread items.'; return }

foreach ($item in $items) {
    $icon = '   '
    if ($item.status -eq 'active') { $icon = '>>>' }
    elseif ($item.status -eq 'done') { $icon = ' x ' }

    Write-Host "$icon $($item.status.PadRight(7)) $($item.topic)"
    if (@($item.refs).Count -gt 0) {
        Write-Host "            refs: $(@($item.refs) -join ', ')" -ForegroundColor DarkCyan
    }
    if ($item.next -ne '') {
        Write-Host "            next: $($item.next)" -ForegroundColor DarkYellow
    }
    if ($item.notes -ne '') {
        $firstLine = @($item.notes -split "`n")[0]
        if ($firstLine.Length -gt 100) { $firstLine = $firstLine.Substring(0, 100) + '...' }
        Write-Host "            $firstLine" -ForegroundColor DarkGray
    }
}
