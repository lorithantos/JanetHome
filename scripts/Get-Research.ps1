# JANET-SHIM
<#
.SYNOPSIS
    Queries research.json -- the node graph of scripts, patterns, notes, and skills.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI;
    this script forwards to it so every existing caller keeps working -- hooks, Test-PreCommit,
    Start-Janet, and muscle memory alike. Invoke-ResearchGuard.ps1 in particular shells out to
    this path on every Write of a new script, and a hook is a separate process that cannot
    speak MCP, which is why the CLI exists at all.

    Behaviour is unchanged, with one improvement: -Text now goes to stdout rather than
    Write-Host, so it can be captured by a pipe, a redirect, or an assignment without 6>&1.
    The caveat recorded against this script about that is retired.

    Everything the old help described still holds -- the ranked shortlist, the caveats shown
    in every view, the {returned, totalMatches, truncated} envelope that reports its own
    truncation. Run `janet research query --help`, or query the catalog for
    script.get-research.

.PARAMETER Path
    Graph file to query. Defaults to research.json in JanetBase.

.PARAMETER NoTrace
    Suppress the retrieval trace. Used by Invoke-ResearchGuard.ps1's own lookup, which must
    not clear the check it is about to perform.
#>
[CmdletBinding()]
param(
    [string[]]$Id,
    [string[]]$Tag,
    [ValidateSet('script', 'pattern', 'note', 'file', 'skill')]
    [string]$Kind,
    [string]$Query,
    [int]$First = 5,
    [switch]$All,
    [switch]$Expand,
    [int]$Depth = 1,
    [switch]$Full,
    [switch]$Text,
    [switch]$Pretty,
    [string]$Path,
    [switch]$NoTrace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$janet = Get-Command 'janet' -ErrorAction SilentlyContinue
if ($null -eq $janet) {
    throw @'
The janet CLI is not on PATH, and the catalog now lives behind it.

  dotnet tool install -g Janet.Cli --add-source <JanetBase>\.janet-bin\pkg

Or rebuild and install in one step:
  scripts\Update-McpServer.ps1 -Project src\Janet.Mcp\Janet.Mcp.csproj -ToolProject src\Janet.Cli\Janet.Cli.csproj
'@
}

$repoRoot = Split-Path $PSScriptRoot -Parent

$arguments = @('research', 'query', '--base', $repoRoot)

# Array parameters become repeated flags rather than a delimited string: the CLI accumulates
# them, so no caller ever has to know a separator. Getting that wrong is exactly how a
# multi-value argument silently becomes one string.
foreach ($value in @($Id)) { if ($value) { $arguments += @('--id', $value) } }
foreach ($value in @($Tag)) { if ($value) { $arguments += @('--tag', $value) } }

if ($Kind) { $arguments += @('--kind', $Kind) }
if ($Query) { $arguments += @('--query', $Query) }
if ($PSBoundParameters.ContainsKey('First')) { $arguments += @('--first', $First) }
if ($PSBoundParameters.ContainsKey('Depth')) { $arguments += @('--depth', $Depth) }
if ($Path) { $arguments += @('--graph', $Path) }
if ($All) { $arguments += '--all' }
if ($Expand) { $arguments += '--expand' }
if ($Full) { $arguments += '--full' }
if ($Text) { $arguments += '--text' }
if ($Pretty) { $arguments += '--pretty' }
if ($NoTrace) { $arguments += '--no-trace' }

& $janet.Source @arguments
exit $LASTEXITCODE
