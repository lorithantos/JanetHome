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

    It is a SEPARATE VERB rather than a switch on Show, deliberately. Show's envelope is
    captured by startup under 'threadStack' and asserted byte for byte against recorded output
    from the pre-shim PowerShell, which the golden generator extracts out of git and re-runs --
    so that envelope cannot grow a field without a declared correction to a contract that has a
    live consumer. Nothing existing changes; this is a new format beside it.

    Each item carries notesLead (the first NON-EMPTY line, capped at 200 characters) and
    notesLength (the full size). The envelope totals what was left behind, so the omission is
    stated rather than implied -- the same rule the catalog follows when it reports its own
    truncation. Read one item in full with Show-ThreadItems.ps1 once you know which one.

.PARAMETER Path
    List file to read. Defaults to Janet\thread-stack.json under TEMP.

.PARAMETER All
    Include completed items. They are kept, never deleted, and hidden by default.

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
if ($Text) { $arguments += '--text' }
if ($Pretty) { $arguments += '--pretty' }

& $janet @arguments
exit $LASTEXITCODE
