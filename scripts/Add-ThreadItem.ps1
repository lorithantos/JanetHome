# JANET-SHIM
<#
.SYNOPSIS
    Records an investigation topic on the thread item list.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI;
    this script forwards to it so every existing caller keeps working.

    Adding does NOT take focus unless -Active is passed. That separation is the whole reason
    this is a list rather than the push/pop stack it replaced: noting work you are not doing
    now has to cost nothing, or you stop noting it.

    Refuses a topic that already exists rather than creating a second one the selectors cannot
    tell apart.

.PARAMETER Topic
    What the investigation is about. One line, distinctive enough to select on later.

.PARAMETER Next
    The resume cursor: the one thing to do first on return.

.PARAMETER Refs
    Catalog node ids carrying the context for this topic.

.PARAMETER Active
    Also take focus, parking whatever held it.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Topic,
    [string]$Notes = '',
    [string]$Next = '',
    [string[]]$Refs = @(),
    [switch]$Active,
    [string]$Path = '',
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('thread', 'add', '--topic', $Topic)

# Repeated flags rather than a delimited string: the CLI accumulates them, so no caller has to
# know a separator. Getting that wrong is how a multi-value argument silently becomes one.
foreach ($value in @($Refs)) { if ($value) { $arguments += @('--ref', $value) } }

if ($Notes) { $arguments += @('--notes', $Notes) }
if ($Next) { $arguments += @('--next', $Next) }
if ($Path) { $arguments += @('--path', $Path) }
if ($Active) { $arguments += '--active' }

& $janet @arguments
exit $LASTEXITCODE
