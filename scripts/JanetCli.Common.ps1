<#
.SYNOPSIS
    Shared helpers for the shims that forward to the janet CLI.

.DESCRIPTION
    Dot-sourced by Get-Research.ps1, Add-/Update-/Rename-ResearchNode.ps1. Not a standalone
    script -- it defines functions and does nothing when run.

    Also by the MCP HTTP clients (Invoke-McpTool.ps1, Find-DivergentStateRoots.ps1,
    Test-OutputContracts.ps1) for the bearer header: Get-McpAuthHeader asks
    `janet mcp-headers` for it, and Get-McpAuthRefusal words a 401.

    Deliberately does NOT Set-StrictMode: strict mode set in a dot-sourced file leaks into the
    caller's scope. Each entry point sets its own (house rules 5 and 7).
#>

function Get-JanetCommand {
    <#
    .SYNOPSIS
        Resolves the janet CLI, or explains precisely how to get it.
    #>
    [CmdletBinding()]
    param()

    $janet = Get-Command 'janet' -ErrorAction SilentlyContinue
    if ($null -ne $janet) { return $janet.Source }

    throw @'
The janet CLI is not on PATH, and the catalog now lives behind it.

  dotnet tool install -g Janet.Cli --add-source <JanetBase>\.janet-bin\pkg

Or rebuild the server and reinstall the CLI in one step:
  scripts\Update-McpServer.ps1 -Project src\Janet.Mcp\Janet.Mcp.csproj `
      -ToolProject src\Janet.Cli\Janet.Cli.csproj
'@
}

function Get-JanetBaseArgument {
    <#
    .SYNOPSIS
        The --base pair pointing at this repo, so a shim works from any working directory.
    .DESCRIPTION
        Resolved from the script's own location rather than the caller's, because these are
        invoked from hooks, pre-commit gates, and other repos -- none of which can be relied
        on to have the right directory current.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptRoot)

    return , @('--base', (Split-Path $ScriptRoot -Parent))
}

function Get-McpAuthHeader {
    <#
    .SYNOPSIS
        The Authorization header the local MCP servers expect, from `janet mcp-headers`, or
        none and the reason why.
    .DESCRIPTION
        Returns [pscustomobject]@{ Header; Reason }. Header is a hashtable to merge into a
        request's headers: @{ Authorization = 'Bearer ...' } when janet supplied one, or an
        empty hashtable when it did not. Reason is $null when a header was found, and otherwise
        says why not -- it never carries the token, so it is safe to print.

        No header is NOT an error here. A server started with --auth report or off answers
        without one, and the installed janet may predate the mcp-headers verb (it answers
        "Unknown command" with exit 2). Any non-zero exit, a missing janet, or stdout that is
        not a JSON object with an Authorization string all mean "send nothing"; a server that
        requires the token then answers 401, and Get-McpAuthRefusal turns that into a message
        that names the fix.

        Call it ONCE per script run and reuse the header: each call starts a janet process.
        The token is only ever held in the returned hashtable; never write Header anywhere.
    .PARAMETER Janet
        The janet executable to ask. Defaults to janet on PATH.
    #>
    [CmdletBinding()]
    param([string]$Janet)

    $result = [pscustomobject]@{ Header = @{}; Reason = $null }

    if (-not $Janet) {
        $command = Get-Command 'janet' -ErrorAction SilentlyContinue
        if ($null -eq $command) {
            $result.Reason = 'janet is not on PATH, so `janet mcp-headers` could not be asked'
            return $result
        }
        $Janet = $command.Source
    }

    try {
        $all = @(& $Janet mcp-headers 2>&1)
        $code = $LASTEXITCODE
    }
    catch {
        $result.Reason = "janet mcp-headers could not run: $($_.Exception.Message)"
        return $result
    }

    # stderr arrives as ErrorRecords beside the stdout strings. Only stderr is ever echoed:
    # stdout is where the token would be.
    $stderr = @($all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } |
            ForEach-Object { "$_".Trim() } | Where-Object { $_ })
    $stdout = @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join "`n"

    if ($code -ne 0) {
        # The first stderr line only: an older janet prints its whole usage text here.
        $first = if ($stderr.Count -gt 0) { $stderr[0] } else { '' }
        if ($first.Length -gt 200) { $first = $first.Substring(0, 200) + '...' }
        $result.Reason = "janet mcp-headers exited $code" + $(if ($first) { ": $first" } else { '' })
        return $result
    }

    $parsed = $null
    try { $parsed = $stdout | ConvertFrom-Json -AsHashtable -ErrorAction Stop }
    catch { $parsed = $null }

    if ($parsed -isnot [hashtable] -or -not $parsed.ContainsKey('Authorization') -or
        [string]::IsNullOrWhiteSpace([string]$parsed['Authorization'])) {
        $result.Reason = 'janet mcp-headers printed no {"Authorization": ...} object'
        return $result
    }

    $result.Header = @{ Authorization = [string]$parsed['Authorization'] }
    return $result
}

function Get-McpAuthRefusal {
    <#
    .SYNOPSIS
        The message for an MCP server's 401, or $null when the error is not one.
    .DESCRIPTION
        A 401 from janet-mcp or razorgraph-mcp means the server runs with --auth require and the
        request did not carry its bearer token. That is a configuration fact with one fix, not
        a server that is down, so it is reported as such: the server's own plain-text body, then
        whether a header was sent, then what to run. Never names the token.
    .PARAMETER ErrorRecord
        The error Invoke-WebRequest threw.
    .PARAMETER Uri
        The server endpoint, for the message.
    .PARAMETER AuthReason
        Get-McpAuthHeader's Reason: $null when a header was sent, else why none was.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Management.Automation.ErrorRecord]$ErrorRecord,
        [Parameter(Mandatory)][string]$Uri,
        [string]$AuthReason
    )

    $exception = $ErrorRecord.Exception
    if ($exception.PSObject.Properties.Name -notcontains 'Response') { return $null }
    $response = $exception.Response
    if ($null -eq $response -or [int]$response.StatusCode -ne 401) { return $null }

    $body = '(no body)'
    if ($null -ne $ErrorRecord.ErrorDetails -and -not [string]::IsNullOrWhiteSpace($ErrorRecord.ErrorDetails.Message)) {
        $body = $ErrorRecord.ErrorDetails.Message.Trim()
    }

    $sent = if ($AuthReason) {
        "No Authorization header was sent ($AuthReason)."
    }
    else {
        'The Authorization header from `janet mcp-headers` was sent and not accepted.'
    }

    return "The MCP server at $Uri refused the request for lack of a valid bearer token (HTTP 401): $body " +
        "$sent Run ``janet mcp-token --ensure`` to create the token if it is absent; " +
        '`janet mcp-headers` is what supplies the header this script sends.'
}
