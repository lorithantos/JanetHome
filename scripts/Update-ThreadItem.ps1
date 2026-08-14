# JANET-SHIM
<#
.SYNOPSIS
    Amends a thread item in place.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI;
    this script forwards to it so every existing caller keeps working.

    Not supplied and supplied-as-empty are different requests: an omitted -Next leaves the
    resume cursor alone, while -Next '' clears it. Dropping a cursor that no longer applies is
    a real thing to want, so the two cannot mean the same.

    Select with -Topic (case-insensitive substring); with none, this acts on whatever is in
    focus. An ambiguous topic is refused and every candidate named, rather than resolved to a
    first match -- the operation rewrites the file, and amending the wrong item is how notes
    get lost. -Index was removed on 2026-08-14 for the same reason: the list is keyed by topic,
    and a displayed position is wrong by the number of completed items above it.

.PARAMETER AppendNotes
    Add to the existing notes rather than replacing them, separated by a blank line.

.PARAMETER AppendRefs
    Add to the existing refs rather than replacing them. Repeats are kept, deliberately: a
    repeated ref is the caller's to decide about.
#>
[CmdletBinding()]
param(
    [string]$Topic = '',
    [string]$Notes = $null,
    [string]$Next = $null,
    [string[]]$Refs = $null,
    [ValidateSet('active', 'parked', 'done')][string]$Status = '',
    [switch]$AppendNotes,
    [switch]$AppendRefs,
    [string]$Path = '',
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('thread', 'update')

# Presence, not truthiness: -Notes '' is a request to clear, and testing the value would
# silently drop it.
if ($PSBoundParameters.ContainsKey('Notes')) { $arguments += @('--notes', $Notes) }
if ($PSBoundParameters.ContainsKey('Next')) { $arguments += @('--next', $Next) }
if ($PSBoundParameters.ContainsKey('Refs')) {
    foreach ($value in @($Refs)) { $arguments += @('--ref', $value) }
}

if ($Topic) { $arguments += @('--topic', $Topic) }
if ($Status) { $arguments += @('--status', $Status) }
if ($Path) { $arguments += @('--path', $Path) }
if ($AppendNotes) { $arguments += '--append-notes' }
if ($AppendRefs) { $arguments += '--append-refs' }

& $janet @arguments
exit $LASTEXITCODE
