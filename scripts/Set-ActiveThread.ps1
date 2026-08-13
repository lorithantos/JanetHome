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
    Clear focus entirely rather than moving it. Cannot be combined with -Topic or -Index.
#>
[CmdletBinding()]
param(
    [string]$Topic = '',
    [int]$Index = -1,
    [switch]$None,
    [string]$Path = '',
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Checked here rather than forwarded: -None with a selector is a contradiction, and the CLI
# would silently honour one of them.
if ($None -and ($Topic -ne '' -or $Index -ge 0)) {
    throw '-None cannot be combined with -Topic or -Index.'
}
if (-not $None -and $Topic -eq '' -and $Index -lt 0) {
    throw 'Pass -Topic, -Index, or -None.'
}

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('thread', 'active')
if ($None) { $arguments += '--none' }
if ($Topic) { $arguments += @('--topic', $Topic) }
if ($Index -ge 0) { $arguments += @('--index', $Index) }
if ($Path) { $arguments += @('--path', $Path) }

& $janet @arguments
exit $LASTEXITCODE
