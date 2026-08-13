# JANET-SHIM
<#
.SYNOPSIS
    Renames a node id in research.json, sweeping every inbound link, without hand-editing JSON.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI.
    Behaviour is unchanged: an id change is not an edit to one node, so every links array
    naming the old id moves too -- a rename that misses one leaves a dangling link that reads
    exactly like a deleted node. Each affected node is spliced inside its own brace-matched
    span, so the file's curation survives.

    The graph is this operation's jurisdiction; note bodies are not. Markdown still mentioning
    the old id comes back in bodyReferences, reported and never rewritten. Fix those with
    Invoke-SurgicalEdit.ps1 or by hand, then re-run to confirm zero.

.PARAMETER NewId
    The id to rename to. Must not already exist -- renaming onto a live id would merge two
    nodes and silently lose one.

.PARAMETER Kind
    Also replace the kind. Ids conventionally carry the kind as a prefix, so a rename is
    usually a re-kind too.

.PARAMETER DryRun
    Report what would change without writing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Id,
    [Parameter(Mandatory)][string]$NewId,
    [ValidateSet('script', 'pattern', 'note', 'file', 'skill')]
    [string]$Kind,
    [string]$GraphPath,
    [switch]$DryRun,
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$arguments = @('research', 'rename') + (Get-JanetBaseArgument $PSScriptRoot) + @('--id', $Id, '--new-id', $NewId)

if ($Kind) { $arguments += @('--kind', $Kind) }
if ($GraphPath) { $arguments += @('--graph', $GraphPath) }
if ($DryRun) { $arguments += '--dry-run' }

$output = & (Get-JanetCommand) @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $Text) { $output; exit 0 }

$result = $output | ConvertFrom-Json
$verb = if ($result.renamed) { 'Renamed' } else { 'Would rename' }
Write-Host "$verb $($result.id) -> $($result.newId)" -ForegroundColor Green

if (@($result.relinked).Count -gt 0) {
    Write-Host "  relinked: $(@($result.relinked) -join ', ')" -ForegroundColor DarkGray
}

# Called out separately and in yellow because it is the part this operation deliberately did
# NOT fix. A quiet list under a green heading reads as done.
foreach ($reference in @($result.bodyReferences)) {
    Write-Host "  ! still mentioned in $reference" -ForegroundColor Yellow
}

foreach ($warning in @($result.warnings)) { Write-Warning $warning }
