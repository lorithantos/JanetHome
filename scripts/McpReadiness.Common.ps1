<#
.SYNOPSIS
    Whether a local MCP server that is listening has finished starting: its /health, read
    whatever the status.

.DESCRIPTION
    Dot-sourced by Ensure-McpServer.ps1 and Update-McpServer.ps1. Not a standalone script --
    it defines functions and does nothing on its own. Like the other *.Common.ps1 files it
    does NOT set Set-StrictMode, which would leak into the caller; the caller sets its own.

    WHY LISTENING IS NOT READY. Since 2026-11-24 janet-mcp binds its port FIRST and finishes
    starting after (the token read), and razorgraph-mcp is being changed to match. Between the
    two, GET /health answers 503 with the server's normal body plus "state": "starting", and
    every other route answers 503 with Retry-After: 1. So a port that accepts a connection, or
    a listener pid that matches, says the server is THERE -- enough to stop a second launch --
    but not that a tools/list or load_graph will be answered. Callers that are about to use
    the server wait on Wait-McpServerReady; callers that only ask "is something there" keep
    their TCP or pid check.

    A 200 is ready, with or without a 'state' field: a server older than the field answered
    /health only once it was ready. Anything else that answers (a 404, a body that is not
    JSON, a 503 that does not say starting) is 'other' -- not a readiness answer, so the
    caller carries on as it did before /health said anything about readiness.

    Not the same job as script.invoke-mcp-tool, which speaks MCP to a server assumed ready;
    this asks only /health, which needs no session and no token.
#>

function Get-McpHealthField {
    # One top-level string field of a parsed /health body, or $null when the body is not a
    # JSON object or does not carry it (house rule 2: absent, not null, under StrictMode).
    param($Body, [Parameter(Mandatory)][string]$Name)
    if ($Body -isnot [System.Management.Automation.PSCustomObject]) { return $null }
    if ($Body.PSObject.Properties.Name -notcontains $Name) { return $null }
    if ($null -eq $Body.$Name) { return $null }
    return [string]$Body.$Name
}

function Get-McpHealthState {
    # GET http://127.0.0.1:<Port>/health once, as { State; Status; Server }. State is:
    #   ready     200.
    #   starting  503 whose JSON body says "state": "starting".
    #   other     some other HTTP answer.
    #   none      no HTTP answer at all (refused, reset, timed out).
    # Never throws. Sends no Authorization header: /health is exempt on both servers.
    param([Parameter(Mandatory)][int]$Port, [int]$TimeoutSec = 2)

    try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/health" -Method Get `
            -TimeoutSec $TimeoutSec -SkipHttpErrorCheck -ErrorAction Stop
    }
    catch {
        return [pscustomobject]@{ State = 'none'; Status = $null; Server = $null }
    }

    $status = [int]$response.StatusCode
    $text = if ($response.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($response.Content) } else { "$($response.Content)" }
    $body = $null
    try { $body = $text | ConvertFrom-Json -ErrorAction Stop } catch { $body = $null }

    $declared = Get-McpHealthField -Body $body -Name 'state'
    $state = 'other'
    if ($status -eq 200) { $state = 'ready' }
    elseif ($status -eq 503 -and $declared -eq 'starting') { $state = 'starting' }

    return [pscustomobject]@{ State = $state; Status = $status; Server = (Get-McpHealthField -Body $body -Name 'server') }
}

function Wait-McpServerReady {
    # Polls Get-McpHealthState while the answer is 'starting' or 'none', until the deadline,
    # or until $Process (anything with HasExited) has exited. Returns { State; Status; Exited }
    # with the last answer; Exited is true only when the process ending is what stopped the
    # wait. Ready or other returns at once: only a server that says it is starting, or has
    # stopped answering, is waited on. Never throws.
    param(
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)][datetime]$Deadline,
        $Process,
        [int]$IntervalMs = 200
    )

    $last = Get-McpHealthState -Port $Port
    while ($last.State -in @('starting', 'none')) {
        if ($null -ne $Process -and $Process.HasExited) {
            return [pscustomobject]@{ State = $last.State; Status = $last.Status; Exited = $true }
        }
        if ((Get-Date) -ge $Deadline) { break }
        Start-Sleep -Milliseconds $IntervalMs
        $last = Get-McpHealthState -Port $Port
    }
    return [pscustomobject]@{ State = $last.State; Status = $last.Status; Exited = $false }
}

function Test-McpServerReady {
    # Whether a Wait-McpServerReady result means "go ahead and use it": ready, or an answer
    # that is not about readiness at all (other). Starting, none and an exit are not.
    param([Parameter(Mandatory)]$Readiness)
    return (-not $Readiness.Exited) -and ($Readiness.State -in @('ready', 'other'))
}

function Format-McpReadiness {
    # How a log line names a server that Test-McpServerReady passed: 'ready on <port>' when
    # /health said so, and otherwise only what is known -- that it is listening.
    param([Parameter(Mandatory)]$Readiness, [Parameter(Mandatory)][int]$Port)
    if ($Readiness.State -eq 'ready') { return "ready on $Port" }
    return "listening on $Port (/health answered $($Readiness.Status), which does not say whether it is ready)"
}
