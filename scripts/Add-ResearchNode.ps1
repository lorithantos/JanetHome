# JANET-SHIM
<#
.SYNOPSIS
    Adds a node to research.json, with validation, without hand-editing JSON.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI.
    Behaviour is unchanged: ids are validated, dangling links and missing paths are warned
    about, the reverse link is written on every existing target, and the node is spliced into
    the file text rather than round-tripped through a serializer -- so the hand-curated
    grouping, comment keys, and blank lines survive.

    PREFER -Json for anything prose-shaped. A summary routed through PowerShell's quoting
    rules is how this catalog once stored a doubled apostrophe that nothing downstream could
    tell from intent. JSON has exactly one escaping rule and the parser enforces it.

.PARAMETER Json
    The whole node as a JSON object: { id, kind, summary, path, tags[], links[], caveats[],
    params[], section }.

.PARAMETER JsonPath
    A file containing that same object, for content too large or quote-heavy for a command line.

.PARAMETER DryRun
    Validate and return the node text that would be written, without writing.
#>
[CmdletBinding(DefaultParameterSetName = 'Fields')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Fields')][string]$Id,
    [Parameter(Mandatory, ParameterSetName = 'Fields')]
    [ValidateSet('script', 'pattern', 'note', 'file', 'skill')]
    [string]$Kind,
    [Parameter(Mandatory, ParameterSetName = 'Fields')][string]$Summary,
    [Parameter(Mandatory, ParameterSetName = 'Fields')][string]$NodePath,
    [Parameter(ParameterSetName = 'Fields')][string[]]$Tags = @(),
    [Parameter(ParameterSetName = 'Fields')][string[]]$Links = @(),
    [Parameter(ParameterSetName = 'Fields')][string[]]$Caveats = @(),
    [Parameter(ParameterSetName = 'Fields')][string[]]$ScriptParams = @(),
    [Parameter(ParameterSetName = 'Fields')][string]$Section,
    [Parameter(Mandatory, ParameterSetName = 'Json')][string]$Json,
    [Parameter(Mandatory, ParameterSetName = 'JsonFile')][string]$JsonPath,
    [string]$GraphPath,
    [switch]$DryRun,
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$arguments = @('research', 'add') + (Get-JanetBaseArgument $PSScriptRoot)

switch ($PSCmdlet.ParameterSetName) {
    'Json' { $arguments += @('--json', $Json) }
    'JsonFile' { $arguments += @('--json-path', $JsonPath) }
    default {
        $arguments += @('--id', $Id, '--kind', $Kind, '--summary', $Summary, '--path', $NodePath)
        foreach ($value in @($Tags)) { if ($value) { $arguments += @('--tag', $value) } }
        foreach ($value in @($Links)) { if ($value) { $arguments += @('--link', $value) } }
        foreach ($value in @($Caveats)) { if ($value) { $arguments += @('--caveat', $value) } }
        foreach ($value in @($ScriptParams)) { if ($value) { $arguments += @('--param', $value) } }
        if ($Section) { $arguments += @('--section', $Section) }
    }
}

if ($GraphPath) { $arguments += @('--graph', $GraphPath) }
if ($DryRun) { $arguments += '--dry-run' }

$output = & (Get-JanetCommand) @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $Text) { $output; exit 0 }

# The CLI is JSON-only by design -- its consumer is a model. The human view is rendered here,
# where the caller who asked for it is.
$result = $output | ConvertFrom-Json
if ($result.added) {
    Write-Host "Added $($result.id) ($($result.totalNodes) nodes total)" -ForegroundColor Green
    if (@($result.reverseLinks).Count -gt 0) {
        Write-Host "Reverse-linked from: $(@($result.reverseLinks) -join ', ')" -ForegroundColor DarkGray
    }
}
else {
    Write-Host ''
    Write-Host "Would add to the graph:" -ForegroundColor Cyan
    Write-Host $result.nodeText
    Write-Host ''
}

foreach ($warning in @($result.warnings)) { Write-Host "! $warning" -ForegroundColor Yellow }
