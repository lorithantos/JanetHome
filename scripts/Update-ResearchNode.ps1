# JANET-SHIM
<#
.SYNOPSIS
    Updates an existing research.json node in place, with validation.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI.
    Behaviour is unchanged: this is a PATCH, so only the fields you pass are touched and
    fields the tool does not know about are preserved. -Append merges into an existing array
    rather than replacing it, dropping repeats. Required fields cannot be removed. The node is
    spliced into the file text, so the file's curation survives.

    PREFER -Json for prose. The JSON form is also a patch: only the fields present are touched.

.PARAMETER Append
    Merge array values into what is already there instead of replacing them.

.PARAMETER Remove
    Field names to delete from the node. Refuses id, kind, path, and summary.
#>
[CmdletBinding()]
param(
    [string]$Id,
    [string]$Summary,
    [ValidateSet('script', 'pattern', 'note', 'file', 'skill')]
    [string]$Kind,
    [string]$NodePath,
    [string]$Section,
    [string[]]$Tags,
    [string[]]$Links,
    [string[]]$Caveats,
    [string[]]$ScriptParams,
    [switch]$Append,
    [string[]]$Remove = @(),
    [string]$Json,
    [string]$JsonPath,
    [string]$GraphPath,
    [switch]$DryRun,
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

if ($Json -and $JsonPath) { throw 'Give -Json or -JsonPath, not both.' }

$arguments = @('research', 'update') + (Get-JanetBaseArgument $PSScriptRoot)

if ($Json) { $arguments += @('--json', $Json) }
if ($JsonPath) { $arguments += @('--json-path', $JsonPath) }
if ($Id) { $arguments += @('--id', $Id) }

# Only fields actually asked for are forwarded. Passing everything would turn a patch into a
# template that blanks whatever it forgot to mention -- the distinction between clearing a
# field and leaving it alone is the whole reason unknown fields survive.
if ($PSBoundParameters.ContainsKey('Summary')) { $arguments += @('--summary', $Summary) }
if ($PSBoundParameters.ContainsKey('Kind')) { $arguments += @('--kind', $Kind) }
if ($PSBoundParameters.ContainsKey('NodePath')) { $arguments += @('--path', $NodePath) }
if ($PSBoundParameters.ContainsKey('Section')) { $arguments += @('--section', $Section) }

foreach ($pair in @(
        @{ Bound = 'Tags'; Flag = '--tag'; Values = $Tags },
        @{ Bound = 'Links'; Flag = '--link'; Values = $Links },
        @{ Bound = 'Caveats'; Flag = '--caveat'; Values = $Caveats },
        @{ Bound = 'ScriptParams'; Flag = '--param'; Values = $ScriptParams })) {

    if (-not $PSBoundParameters.ContainsKey($pair.Bound)) { continue }
    foreach ($value in @($pair.Values)) { if ($value) { $arguments += @($pair.Flag, $value) } }
}

foreach ($field in @($Remove)) { if ($field) { $arguments += @('--remove', $field) } }
if ($GraphPath) { $arguments += @('--graph', $GraphPath) }
if ($Append) { $arguments += '--append' }
if ($DryRun) { $arguments += '--dry-run' }

$output = & (Get-JanetCommand) @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $Text) { $output; exit 0 }

$result = $output | ConvertFrom-Json
if (-not $result.updated -and @($result.changes).Count -eq 0) {
    Write-Host "No changes for $($result.id)." -ForegroundColor Yellow
}
else {
    $verb = if ($result.dryRun) { 'Would update' } else { 'Updated' }
    Write-Host "$verb $($result.id)" -ForegroundColor Green
    foreach ($change in @($result.changes)) {
        Write-Host "  $($change.field): '$($change.from)' -> '$($change.to)'"
    }
    if ($result.dryRun -and $result.PSObject.Properties.Name -contains 'nodeText') {
        Write-Host ''
        Write-Host $result.nodeText
    }
}

foreach ($warning in @($result.warnings)) { Write-Warning $warning }
