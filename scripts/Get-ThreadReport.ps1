# JANET-SHIM
<#
.SYNOPSIS
    Reports the thread item list as a map: topics, focus, and note sizes -- without the note
    bodies.

.DESCRIPTION
    A shim. The implementation is ThreadItems.Report in Janet.Core, reached through the `janet`
    CLI, and shared with the thread_report MCP tool so the three cannot disagree.

    This answers "where was I", which is the question asked on resuming. Show-ThreadItems.ps1
    answers "what exactly did I write down", which is a different and much more expensive
    question: on 2026-08-14 the same list was 174,129 characters through Show and the MCP tool
    refused to return it at all, while three items held a third of that between them.

    It is a SEPARATE VERB rather than a switch on Show, deliberately. Show's envelope was
    captured by startup under 'threadStack' until 2026-08-14 and is asserted byte for byte against recorded output
    from the pre-shim PowerShell, which the golden generator extracts out of git and re-runs --
    so that envelope cannot grow a field without a declared correction to a contract that has a
    live consumer. Nothing existing changes; this is a new format beside it.

    Each item carries notesLead (the first NON-EMPTY line, capped at 200 characters) and
    notesLength (the full size). The envelope totals what was left behind, so the omission is
    stated rather than implied -- the same rule the catalog follows when it reports its own
    truncation. Read one item in full with Show-ThreadItems.ps1 once you know which one.

    The envelope also carries 'areas' since 2026-09-04 (report contract 3): one { area, open }
    row per area with open items, over the WHOLE list, whatever -Area narrowed to -- the same
    rule 'active' follows. Startup passes -Area through the manifest's 'args', so a session's
    brief holds its own project's items plus this map of everyone else's; before that the
    unnarrowed report was 38,058 of the brief's 42,241 characters.

.PARAMETER Path
    List file to read. Defaults to Janet\thread-stack.json under TEMP.

.PARAMETER All
    Include completed items. They are kept, never deleted, and hidden by default.

.PARAMETER Topic
    Case-insensitive substring naming exactly ONE item. Ambiguous is refused with every
    candidate named; unmatched is refused too, rather than answered with an empty list.

.PARAMETER Area
    Narrows to one area, case-insensitive substring. Each item carries the area it is filed
    under -- a STORED label, never derived from the topic -- and '(unfiled)' is the group of
    items with none. Nothing was backfilled, so most items read as (unfiled) until they are
    labelled deliberately.

    This is the selector the list needed: it is shared by every repo on this machine, so an
    unnarrowed report is mostly some other project's work. The startup manifest passes it
    through the run entry's 'args'; the value lives there, not here.

.PARAMETER NoLead
    Omit notesLead from every item; notesLength stays, so the size withheld is still stated.
    Added 2026-09-04 by measurement: narrowed to JanetHome the report was 5,430 characters
    of a 9,969-character startup brief whose budget is about 8,000, and the leads were 1,827
    of it. The startup manifest passes this too. 'next' is never dropped -- it is the field the
    report exists to deliver.

.PARAMETER Text
    The formatted view, for a terminal. The default is compressed JSON: the consumer is a model.

.PARAMETER Pretty
    Indent the JSON.

.EXAMPLE
    .\Get-ThreadReport.ps1

.EXAMPLE
    # What is open, and which items have grown into logs?
    .\Get-ThreadReport.ps1 -Text
#>
param(
    [string]$Path = '',
    [switch]$All,
    [string]$Topic = '',
    [string]$Area = '',
    [switch]$NoLead,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('thread', 'report')
if ($Path) { $arguments += @('--path', $Path) }
if ($All) { $arguments += '--all' }
if ($Topic) { $arguments += @('--topic', $Topic) }
if ($Area) { $arguments += @('--area', $Area) }
if ($NoLead) { $arguments += '--no-lead' }
if ($Text) { $arguments += '--text' }
if ($Pretty) { $arguments += '--pretty' }

& $janet @arguments
exit $LASTEXITCODE
