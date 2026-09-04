# JANET-SHIM
<#
.SYNOPSIS
    Shows the thread item list: investigation topics, and which one is in focus.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI;
    this script forwards to it so every existing caller keeps working.

    This one is load-bearing at startup: startup-manifest.json runs it and captures its JSON
    under 'threadStack', so its envelope is a contract rather than an output format. The shape
    { count, active, items[], error } is asserted byte for byte against what the original
    printed -- see ThreadGoldenTests and note.golden-tests.

    Behaviour is unchanged, with one improvement: -Text now goes to stdout rather than
    Write-Host, so it can be captured by a pipe, a redirect, or an assignment without 6>&1.

    'error' reports a list that could not be read, in-band and without throwing, because this
    runs before a session has anything else. 'active' is null when nothing is in focus, which
    is an ordinary state and not a fault.

.PARAMETER Path
    List file to read. Defaults to Janet\thread-stack.json under TEMP.

.PARAMETER All
    Include completed items. They are kept, never deleted, and hidden by default.

.PARAMETER Topic
    Case-insensitive substring naming exactly ONE item, returned with its notes in full. An
    ambiguous topic is refused with every candidate named, and one that matches nothing is
    refused too rather than answered with an empty list -- 'no such item' and 'no open work'
    are different claims. A '*' is a literal asterisk, not a wildcard.

.PARAMETER Area
    Narrows to one area, case-insensitive substring. '(unfiled)' is the group of items with no
    area set; items are never guessed into a neighbouring one.

    'active' still names the focus of the WHOLE list under either selector, so a narrowed
    answer does not read as though nothing is in focus.

.EXAMPLE
    .\Show-ThreadItems.ps1 -Area JanetHome -Text
#>
[CmdletBinding()]
param(
    [string]$Path = '',
    [switch]$All,
    [string]$Topic = '',
    [string]$Area = '',
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('thread', 'show')
if ($Path) { $arguments += @('--path', $Path) }
if ($All) { $arguments += '--all' }
if ($Topic) { $arguments += @('--topic', $Topic) }
if ($Area) { $arguments += @('--area', $Area) }
if ($Text) { $arguments += '--text' }
if ($Pretty) { $arguments += '--pretty' }

& $janet @arguments
exit $LASTEXITCODE
