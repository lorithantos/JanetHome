#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for the MCP clients' bearer header: Get-McpAuthHeader and
    Get-McpAuthRefusal in scripts\JanetCli.Common.ps1, and Invoke-McpTool.ps1 sending the
    header and reporting a 401.

.DESCRIPTION
    No real token and no real server. `janet mcp-headers` is played by .cmd fakes under
    TestDrive, and the 401 by a one-shot TcpListener on a loopback port that records the
    request it was sent. Nothing here reads or writes HKCU\Software\Janet.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test fixture builders: fake executables under TestDrive and a loopback listener, where -WhatIf has no meaning.')]
param()

BeforeAll {
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    . (Join-Path $script:repoRoot 'scripts\JanetCli.Common.ps1')
    $script:mcpTool = Join-Path $script:repoRoot 'scripts\Invoke-McpTool.ps1'
    $script:fakeToken = 'fake-token-' + [guid]::NewGuid().ToString('n')

    function New-FakeJanet {
        param([string]$Name, [string[]]$Lines)
        $path = Join-Path $TestDrive "$Name.cmd"
        Set-Content -LiteralPath $path -Value (@('@echo off') + $Lines) -Encoding ascii
        return $path
    }

    # One request, one canned 401. Returns the job (whose output is the request text), the
    # listener and the URI to dial. The listener is created here so the port is known before
    # the job runs. A connection that closes without sending a request -- Ensure-McpServer's
    # is-anything-listening probe -- is skipped, up to five of them. Always end with
    # Stop-RefusingServer: a script that never dials leaves the job blocked in
    # AcceptTcpClient, which Remove-Job -Force cannot interrupt, and the gate then hung for
    # 50 minutes (2026-10-31). Stopping the listener is what releases the accept.
    function Start-RefusingServer {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseUsingScopeModifierInNewRunspaces', '', Justification = 'The job scriptblock declares $Listener and $Body as its own params, bound by -ArgumentList; $using: would be wrong.')]
        param([string]$Body)
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        $job = Start-ThreadJob -ArgumentList $listener, $Body -ScriptBlock {
            param($Listener, $Body)
            try {
                $request = ''
                $buffer = [byte[]]::new(65536)
                for ($attempt = 0; $attempt -lt 5 -and -not $request; $attempt++) {
                    $client = $Listener.AcceptTcpClient()
                    $stream = $client.GetStream()
                    while ($request -notmatch "`r`n`r`n") {
                        $read = $stream.Read($buffer, 0, $buffer.Length)
                        if ($read -le 0) { break }
                        $request += [System.Text.Encoding]::ASCII.GetString($buffer, 0, $read)
                    }
                    if (-not $request) { $client.Close() }
                }
                if (-not $request) { return }
                # Drain the body so the client is not reset mid-send.
                $headEnd = $request.IndexOf("`r`n`r`n") + 4
                $length = 0
                if ($request -match '(?im)^Content-Length:\s*(\d+)') { $length = [int]$Matches[1] }
                $have = $request.Length - $headEnd
                while ($have -lt $length) {
                    $read = $stream.Read($buffer, 0, $buffer.Length)
                    if ($read -le 0) { break }
                    $have += $read
                }
                $payload = [System.Text.Encoding]::UTF8.GetBytes($Body)
                $head = "HTTP/1.1 401 Unauthorized`r`nContent-Type: text/plain; charset=utf-8`r`n" +
                    "Content-Length: $($payload.Length)`r`nConnection: close`r`n`r`n"
                $headBytes = [System.Text.Encoding]::ASCII.GetBytes($head)
                $stream.Write($headBytes, 0, $headBytes.Length)
                $stream.Write($payload, 0, $payload.Length)
                $stream.Flush()
                $client.Close()
                $request
            }
            catch [System.Net.Sockets.SocketException] {
                # Stop-RefusingServer stopped the listener while it waited: nobody dialled.
                return
            }
            finally { $Listener.Stop() }
        }
        return [pscustomobject]@{ Job = $job; Listener = $listener; Uri = "http://127.0.0.1:$port/" }
    }

    # Waits for the job, then stops the listener BEFORE removing the job, so an accept that
    # nobody satisfied is released rather than waited on forever. Returns the request text,
    # or nothing when the server was never dialled.
    function Stop-RefusingServer {
        param($Server)
        $null = $Server.Job | Wait-Job -Timeout 15
        $Server.Listener.Stop()
        $null = $Server.Job | Wait-Job -Timeout 5
        $request = $Server.Job | Receive-Job -ErrorAction SilentlyContinue
        $Server.Job | Remove-Job -Force
        return $request
    }
}

Describe 'Get-McpAuthHeader' {
    It 'returns the Authorization header janet prints, with no reason' {
        $janet = New-FakeJanet -Name 'ok' -Lines @("echo {`"Authorization`":`"Bearer $script:fakeToken`"}")
        $auth = Get-McpAuthHeader -Janet $janet
        $auth.Header['Authorization'] | Should -Be "Bearer $script:fakeToken"
        $auth.Reason | Should -BeNullOrEmpty
    }

    It 'sends nothing when there is no token, and says why from stderr' {
        $janet = New-FakeJanet -Name 'absent' -Lines @(
            'echo No MCP token at HKCU\Software\Janet.Tests\McpToken. Run janet mcp-token --ensure. 1>&2'
            'exit /b 1')
        $auth = Get-McpAuthHeader -Janet $janet
        $auth.Header.Count | Should -Be 0
        $auth.Reason | Should -Match 'exited 1: No MCP token'
    }

    It 'sends nothing to a janet that predates the verb' {
        $janet = New-FakeJanet -Name 'old' -Lines @("echo Unknown command 'mcp-headers'. 1>&2", 'exit /b 2')
        $auth = Get-McpAuthHeader -Janet $janet
        $auth.Header.Count | Should -Be 0
        $auth.Reason | Should -Match 'exited 2'
    }

    It 'sends nothing for stdout that is not the header object, and does not echo it' {
        $janet = New-FakeJanet -Name 'garbage' -Lines @("echo $script:fakeToken")
        $auth = Get-McpAuthHeader -Janet $janet
        $auth.Header.Count | Should -Be 0
        $auth.Reason | Should -Not -Match $script:fakeToken
    }
}

Describe 'Invoke-McpTool against a server that answers 401' {
    It 'sends the header it was given and reports the refusal without the token' {
        $server = Start-RefusingServer -Body 'Refused: this server requires a bearer token. Run `janet mcp-headers` to print the header to send.'
        $out = & $script:mcpTool -Tool 'graph_summary' -Uri $server.Uri -TimeoutSec 10 `
            -AuthHeader @{ Authorization = "Bearer $script:fakeToken" }
        $code = $LASTEXITCODE
        $request = Stop-RefusingServer $server

        $code | Should -Be 1
        $request | Should -Match "(?im)^Authorization: Bearer $script:fakeToken"
        $text = $out -join "`n"
        $text | Should -Not -Match $script:fakeToken
        $envelope = $text | ConvertFrom-Json
        $envelope.status | Should -Be 401
        $envelope.error | Should -Match 'lack of a valid bearer token \(HTTP 401\)'
        $envelope.error | Should -Match 'Refused: this server requires a bearer token'
        $envelope.error | Should -Match 'janet mcp-token --ensure'
        $envelope.error | Should -Match 'was sent and not accepted'
    }

    It 'Ensure-McpServer reports a 401 as unauthorized, not as not-answering' {
        # -NoStart, and the port is the refusing listener's, so nothing is started and
        # `janet mcp-token --ensure` is never run. The header comes from whatever janet is on
        # PATH, which only reads.
        $server = Start-RefusingServer -Body 'Refused: this server requires a bearer token.'
        $port = ([uri]$server.Uri).Port
        $out = & (Join-Path $script:repoRoot 'scripts\Ensure-McpServer.ps1') -Port $port -NoStart `
            -TimeoutSeconds 10 -SourceDir (Join-Path $TestDrive 'no-source')
        $null = Stop-RefusingServer $server

        $result = ($out -join "`n") | ConvertFrom-Json
        $result.ok | Should -BeFalse
        $result.state | Should -Be 'unauthorized'
        $result.note | Should -Match 'refused tools/list with 401: Refused: this server requires a bearer token'
        $result.note | Should -Match 'janet mcp-token --ensure|janet mcp-headers'
    }

    It 'Ensure-McpServer finds a listener the connection table does not list (NoStart: <NoStart>)' -TestCases @(
        @{ NoStart = $true }
        @{ NoStart = $false }
    ) {
        # On 2026-10-31 this machine's TCP table (Get-NetTCPConnection and netstat alike) listed
        # 7 loopback sockets and omitted janet-mcp and razorgraph while both answered /health.
        # The table is not evidence that a port is free. The mock makes that the case on every
        # machine. A wrong "nothing listening" without -NoStart would launch a duplicate server,
        # and the probe afterwards overwrites the state, so the start is caught by what it
        # leaves behind: the fake server writes a marker, and the fake janet's --ensure answer
        # would land in 'token'. The fake janet also keeps the real HKCU token out of reach,
        # and -Name keeps the start's log files away from the real server's.
        Mock Get-NetTCPConnection { throw 'No matching MSFT_NetTCPConnection objects found' }
        $marker = Join-Path $TestDrive 'fake-server-started.txt'
        $fakeServer = New-FakeJanet -Name 'fake-mcp' -Lines @("echo started> `"$marker`"")
        $fakeBin = Join-Path $TestDrive 'fakebin'
        $null = New-Item -ItemType Directory -Path $fakeBin -Force
        Set-Content -LiteralPath (Join-Path $fakeBin 'janet.cmd') -Encoding ascii -Value @(
            '@echo off', "echo {`"Authorization`":`"Bearer $script:fakeToken`"}")
        $savedPath = $env:PATH
        $server = Start-RefusingServer -Body 'Refused: this server requires a bearer token.'
        try {
            $env:PATH = "$fakeBin;$savedPath"
            $port = ([uri]$server.Uri).Port
            $out = & (Join-Path $script:repoRoot 'scripts\Ensure-McpServer.ps1') -Port $port -NoStart:$NoStart `
                -TimeoutSeconds 5 -SourceDir (Join-Path $TestDrive 'no-source') `
                -Name 'fake-mcp' -ServerPath $fakeServer
        }
        finally {
            $env:PATH = $savedPath
            $request = Stop-RefusingServer $server
        }

        $result = ($out -join "`n") | ConvertFrom-Json
        $result.state | Should -Be 'unauthorized'
        $result.pid | Should -BeNullOrEmpty
        $result.note | Should -Match 'pid unknown'
        $result.token | Should -BeNullOrEmpty
        Test-Path -LiteralPath $marker | Should -BeFalse
        $request | Should -Match '(?m)^POST / HTTP/1\.1'
    }

    It 'says no header was sent when it had none' {
        $server = Start-RefusingServer -Body 'Refused: no Authorization header.'
        $out = & $script:mcpTool -Tool 'graph_summary' -Uri $server.Uri -TimeoutSec 10 -AuthHeader @{}
        $request = Stop-RefusingServer $server

        $request | Should -Not -Match '(?im)^Authorization:'
        $envelope = ($out -join "`n") | ConvertFrom-Json
        $envelope.status | Should -Be 401
        $envelope.error | Should -Match 'No Authorization header was sent'
    }
}
