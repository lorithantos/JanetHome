# JANET-SHIM
<#
.SYNOPSIS
    Moves focus to a thread item, or clears it.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI;
    this script forwards to it so every existing caller keeps working.

    Focus is one cursor PER AREA since 2026-09-06: taking it parks whatever held focus in
    THAT ITEM'S OWN AREA and nothing else, so picking up JanetHome work leaves a session in
    another repo holding what it was holding. The parked topic is still reported as
    'previous' so the switch is visible rather than silent. A completed item cannot take
    focus until it is reopened -- Update-ThreadItem.ps1 -Status parked.

.PARAMETER None
    Clear one area's focus rather than moving it. Cannot be combined with -Topic. Name the
    area with -Area; without it the clear succeeds only while a single area holds focus, and
    is otherwise refused with every area and its topic named.

.PARAMETER Area
    Which area's focus to clear, case-insensitive substring resolving to exactly one area.
    Applies only with -None: an item takes focus in the area it is filed under, so passing
    both an area and a topic says two different things about which area is meant, and is
    refused. '(unfiled)' is an area like any other and has its own cursor.

.NOTES
    -Index was removed on 2026-08-14. The list is keyed by topic: Show-ThreadItems filters
    completed items before printing while an index counted into the unfiltered file, so a
    displayed number was wrong by the done count and moved focus to a different item.
#>
[CmdletBinding()]
param(
    [string]$Topic = '',
    [switch]$None,
    [string]$Area = '',
    [string]$Path = '',
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Checked here rather than forwarded: -None with a selector is a contradiction, and the CLI
# would silently honour one of them.
if ($None -and $Topic -ne '') {
    throw '-None cannot be combined with -Topic.'
}
if (-not $None -and $Topic -eq '') {
    throw 'Pass -Topic or -None.'
}
# -Area says whose cursor to clear. With a topic the item's own area governs, so the two
# together name two different areas; the CLI refuses it, and saying so here is cheaper.
if ($Area -ne '' -and -not $None) {
    throw '-Area applies only with -None. An item takes focus in the area it is filed under.'
}

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('thread', 'active')
if ($None) { $arguments += '--none' }
if ($Topic) { $arguments += @('--topic', $Topic) }
if ($Area) { $arguments += @('--area', $Area) }
if ($Path) { $arguments += @('--path', $Path) }

& $janet @arguments
exit $LASTEXITCODE
