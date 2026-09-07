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
    Case-insensitive substring naming exactly ONE item. Add -Full for its notes whole. An
    ambiguous topic is refused with every candidate named, and one that matches nothing is
    refused too rather than answered with an empty list -- 'no such item' and 'no open work'
    are different claims. A '*' is a literal asterisk, not a wildcard.

    This script is UNBOUNDED by count, like the CLI it shims: an unnarrowed list of any size
    comes back whole, so redirect to a file when it is large. The thread_show MCP tool is not
    -- a result over the result budget (100,000 characters, JANET_RESULT_BUDGET overrides) is
    refused with a hint naming -Topic and -Area, never cut.

.PARAMETER Full
    Return the selected item's notes whole rather than the lead. Since 2026-09-05 every item's
    'notes' is a LEAD -- the first non-empty line, capped at 200 characters, the same lead
    Get-ThreadReport carries -- with 'notesLength' (the stored size) beside it and
    'notesTruncated': true whenever the lead is not the whole text. -Full needs -Topic and is
    refused with -Area or with no selector: notes are read one item at a time, and a flag that
    expanded them across a set would re-create the oversized read the lead exists to prevent.

.PARAMETER Area
    Narrows to one area, case-insensitive substring. '(unfiled)' is the group of items with no
    area set; items are never guessed into a neighbouring one.

    'active' names the focus of THIS ANSWER'S SCOPE since 2026-09-06: this area's cursor when
    -Area narrowed, and null when it did not. Focus is one cursor PER AREA now, so there is no
    single focus of the whole list left to name -- and a null here means "you did not narrow",
    not "nothing is in focus". Show carries no areas map, so use Get-ThreadReport.ps1 when the
    question is what every project has in hand: its envelope carries every area's cursor.

.EXAMPLE
    .\Show-ThreadItems.ps1 -Area JanetHome -Text

.EXAMPLE
    .\Show-ThreadItems.ps1 -Topic 'cache eviction' -Full
#>
[CmdletBinding()]
param(
    [string]$Path = '',
    [switch]$All,
    [string]$Topic = '',
    [string]$Area = '',
    [switch]$Full,
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
if ($Full) { $arguments += '--full' }
if ($Text) { $arguments += '--text' }
if ($Pretty) { $arguments += '--pretty' }

& $janet @arguments
exit $LASTEXITCODE
