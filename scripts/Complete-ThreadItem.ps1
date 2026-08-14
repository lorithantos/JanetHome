# JANET-SHIM
<#
.SYNOPSIS
    Marks a thread item finished.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI;
    this script forwards to it so every existing caller keeps working.

    Completion is a status change, not a deletion. The stack this list replaced dropped items
    when they finished, so completing work erased the record of having done it; here the item
    stays and simply stops showing by default.

    With no selector, completes whatever is in focus.

.PARAMETER Topic
    Substring of the topic to complete. Ambiguity is refused, not guessed at.

.NOTES
    -Index was removed on 2026-08-14. The list is keyed by topic: Show-ThreadItems filters
    completed items before printing while an index counted into the unfiltered file, so a
    displayed number was wrong by the done count and completed a different item.
#>
[CmdletBinding()]
param(
    [string]$Topic = '',
    [string]$Path = '',
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('thread', 'complete')
if ($Topic) { $arguments += @('--topic', $Topic) }
if ($Path) { $arguments += @('--path', $Path) }

& $janet @arguments
exit $LASTEXITCODE
