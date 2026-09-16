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

.PARAMETER Notes
    Replacement notes, or '' to clear. Notes are CAPPED at 8,000 characters
    (JANET_NOTES_BUDGET overrides), measured on what the item would hold after the write.

    A REPLACEMENT over the ceiling is refused whole: long-form belongs in a catalogued note --
    write it to notes\<slug>.md, `janet research add` it as note.<slug>, put the id in -Refs,
    and replace the notes with a shorter working log. An -AppendNotes over the ceiling is not
    refused; see -NoSplit.

.PARAMETER Next
    Replacement resume cursor, or '' to clear. Capped at 1,000 characters, with no override:
    it is the one thing to do first on return, and a cursor needing a thousand characters is
    notes wearing the wrong label. Put the detail in -Notes.

.PARAMETER AppendNotes
    Add to the existing notes rather than replacing them, separated by a blank line.

    An append that would cross the notes ceiling ARCHIVES the oldest paragraphs to
    notes\<slug>-<date>-<hash>.md beside the store and keeps the newest half live behind a
    pointer line naming that file. The response carries a 'split' object whose 'where' is the
    path. It is refused instead when the stored notes are one block with nowhere to cut, or
    when the text being appended leaves no room.

.PARAMETER NoSplit
    Refuse an over-ceiling -AppendNotes whole rather than archiving the oldest paragraphs, which
    is how this behaved before archiving existed. JANET_NOTES_AUTOSPLIT=off does the same for a
    whole process.

.PARAMETER AppendRefs
    Add to the existing refs rather than replacing them. Repeats are kept, deliberately: a
    repeated ref is the caller's to decide about.

.PARAMETER Area
    Which project or area the item belongs to, or '' to unfile it. This is how an existing item
    gets labelled: nothing was backfilled when the field was added, because inferring an area
    from a topic is exactly what the field exists to avoid, so most items read as '(unfiled)'
    until someone says otherwise. Omitted leaves it alone; -Area '' clears it.
#>
[CmdletBinding()]
param(
    [string]$Topic = '',
    [string]$Notes = $null,
    [string]$Next = $null,
    [string[]]$Refs = $null,
    [string]$Area = $null,
    [ValidateSet('active', 'parked', 'done')][string]$Status = '',
    [switch]$AppendNotes,
    [switch]$AppendRefs,
    [switch]$NoSplit,
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

if ($PSBoundParameters.ContainsKey('Area')) { $arguments += @('--area', $Area) }

if ($Topic) { $arguments += @('--topic', $Topic) }
if ($Status) { $arguments += @('--status', $Status) }
if ($Path) { $arguments += @('--path', $Path) }
if ($AppendNotes) { $arguments += '--append-notes' }
if ($AppendRefs) { $arguments += '--append-refs' }
if ($NoSplit) { $arguments += '--no-split' }

& $janet @arguments
exit $LASTEXITCODE
