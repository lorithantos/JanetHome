# JANET-SHIM
<#
.SYNOPSIS
    Moves focus to a thread item, or clears it.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI;
    this script forwards to it so every existing caller keeps working.

    Focus is single: taking it always parks whatever held it, and the previous topic is
    reported so the switch is visible rather than silent. A completed item cannot take focus
    until it is reopened -- Update-ThreadItem.ps1 -Status parked.

.PARAMETER None
    Clear focus entirely rather than moving it. Cannot be combined with -Topic.

.NOTES
    -Index was removed on 2026-08-14. The list is keyed by topic: Show-ThreadItems filters
    completed items before printing while an index counted into the unfiltered file, so a
    displayed number was wrong by the done count and moved focus to a different item.
#>
[CmdletBinding()]
param(
    [string]$Topic = '',
    [switch]$None,
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

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('thread', 'active')
if ($None) { $arguments += '--none' }
if ($Topic) { $arguments += @('--topic', $Topic) }
if ($Path) { $arguments += @('--path', $Path) }

& $janet @arguments
exit $LASTEXITCODE
