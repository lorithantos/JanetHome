<#
.SYNOPSIS
    Manifest entry point for the razorgraph-mcp HTTP server. A thin forwarder to
    Ensure-McpServer.ps1 with razorgraph's parameters.

.DESCRIPTION
    This exists only because a manifest 'run' entry is a bare script path -- startup
    validates it with Test-Path -PathType Leaf and has nowhere to put arguments. Rather
    than teach the manifest an argument list, or copy the probe/start/staleness logic a
    second time, the parameterisation lives in Ensure-McpServer.ps1 and this file supplies
    the razorgraph half of it.

    JanetHome's own .mcp.json declares BOTH servers -- janet on 7717 and razorgraph on
    7718 -- so a session started here depends on razorgraph being up exactly as much as it
    depends on janet. Until 2026-08-31 startup only ensured janet, so razorgraph's tools
    could be silently absent with every startup gate green: the same quiet degradation the
    manifest contract exists to catch, one server over.

    Never throws, for the same reason Ensure-McpServer.ps1 does not (house rule 6): this
    runs in the startup path, and the JSON result carries every failure.

.PARAMETER Repo
    RazorGraphTool checkout. The rotation root (.mcp-bin\current) and the source tree used
    for the staleness count both hang off it.

.PARAMETER Port
    Must match the url in .mcp.json. 7718 by convention -- janet holds 7717.

.PARAMETER NoStart
    Probe and report without starting anything.

.PARAMETER Pretty
    Indent the JSON. The default is compressed: the consumer is a model, not a terminal.
#>
[CmdletBinding()]
param(
    [string]$Repo = 'C:\repos\RazorGraphTool',
    [int]$Port = 7718,
    [switch]$NoStart,
    [switch]$Pretty
)

Set-StrictMode -Version Latest

# Resolved from $PSScriptRoot rather than cwd (house rule 6): startup invokes this with the
# repo root as cwd sometimes and not others.
$ensure = Join-Path $PSScriptRoot 'Ensure-McpServer.ps1'

# razorgraph-mcp takes no --base: it is told which solution to graph per call, not per
# process. Passing janet's default argument set would make the server exit on startup.
& $ensure `
    -Name 'RazorGraph.Mcp' `
    -Port $Port `
    -Base $Repo `
    -BinDir (Join-Path $Repo '.mcp-bin\current') `
    -SourceDir (Join-Path $Repo 'src\RazorGraph.Mcp') `
    -ServerArgument '--http', '--port', "$Port" `
    -NoStart:$NoStart `
    -Pretty:$Pretty
