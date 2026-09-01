<#
.SYNOPSIS
    Calls one tool on an MCP server over HTTP and returns its parsed payload.

.DESCRIPTION
    Exists because "hooks run as separate processes and cannot speak MCP" is only
    true of STDIO. A stdio server is a child process the client owns, and nothing
    else can reach it. An HTTP server is a port, and any process can dial it --
    which means a script can ask the graph a question directly instead of going
    through a CLI twin or a saved-graph file.

    Measured against RazorGraph.Mcp on 7718 before this was written: initialize,
    session header, initialised notification, tools/call, real answer back.

    THREE THINGS MAKE THE NAIVE ATTEMPT FAIL, and each is handled here:

      * RESPONSES ARE SSE-FRAMED. The body arrives as "event: message" then
        "data: {json}", not as bare JSON, so Invoke-RestMethod parses it as a
        string and hands back something that looks broken. This is the whole
        difference between three lines and thirty.
      * THE SESSION ID IS A HEADER, NOT A FIELD. initialize returns
        Mcp-Session-Id, and every later request on that session must echo it.
      * THE HANDSHAKE HAS THREE STEPS. initialize, then a notifications/initialized
        with no id (a notification, so no response), and only then tools/call.
        Skipping the middle step gets a server that answers nothing.

    The tool's own payload is JSON inside a text content block, so it is unwrapped
    twice: once out of the JSON-RPC envelope, once out of the content block. Pass
    -Raw to stop before the second unwrap when a tool returns prose.

.PARAMETER Tool
    Tool name, e.g. graph_summary.

.PARAMETER Arguments
    Hashtable of tool arguments. Omit for a tool that takes none.

.PARAMETER Uri
    Server endpoint. Defaults to the razorgraph server on 7718; janet is 7717.

.PARAMETER ListTools
    Return the server's tool list instead of calling anything. Cheap way to check
    a server is alive and which surface it is serving.

.PARAMETER Raw
    Return the content block as text rather than parsing it as JSON. Use for tools
    whose output is prose.

.PARAMETER TimeoutSec
    Per-request timeout. Default 120, because build_solution compiles a solution.

.PARAMETER Text
    Formatted output for a terminal. The default is JSON.

.PARAMETER Pretty
    Indent the JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-McpTool.ps1" -Tool graph_summary -Arguments @{ graphId = 'gamehub' }

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-McpTool.ps1" -ListTools -Text
#>
[CmdletBinding(DefaultParameterSetName = 'Call')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Call', Position = 0)][string]$Tool,
    [Parameter(ParameterSetName = 'Call')][hashtable]$Arguments = @{},
    [Parameter(Mandatory, ParameterSetName = 'List')][switch]$ListTools,
    [string]$Uri = 'http://127.0.0.1:7718/',
    [switch]$Raw,
    [int]$TimeoutSec = 120,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$protocolVersion = '2025-06-18'
$headers = @{
    'Content-Type' = 'application/json'
    'Accept'       = 'application/json, text/event-stream'
}

# An SSE body carries one JSON document per "data:" line. Servers may also answer
# a plain application/json body, so both shapes are accepted rather than assuming
# the framing this server happens to use today.
function Read-RpcBody {
    param([string]$Body)

    $dataLines = @(
        $Body -split "`n" |
            Where-Object { $_.TrimStart().StartsWith('data:') } |
            ForEach-Object { $_.TrimStart().Substring(5).Trim() })

    if ($dataLines.Count -eq 0) {
        if ([string]::IsNullOrWhiteSpace($Body)) { return $null }
        return $Body | ConvertFrom-Json
    }

    return ($dataLines -join '') | ConvertFrom-Json
}

function Invoke-Rpc {
    param(
        [hashtable]$Payload,
        [hashtable]$RequestHeaders,
        [switch]$Notification
    )

    $response = Invoke-WebRequest -Uri $Uri -Method Post -Headers $RequestHeaders `
        -Body ($Payload | ConvertTo-Json -Depth 12) -TimeoutSec $TimeoutSec
    if ($Notification) { return $null }
    return Read-RpcBody -Body $response.Content
}

$started = Get-Date
try {
    $handshake = Invoke-WebRequest -Uri $Uri -Method Post -Headers $headers -TimeoutSec $TimeoutSec -Body (
        @{
            jsonrpc = '2.0'; id = 1; method = 'initialize'
            params  = @{
                protocolVersion = $protocolVersion
                capabilities    = @{}
                clientInfo      = @{ name = 'Invoke-McpTool'; version = '1' }
            }
        } | ConvertTo-Json -Depth 8)
}
catch {
    $envelope = [ordered]@{
        ok    = $false
        uri   = $Uri
        tool  = if ($ListTools) { 'tools/list' } else { $Tool }
        error = "cannot reach the MCP server: $($_.Exception.Message)"
        hint  = 'Start it with Ensure-McpServer.ps1 (7717) or Ensure-RazorGraphServer.ps1 (7718).'
    }
    if ($Text) { Write-Host $envelope.error -ForegroundColor Red; Write-Host "  $($envelope.hint)" -ForegroundColor DarkGray }
    else { [PSCustomObject]$envelope | ConvertTo-Json -Depth 4 }
    exit 1
}

$sessionId = $handshake.Headers['Mcp-Session-Id']
if ($sessionId -is [array]) { $sessionId = $sessionId[0] }

$sessionHeaders = $headers.Clone()
if ($sessionId) { $sessionHeaders['Mcp-Session-Id'] = $sessionId }

$serverInfo = (Read-RpcBody -Body $handshake.Content).result.serverInfo

# A notification, so it carries no id and the server sends nothing back. Omitting
# it leaves the session un-initialised and later calls answer nothing.
Invoke-Rpc -Payload @{ jsonrpc = '2.0'; method = 'notifications/initialized' } `
    -RequestHeaders $sessionHeaders -Notification | Out-Null

if ($ListTools) {
    $reply = Invoke-Rpc -Payload @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' } -RequestHeaders $sessionHeaders
    $names = @($reply.result.tools | ForEach-Object { $_.name } | Sort-Object)
    $envelope = [ordered]@{
        ok         = $true
        uri        = $Uri
        server     = if ($serverInfo) { "$($serverInfo.name) $($serverInfo.version)" } else { $null }
        toolCount  = $names.Count
        tools      = $names
        elapsedSec = [math]::Round(((Get-Date) - $started).TotalSeconds, 2)
    }

    if (-not $Text) {
        if ($Pretty) { [PSCustomObject]$envelope | ConvertTo-Json -Depth 4 }
        else { [PSCustomObject]$envelope | ConvertTo-Json -Depth 4 -Compress }
        exit 0
    }

    Write-Host "$($envelope.server) on $Uri -- $($names.Count) tools"
    foreach ($name in $names) { Write-Host "  $name" }
    exit 0
}

$reply = Invoke-Rpc -RequestHeaders $sessionHeaders -Payload @{
    jsonrpc = '2.0'; id = 2; method = 'tools/call'
    params  = @{ name = $Tool; arguments = $Arguments }
}

# A protocol-level error (unknown tool, bad arguments) and a tool-level failure
# (isError on the result) are different things, and collapsing them would hide
# which one happened.
if ($reply.PSObject.Properties.Name -contains 'error' -and $reply.error) {
    $envelope = [ordered]@{
        ok = $false; uri = $Uri; tool = $Tool
        error = "$($reply.error.message)"; code = $reply.error.code
    }
    if ($Text) { Write-Host "$Tool refused: $($envelope.error)" -ForegroundColor Red }
    else { [PSCustomObject]$envelope | ConvertTo-Json -Depth 4 }
    exit 1
}

$textBlock = ($reply.result.content | Where-Object { $_.type -eq 'text' } | Select-Object -First 1).text
$payload = $textBlock
if (-not $Raw -and $textBlock) {
    try { $payload = $textBlock | ConvertFrom-Json }
    catch { $payload = $textBlock }   # Prose, not JSON. Hand it back as it came.
}

$envelope = [ordered]@{
    ok         = -not ($reply.result.PSObject.Properties.Name -contains 'isError' -and $reply.result.isError)
    uri        = $Uri
    server     = if ($serverInfo) { "$($serverInfo.name) $($serverInfo.version)" } else { $null }
    tool       = $Tool
    elapsedSec = [math]::Round(((Get-Date) - $started).TotalSeconds, 2)
    result     = $payload
}

if (-not $Text) {
    if ($Pretty) { [PSCustomObject]$envelope | ConvertTo-Json -Depth 24 }
    else { [PSCustomObject]$envelope | ConvertTo-Json -Depth 24 -Compress }
    if (-not $envelope.ok) { exit 1 }
    exit 0
}

if (-not $envelope.ok) {
    Write-Host "$Tool reported an error:" -ForegroundColor Red
    Write-Host "  $textBlock"
    exit 1
}

Write-Host "$Tool ok in $($envelope.elapsedSec)s"
$payload | ConvertTo-Json -Depth 24
exit 0
