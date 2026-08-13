# JANET-SHIM
<#
.SYNOPSIS
    Queries a .NET XML documentation file the way Get-Research.ps1 queries the catalog.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI;
    this script forwards to it so every existing caller keeps working.

    Researching a library from raw doc XML by grep is expensive and bad at the job: the file
    is one stream of hard-wrapped indented <member> elements, so a match costs dozens of
    context lines and the answer arrives split across them. This parses it and returns
    members -- kind, declaring type, readable signature, summary, per-parameter docs -- with
    doc markup flattened to prose.

    JSON by default, because the consumer is a model; -Text is the opt-in reading view.

    Free-text results are RANKED and capped: check 'truncated'. A selector (-Type, -Kind,
    -Id) is never capped, because it is a request for a known set.

    One thing changed at the cutover, and in your favour: the -Text view is now capturable.
    It used to be written with Write-Host, so `$x = ... -Text` yielded nothing and the text
    only appeared on the console. The shim's output is a child process's stdout, so it
    assigns and redirects like any other output.

.PARAMETER Package
    NuGet package id, resolved from the local package cache. Prefix match; the newest version
    and newest target framework win unless -Version / -Tfm say otherwise.

.PARAMETER Path
    An explicit .xml documentation file, bypassing package resolution.

.PARAMETER Full
    Include remarks, exceptions, and type parameters.
#>
[CmdletBinding()]
param(
    [string]$Package = '',
    [string]$Path = '',
    [string]$Version = '',
    [string]$Tfm = '',
    [string]$Query = '',
    [string[]]$Id = @(),
    [ValidateSet('Type', 'Method', 'Property', 'Field', 'Event')]
    [string]$Kind = '',
    [string]$Type = '',
    [int]$First = 5,
    [switch]$All,
    [switch]$Full,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('api')
if ($Package) { $arguments += @('--package', $Package) }
if ($Path) { $arguments += @('--path', $Path) }
if ($Version) { $arguments += @('--version', $Version) }
if ($Tfm) { $arguments += @('--tfm', $Tfm) }
if ($Query) { $arguments += @('--query', $Query) }
foreach ($value in @($Id)) { $arguments += @('--id', $value) }
if ($Kind) { $arguments += @('--kind', $Kind) }
if ($Type) { $arguments += @('--type', $Type) }

# Only when the caller asked, so the default lives in one place rather than two.
if ($PSBoundParameters.ContainsKey('First')) { $arguments += @('--first', $First) }

if ($All) { $arguments += '--all' }
if ($Full) { $arguments += '--full' }
if ($Text) { $arguments += '--text' }
if ($Pretty) { $arguments += '--pretty' }

& $janet @arguments
exit $LASTEXITCODE
