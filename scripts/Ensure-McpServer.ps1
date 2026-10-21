<#
.SYNOPSIS
    Make sure an MCP HTTP server is up before a session starts, and report what it is
    actually serving. Defaults to janet-mcp; -Name points it at any of them.

.DESCRIPTION
    The HTTP transport is a separate process the client dials, which is what lets the
    server be rebuilt mid-session -- but it also means nothing starts it. .mcp.json names
    a URL, so a session whose server is down does not fail: it silently loses every
    research_*, thread_*, api_* and dotnet_check tool and falls back to the CLI, which
    works well enough that nobody notices. That is quiet degradation of exactly the kind
    the manifest contract exists to prevent, one layer below where the manifest looks.

    This runs as a manifest 'run' entry, so the answer lands in the brief every session.

    It reports SERVED versus DECLARED tool counts, because a server being up is not the
    same as a server being current. On 2026-08-14 the installed server answered with three
    tools while the source declared twelve; both global tools report 0.1.0 and always have,
    so the version number could not distinguish a fresh install from an ancient one. A
    count read off the running process can.

    Never throws. A startup script that can take the session down is worse than the
    problem it solves (house rule 6), so every failure is captured into the JSON result.

    THE BEARER TOKEN (2026-10-21). Every launch gets --auth from startup-manifest.json's
    'mcpAuth', the one setting ServerRotation.cs and ServerLink.cs read too. Before starting
    a server this runs `janet mcp-token --ensure`, which creates the per-machine token when
    there is none -- that is how a fresh machine gets one -- and records its status (never
    the token) under 'token'. The tools/list probe sends the header `janet mcp-headers`
    prints, fetched once per run, or none when there is no token; a 401 is reported as
    state 'unauthorized', not as 'not-answering'. /health is never probed with a header.

.PARAMETER Port
    TCP port the server listens on. Must match the url in .mcp.json.

.PARAMETER Base
    Repo the server serves. Defaults to the repo this script lives in.

.PARAMETER ServerPath
    Server executable. Defaults to <BinDir>\<Name>.exe -- the junction Update-McpServer.ps1
    maintains -- falling back to <Name> on PATH.

.PARAMETER Name
    Server binary and process name, without .exe. Drives the default executable, the log
    file names, and the remediation text. Defaults to janet-mcp.

.PARAMETER BinDir
    Directory holding the 'current' junction Update-McpServer.ps1 repoints. Defaults to
    <repo>\.janet-bin\current. Point it at another repo's rotation root to ensure that
    repo's server -- RazorGraphTool\.mcp-bin\current, for instance.

.PARAMETER ServerArgument
    Arguments for the started process. Defaults to janet's --http/--base/--port set. Pass
    it explicitly for a server that does not take --base: razorgraph-mcp does not. Either
    way '--auth <mcpAuth>' is appended unless the list already carries --auth.

.PARAMETER SourceDir
    Source tree whose [McpServerTool( attributes are counted for the served-versus-declared
    staleness check. Defaults to <repo>\src\Janet.Mcp. Absent source means no comparison
    rather than a false verdict.

.PARAMETER TimeoutSeconds
    Budget for the readiness poll and for each HTTP probe.

.PARAMETER NoStart
    Probe and report without starting anything.

.PARAMETER Pretty
    Indent the JSON. The default is compressed: the consumer is a model, not a terminal.

.EXAMPLE
    .\Ensure-McpServer.ps1

.EXAMPLE
    # What is the running server actually serving?
    .\Ensure-McpServer.ps1 -NoStart -Pretty

.EXAMPLE
    # razorgraph-mcp, which lives in another repo and takes no --base.
    .\Ensure-McpServer.ps1 -Name RazorGraph.Mcp -Port 7718 `
        -Base C:\repos\RazorGraphTool `
        -BinDir C:\repos\RazorGraphTool\.mcp-bin\current `
        -SourceDir C:\repos\RazorGraphTool\src\RazorGraph.Mcp `
        -ServerArgument '--http', '--port', '7718', '--auth-token-command', 'janet mcp-token --print'
#>
[CmdletBinding()]
param(
    [int]$Port = 7717,
    [string]$Base,
    [string]$ServerPath,
    [string]$Name = 'janet-mcp',
    [string]$BinDir,
    [string[]]$ServerArgument,
    [string]$SourceDir,
    [int]$TimeoutSeconds = 10,
    [switch]$NoStart,
    [switch]$Pretty
)

Set-StrictMode -Version Latest

# Resolved from $PSScriptRoot rather than a hardcoded layout (house rule 6): this script is
# invoked by startup with the repo root as cwd sometimes and not others.
$repoRoot = Split-Path $PSScriptRoot -Parent

# Get-McpAuthHeader: the bearer header from `janet mcp-headers`, shared with the other MCP
# clients so they agree about what "no token" means.
. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

function Get-ListenerPid {
    # $null when nothing holds the port. Get-NetTCPConnection is Windows-only and throws
    # rather than returning empty when there is no match, so both are caught here.
    param([int]$TcpPort)
    try {
        $conn = @(Get-NetTCPConnection -State Listen -LocalPort $TcpPort -ErrorAction Stop)
        if ($conn.Count -eq 0) { return $null }
        return [int]$conn[0].OwningProcess
    }
    catch { return $null }
}

function Get-AuthMode {
    # The ONE auth setting: startup-manifest.json's top-level 'mcpAuth' (require, report or
    # off), which ServerRotation.cs and ServerLink.cs read too, so every launcher passes the
    # same --auth. $null when the manifest or the key is absent: no --auth is passed and the
    # server applies its own default. An unknown value throws, and the caller reports it.
    $manifestPath = Join-Path $repoRoot 'startup-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $null }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.PSObject.Properties.Name -notcontains 'mcpAuth') { return $null }
    $mode = "$($manifest.mcpAuth)".Trim().ToLowerInvariant()
    if ($mode -notin @('require', 'report', 'off')) {
        throw "$manifestPath sets mcpAuth to '$($manifest.mcpAuth)'; it must be require, report or off."
    }
    return $mode
}

function Invoke-JanetCli {
    # Runs the janet CLI and returns stdout, the exit code and the first stderr line. Never
    # throws: a missing janet, or one older than the verb, is a result to report, not a crash.
    # Used for `mcp-token --ensure`, whose stdout is a status line without the token; never
    # point it at a verb that prints the token, since its stdout goes into the result.
    param([string[]]$Arguments)

    $outcome = [ordered]@{ ExitCode = $null; Stdout = ''; Stderr = '' }
    $janet = Get-Command janet -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $janet) {
        $outcome.Stderr = 'janet is not on PATH'
        return [PSCustomObject]$outcome
    }

    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $outcome.Stdout = (& $janet.Source @Arguments 2> $errFile) -join "`n"
        $outcome.ExitCode = $LASTEXITCODE
        $outcome.Stderr = (Get-Content -LiteralPath $errFile -ErrorAction SilentlyContinue |
                Where-Object { $_ } | Select-Object -First 1)
    }
    catch {
        $outcome.Stderr = $_.Exception.Message
    }
    finally {
        Remove-Item -LiteralPath $errFile -Force -ErrorAction SilentlyContinue
    }
    return [PSCustomObject]$outcome
}

function Get-ServedToolName {
    # Returns a comma-wrapped array of tool names, empty when the server did not answer.
    # Comma-wrapped because 'return @()' emits nothing (house rule 1) -- assign the result,
    # never wrap the call in @(...). A 401 is recorded in $script:probeRefusal so the caller
    # can say the server REFUSED rather than that it did not answer.
    param([int]$TcpPort, [int]$Budget, [hashtable]$AuthHeader)

    $uri = "http://127.0.0.1:$TcpPort/"
    $headers = @{
        'Content-Type' = 'application/json'
        'Accept'       = 'application/json, text/event-stream'
    }
    if ($AuthHeader) { foreach ($key in $AuthHeader.Keys) { $headers[$key] = $AuthHeader[$key] } }
    try {
        $initBody = @{
            jsonrpc = '2.0'
            id      = 1
            method  = 'initialize'
            params  = @{
                protocolVersion = '2024-11-05'
                capabilities    = @{}
                clientInfo      = @{ name = 'Ensure-McpServer'; version = '1' }
            }
        } | ConvertTo-Json -Depth 6

        $init = Invoke-WebRequest -Uri $uri -Method Post -Headers $headers `
            -Body $initBody -TimeoutSec $Budget -ErrorAction Stop

        # A header the server chose not to send is absent, not null -- probe before reading.
        $sessionId = ''
        if ($init.Headers.ContainsKey('Mcp-Session-Id')) {
            $sessionId = ($init.Headers['Mcp-Session-Id'] | Select-Object -First 1)
        }
        if (-not $sessionId) { return , @() }

        $sessionHeaders = $headers.Clone()
        $sessionHeaders['Mcp-Session-Id'] = "$sessionId"

        $null = Invoke-WebRequest -Uri $uri -Method Post -Headers $sessionHeaders `
            -Body (@{ jsonrpc = '2.0'; method = 'notifications/initialized' } | ConvertTo-Json) `
            -TimeoutSec $Budget -ErrorAction Stop

        $listed = Invoke-WebRequest -Uri $uri -Method Post -Headers $sessionHeaders `
            -Body (@{ jsonrpc = '2.0'; id = 2; method = 'tools/list' } | ConvertTo-Json) `
            -TimeoutSec $Budget -ErrorAction Stop

        # Streamable HTTP answers as SSE; the payload is the 'data:' lines. A plain JSON
        # body is also legal, so fall back to the raw content.
        $payload = (($listed.Content -split "`n" |
                    Where-Object { $_ -like 'data: *' } |
                    ForEach-Object { $_.Substring(6) }) -join '')
        if (-not $payload) { $payload = $listed.Content }
        if (-not $payload) { return , @() }

        $parsed = $payload | ConvertFrom-Json
        if ($parsed.PSObject.Properties.Name -notcontains 'result') { return , @() }
        if ($parsed.result.PSObject.Properties.Name -notcontains 'tools') { return , @() }

        return , @($parsed.result.tools | ForEach-Object { $_.name })
    }
    catch [Microsoft.PowerShell.Commands.HttpResponseException] {
        if ([int]$_.Exception.Response.StatusCode -eq 401) {
            $body = "$($_.ErrorDetails.Message)".Trim()
            $script:probeRefusal = if ($body) { $body } else { '401 Unauthorized' }
        }
        return , @()
    }
    catch { return , @() }
}

function Get-DeclaredToolCount {
    # What the SOURCE says the surface is. $null when the source is not present, so a
    # deployment without src\ reports no comparison rather than a false mismatch.
    param([string]$SourceDir)
    try {
        if (-not (Test-Path -LiteralPath $SourceDir -PathType Container)) { return $null }
        $files = @(Get-ChildItem -LiteralPath $SourceDir -Filter '*.cs' -Recurse -ErrorAction Stop)
        if ($files.Count -eq 0) { return $null }
        # The paren matters: [McpServerTool(Name = "x")] is a tool, [McpServerToolType] is
        # the class that holds them. Matching the prefix alone counts the four container
        # classes as tools and reports a stale server that is perfectly current.
        $hits = @($files | Select-String -Pattern '\[McpServerTool\(' -AllMatches)
        return $hits.Count
    }
    catch { return $null }
}

$result = [ordered]@{
    ok            = $false
    state         = 'unknown'
    port          = $Port
    pid           = $null
    serverPath    = ''
    base          = ''
    toolsServed   = $null
    toolsDeclared = $null
    stale         = $false
    auth          = $null
    token         = $null
    note          = ''
    error         = $null
}

$script:probeRefusal = $null

try {
    if (-not $Base) { $Base = $repoRoot }
    $result.base = $Base

    if (-not $BinDir) { $BinDir = Join-Path $repoRoot '.janet-bin\current' }
    if (-not $SourceDir) { $SourceDir = Join-Path $repoRoot 'src\Janet.Mcp' }
    # Defaulted here rather than in the param block because it reads $Port and $Base, which
    # are not settled until this point. Same order as ServerRotation.cs's janet target.
    if (-not $ServerArgument) { $ServerArgument = @('--http', '--base', "$Base", '--port', "$Port") }

    # --auth from the one setting, appended unless the caller already chose. Both servers get
    # it this way: Ensure-RazorGraphServer.ps1 forwards here.
    $authMode = Get-AuthMode
    $result.auth = $authMode
    if ($null -ne $authMode -and $ServerArgument -notcontains '--auth') {
        $ServerArgument = @($ServerArgument) + @('--auth', $authMode)
    }

    if (-not $ServerPath) {
        # The junction Update-McpServer.ps1 repoints; a stable path that resolves to the
        # newest build by lookup. The global tool on PATH is a SEPARATE copy and goes stale
        # independently, so it is the fallback rather than the default.
        $junction = Join-Path $BinDir "$Name.exe"
        if (Test-Path -LiteralPath $junction -PathType Leaf) {
            $ServerPath = $junction
        }
        else {
            $onPath = Get-Command $Name -ErrorAction SilentlyContinue
            if ($null -ne $onPath) { $ServerPath = $onPath.Source }
        }
    }
    $result.serverPath = $ServerPath

    $listenerPid = Get-ListenerPid -TcpPort $Port

    if ($null -eq $listenerPid -and -not $NoStart) {
        if (-not $ServerPath -or -not (Test-Path -LiteralPath $ServerPath -PathType Leaf)) {
            $result.state = 'no-server-binary'
            $result.note = "No $Name binary found under $BinDir or on PATH. Build one with " +
                           "scripts\Update-McpServer.ps1 -ProcessName $Name " +
                           "-ServerArgument '$($ServerArgument -join "','")'"
        }
        else {
            # The token must exist before a server that requires it starts, and this is how it
            # comes to exist on a fresh machine: --ensure creates it when absent and leaves an
            # existing one alone. Its status line carries no token. A failure is reported, not
            # fatal -- a server under --auth report or off starts without one, and one under
            # require refuses to start and says so in its err.log.
            $ensured = Invoke-JanetCli -Arguments @('mcp-token', '--ensure')
            $result.token = if ($ensured.ExitCode -eq 0) {
                try { $ensured.Stdout | ConvertFrom-Json -ErrorAction Stop } catch { $ensured.Stdout }
            }
            else {
                [ordered]@{
                    ok    = $false
                    error = "janet mcp-token --ensure failed (exit $($ensured.ExitCode)): $($ensured.Stderr)"
                }
            }

            $logDir = Join-Path ([System.IO.Path]::GetTempPath()) 'Janet'
            # -Force is safe for a directory; it is -ItemType File -Force that truncates
            # (house rule 4).
            if (-not (Test-Path -LiteralPath $logDir -PathType Container)) {
                New-Item -ItemType Directory -Path $logDir -Force | Out-Null
            }

            # Start-Process joins -ArgumentList with spaces and quotes nothing, so an argument
            # holding a space -- razorgraph's --auth-token-command 'janet mcp-token --print' --
            # would arrive as three (measured 2026-10-21). Quote those, with Windows
            # command-line escaping; Update-McpServer.ps1 does the same.
            $argumentLine = @($ServerArgument | ForEach-Object {
                    if ($_ -match '[\s"]') { '"' + (($_ -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"' } else { $_ }
                })

            $started = Start-Process -FilePath $ServerPath `
                -ArgumentList $argumentLine `
                -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $logDir "$Name.out.log") `
                -RedirectStandardError (Join-Path $logDir "$Name.err.log")

            # Poll rather than sleep a fixed interval: the server is usually listening well
            # inside a second, and a startup script should not spend the whole budget.
            $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            while ((Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 200
                $listenerPid = Get-ListenerPid -TcpPort $Port
                if ($null -ne $listenerPid) { break }
                if ($started.HasExited) { break }
            }

            if ($null -eq $listenerPid) {
                $result.state = 'start-failed'
                $result.note = "Started $ServerPath but nothing was listening on $Port within " +
                               "$TimeoutSeconds s. See $logDir\$Name.err.log"
            }
            else {
                $result.state = 'started'
            }
        }
    }
    elseif ($null -eq $listenerPid) {
        $result.state = 'not-running'
        $result.note = "Nothing listening on $Port and -NoStart was given."
    }
    else {
        $result.state = 'already-running'
    }

    $result.pid = $listenerPid

    if ($null -ne $listenerPid) {
        # The header from `janet mcp-headers`, once for this run, through the helper the other
        # MCP clients share. None is not an error: the probe goes without, and a server that
        # requires one answers 401, reported below.
        $auth = Get-McpAuthHeader
        $authHeader = $auth.Header
        $servedNames = Get-ServedToolName -TcpPort $Port -Budget $TimeoutSeconds -AuthHeader $authHeader
        $declared = Get-DeclaredToolCount -SourceDir $SourceDir

        $result.toolsDeclared = $declared

        if ($servedNames.Count -eq 0 -and $null -ne $script:probeRefusal) {
            # Up and answering, but refusing this client: a different failure from not
            # answering, with a different fix.
            $result.ok = $false
            $result.state = 'unauthorized'
            $result.toolsServed = 0
            $result.note = "Port $Port (pid $listenerPid) refused tools/list with 401: $($script:probeRefusal) " +
                           $(if ($authHeader.Count -eq 0) {
                               "No Authorization header was sent ($($auth.Reason)). " +
                               'Run `janet mcp-token --ensure` to create the token.'
                             } else {
                               'The header from `janet mcp-headers` was sent and refused: the server holds a ' +
                               'different token or none. Check its /health ''auth''.'
                             })
        }
        elseif ($servedNames.Count -eq 0) {
            # Listening but not answering MCP is its own failure, and a worse one than being
            # down: the client connects and finds nothing.
            $result.ok = $false
            $result.state = 'not-answering'
            $result.toolsServed = 0
            $result.note = "Port $Port is held by pid $listenerPid but it did not answer " +
                           "tools/list. Something other than $Name may own the port."
        }
        else {
            $result.toolsServed = $servedNames.Count
            $result.ok = $true
            if ($null -ne $declared -and $servedNames.Count -lt $declared) {
                $result.stale = $true
                $result.ok = $false
                $result.note = "STALE SERVER: serving $($servedNames.Count) tools, source declares " +
                               "$declared. The running build predates the current source ($SourceDir). " +
                               "Rebuild with scripts\Update-McpServer.ps1 -ProcessName $Name " +
                               "-ServerArgument '$($ServerArgument -join "','")'" +
                               $(if ($Name -eq 'janet-mcp') {
                                   " -ToolProject src\Janet.Mcp\Janet.Mcp.csproj,src\Janet.Cli\Janet.Cli.csproj"
                                 } else { '' })
            }
            elseif (-not $result.note) {
                $result.note = "$($servedNames.Count) tools served on $Port by pid $listenerPid."
            }
        }
    }
}
catch {
    # Startup path: capture and report, never propagate (house rule 6, DESIGN-NOTES 8).
    $result.ok = $false
    $result.state = 'error'
    $result.error = $_.Exception.Message
}

$result | ConvertTo-Json -Depth 5 -Compress:(-not $Pretty)
