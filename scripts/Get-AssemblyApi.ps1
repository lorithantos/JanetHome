# JANET-SHIM
<#
.SYNOPSIS
    Report a compiled assembly's real API surface: types and their declared members.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI;
    this script forwards to it so every existing caller keeps working.

    Answers "what is this library actually called" without a guess-and-compile loop.

    SIBLINGS. Loading a DLL straight out of a nuget lib folder fails, because that folder
    holds one assembly and its dependencies are elsewhere. References resolve from the folder
    the assembly sits in, so pointing at a build or publish output -- where the whole closure
    is laid out side by side -- just works. A folder with no siblings is now reported in the
    envelope as 'siblingWarning' rather than on the warning stream, where an MCP client never
    saw it and a redirected caller dropped it.

    PARTIAL LOADS. Types that failed to load are counted in 'typesUnloadable' and the rest
    are used. Members whose signature names a type that cannot be resolved are counted in
    'membersDropped'. The original promised the partial answer and delivered it only at the
    outer level: it recovered from the failed GetTypes and then threw reading a property's
    type. The recovery now runs where the failure happens.

    THE PROCESS-LIFETIME TRAP IS GONE. LoadFrom pinned every assembly for the life of the
    process, and an agent shell reuses one process, so rebuilding the target and re-running
    returned the OLD surface, and a sibling problem reproduced only in a fresh process. Each
    request now loads into its own collectible context. Note the limit: the assembly under
    inspection is always read fresh, but a DEPENDENCY already loaded by the host process
    still resolves, so the isolation is total for the CLI and partial inside a long-lived
    server.

.PARAMETER Assembly
    Path to the .dll, or a bare assembly name to find under -SearchRoot.

.PARAMETER SearchRoot
    Directory to search when -Assembly is a name. The candidate with the most sibling
    assemblies wins, since that is the one whose dependencies resolve.

.PARAMETER Inherited
    Include members declared on base types. Off by default: a syntax-node class inherits
    dozens of members and the interesting ones are its own.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Assembly,

    [string]$SearchRoot = '.',
    [string]$Type = '',
    [string]$Member = '',
    [switch]$Inherited,
    [switch]$Static,
    [int]$MaxTypes = 40,
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('assembly', '--assembly', $Assembly)
if ($SearchRoot) { $arguments += @('--search-root', $SearchRoot) }
if ($Type) { $arguments += @('--type', $Type) }
if ($Member) { $arguments += @('--member', $Member) }
if ($Inherited) { $arguments += '--inherited' }
if ($Static) { $arguments += '--static' }

# Only when the caller asked, so the default lives in one place rather than two.
if ($PSBoundParameters.ContainsKey('MaxTypes')) { $arguments += @('--max-types', $MaxTypes) }

if ($Text) { $arguments += '--text' }

& $janet @arguments
exit $LASTEXITCODE
