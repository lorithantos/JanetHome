#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for "listening is not ready": scripts\McpReadiness.Common.ps1, and the two
    callers that wait on it -- Ensure-McpServer.ps1's tools/list probe and
    Update-McpServer.ps1's listener wait.

.DESCRIPTION
    Since 2026-11-24 janet-mcp binds first and answers 503 until its startup work resolves:
    /health with its normal body plus "state": "starting", every other route with Retry-After.
    No real server is started here. The common functions run against a mocked
    Invoke-WebRequest; Ensure-McpServer.ps1 runs -NoStart against a loopback TcpListener that
    plays a starting server; Update-McpServer.ps1's listener wait is lifted out of the script by
    its AST and run in a fresh runspace with a fake clock, a fake listener pid and a fake
    /health, so nothing is published, stopped or started.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test fixture builders: a loopback listener and fakes, where -WhatIf has no meaning.')]
param()

BeforeAll {
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:common = Join-Path $script:repoRoot 'scripts\McpReadiness.Common.ps1'
    . $script:common

    function New-HealthAnswer {
        param([int]$Status, [string]$Content)
        return [pscustomobject]@{ StatusCode = $Status; Content = $Content }
    }

    $script:startingBody = '{"server":"razorgraph-mcp","graph":"x","version":"1","auth":null,"state":"starting"}'
    $script:readyBody = '{"server":"janet-mcp","graph":"x","version":"1","auth":null,"state":"ready"}'

    # A loopback listener that answers like a server that has bound but not finished starting
    # for its first -StartingRequests requests (-1: forever), then like a ready MCP server with
    # two tools. A connection that sends nothing -- Ensure-McpServer's is-anything-listening
    # probe -- is not a request. The job's output is one line per request, '<request line>
    # <status>'. End with Stop-StartingServer, which stops the listener to release the accept.
    function Start-StartingServer {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseUsingScopeModifierInNewRunspaces', '', Justification = 'The job scriptblock declares its own params, bound by -ArgumentList; $using: would be wrong.')]
        param([int]$StartingRequests)
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        $job = Start-ThreadJob -ArgumentList $listener, $StartingRequests -ScriptBlock {
            param($Listener, $StartingRequests)

            function Read-Request {
                param($Stream)
                $buffer = [byte[]]::new(65536)
                $text = ''
                while ($text -notmatch "`r`n`r`n") {
                    $read = $Stream.Read($buffer, 0, $buffer.Length)
                    if ($read -le 0) { return $text }
                    $text += [System.Text.Encoding]::ASCII.GetString($buffer, 0, $read)
                }
                $headEnd = $text.IndexOf("`r`n`r`n") + 4
                $length = 0
                if ($text -match '(?im)^Content-Length:\s*(\d+)') { $length = [int]$Matches[1] }
                while (($text.Length - $headEnd) -lt $length) {
                    $read = $Stream.Read($buffer, 0, $buffer.Length)
                    if ($read -le 0) { break }
                    $text += [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)
                }
                return $text
            }

            function Get-Answer {
                # @(status, extra header lines, content type, body)
                param([string]$Line, [string]$Body, [bool]$Starting)
                $health = $Line -match '^GET /health '
                $method = ''
                if ($Body -match '"method"\s*:\s*"([^"]+)"') { $method = $Matches[1] }
                if ($Starting -and $health) {
                    return @(503, '', 'application/json', '{"server":"fake-mcp","graph":"x","version":"1","auth":null,"state":"starting"}')
                }
                if ($Starting) { return @(503, "Retry-After: 1`r`n", 'text/plain', 'fake-mcp is starting.') }
                if ($health) { return @(200, '', 'application/json', '{"server":"fake-mcp","graph":"x","version":"1","auth":null,"state":"ready"}') }
                if ($method -eq 'initialize') {
                    return @(200, "Mcp-Session-Id: s1`r`n", 'application/json', '{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"fake-mcp","version":"1"}}}')
                }
                if ($method -eq 'notifications/initialized') { return @(202, '', 'application/json', '') }
                if ($method -eq 'tools/list') {
                    return @(200, '', 'application/json', '{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"one"},{"name":"two"}]}}')
                }
                return @(404, '', 'text/plain', 'no such route')
            }

            $served = 0
            try {
                while ($true) {
                    $client = $Listener.AcceptTcpClient()
                    try {
                        $stream = $client.GetStream()
                        $request = Read-Request -Stream $stream
                        if (-not $request) { continue }
                        $served++
                        $line = ($request -split "`r`n")[0]
                        $body = $request.Substring($request.IndexOf("`r`n`r`n") + 4)
                        $answer = Get-Answer -Line $line -Body $body -Starting ($StartingRequests -lt 0 -or $served -le $StartingRequests)
                        $payload = [System.Text.Encoding]::UTF8.GetBytes([string]$answer[3])
                        $head = "HTTP/1.1 $($answer[0]) X`r`n$($answer[1])Content-Type: $($answer[2]); charset=utf-8`r`n" +
                            "Content-Length: $($payload.Length)`r`nConnection: close`r`n`r`n"
                        $headBytes = [System.Text.Encoding]::ASCII.GetBytes($head)
                        $stream.Write($headBytes, 0, $headBytes.Length)
                        $stream.Write($payload, 0, $payload.Length)
                        $stream.Flush()
                        "$line $($answer[0])"
                    }
                    finally { $client.Close() }
                }
            }
            catch [System.Net.Sockets.SocketException] { return }
            catch [System.InvalidOperationException] { return }
            finally { $Listener.Stop() }
        }
        return [pscustomobject]@{ Job = $job; Listener = $listener; Port = $port }
    }

    function Stop-StartingServer {
        param($Server)
        $Server.Listener.Stop()
        $null = $Server.Job | Wait-Job -Timeout 10
        $log = @($Server.Job | Receive-Job -ErrorAction SilentlyContinue)
        $Server.Job | Remove-Job -Force
        return , $log
    }

    # Ensure-McpServer.ps1 -NoStart against a port, with a fake janet first on PATH so the real
    # HKCU token is never read, and no source directory so no tool count is compared.
    function Invoke-EnsureAgainst {
        param([int]$Port, [int]$TimeoutSeconds)
        $fakeBin = Join-Path $TestDrive 'fakebin'
        $null = New-Item -ItemType Directory -Path $fakeBin -Force
        Set-Content -LiteralPath (Join-Path $fakeBin 'janet.cmd') -Encoding ascii -Value @(
            '@echo off', 'echo {"Authorization":"Bearer fake-readiness-token"}')
        $savedPath = $env:PATH
        try {
            $env:PATH = "$fakeBin;$savedPath"
            $out = & (Join-Path $script:repoRoot 'scripts\Ensure-McpServer.ps1') -Port $Port -NoStart `
                -TimeoutSeconds $TimeoutSeconds -SourceDir (Join-Path $TestDrive 'no-source') -Name 'fake-mcp'
        }
        finally { $env:PATH = $savedPath }
        return ($out -join "`n") | ConvertFrom-Json
    }
}

Describe 'Get-McpHealthState' {
    It 'reads a 503 "starting" body as starting, and names the server' {
        Mock Invoke-WebRequest { New-HealthAnswer -Status 503 -Content $script:startingBody }
        $state = Get-McpHealthState -Port 1
        $state.State | Should -Be 'starting'
        $state.Status | Should -Be 503
        $state.Server | Should -Be 'razorgraph-mcp'
    }

    It 'reads a 200 as ready, with or without a state field' -TestCases @(
        @{ Content = '{"server":"janet-mcp","graph":"x","version":"1","auth":null,"state":"ready"}' }
        @{ Content = '{"server":"janet-mcp","graph":"x","version":"1"}' }
    ) {
        Mock Invoke-WebRequest { New-HealthAnswer -Status 200 -Content $Content }
        (Get-McpHealthState -Port 1).State | Should -Be 'ready'
    }

    It 'reads any other answer as other, not starting' -TestCases @(
        @{ Status = 503; Content = 'Service Unavailable' }
        @{ Status = 503; Content = '{"server":"janet-mcp","state":"ready"}' }
        @{ Status = 404; Content = 'not found' }
    ) {
        Mock Invoke-WebRequest { New-HealthAnswer -Status $Status -Content $Content }
        (Get-McpHealthState -Port 1).State | Should -Be 'other'
    }

    It 'reads no answer as none, without throwing' {
        Mock Invoke-WebRequest { throw [System.Net.Http.HttpRequestException]::new('No connection could be made') }
        $state = Get-McpHealthState -Port 1
        $state.State | Should -Be 'none'
        $state.Status | Should -BeNullOrEmpty
    }

    It 'asks /health without failing on the status' {
        Mock Invoke-WebRequest { New-HealthAnswer -Status 503 -Content $script:startingBody }
        $null = Get-McpHealthState -Port 4321
        Should -Invoke Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
            $Uri -eq 'http://127.0.0.1:4321/health' -and $SkipHttpErrorCheck
        }
    }
}

Describe 'Wait-McpServerReady' {
    BeforeEach {
        Mock Start-Sleep { }
        $script:answers = [System.Collections.Generic.Queue[object]]::new()
        Mock Invoke-WebRequest {
            $next = if ($script:answers.Count -gt 1) { $script:answers.Dequeue() } else { $script:answers.Peek() }
            if ($null -eq $next) { throw [System.Net.Http.HttpRequestException]::new('refused') }
            $next
        }
    }

    It 'waits through starting until ready' {
        $script:answers.Enqueue((New-HealthAnswer -Status 503 -Content $script:startingBody))
        $script:answers.Enqueue((New-HealthAnswer -Status 503 -Content $script:startingBody))
        $script:answers.Enqueue((New-HealthAnswer -Status 200 -Content $script:readyBody))
        $result = Wait-McpServerReady -Port 1 -Deadline (Get-Date).AddSeconds(5)
        $result.State | Should -Be 'ready'
        $result.Exited | Should -BeFalse
        Should -Invoke Invoke-WebRequest -Times 3 -Exactly
        Test-McpServerReady -Readiness $result | Should -BeTrue
    }

    It 'gives up at the deadline still starting, and that is not ready' {
        $script:answers.Enqueue((New-HealthAnswer -Status 503 -Content $script:startingBody))
        $result = Wait-McpServerReady -Port 1 -Deadline (Get-Date).AddSeconds(-1)
        $result.State | Should -Be 'starting'
        $result.Exited | Should -BeFalse
        Test-McpServerReady -Readiness $result | Should -BeFalse
    }

    It 'stops when the process it is waiting on exits' {
        $script:answers.Enqueue((New-HealthAnswer -Status 503 -Content $script:startingBody))
        $result = Wait-McpServerReady -Port 1 -Deadline (Get-Date).AddSeconds(5) -Process ([pscustomobject]@{ HasExited = $true })
        $result.Exited | Should -BeTrue
        Test-McpServerReady -Readiness $result | Should -BeFalse
    }

    It 'returns at once on an answer that is not about readiness' {
        # A short deadline: sleeps are mocked, so a wait that wrongly looped would spin until it.
        $script:answers.Enqueue((New-HealthAnswer -Status 404 -Content 'not found'))
        $result = Wait-McpServerReady -Port 1 -Deadline (Get-Date).AddSeconds(2)
        $result.State | Should -Be 'other'
        Should -Invoke Invoke-WebRequest -Times 1 -Exactly
        Test-McpServerReady -Readiness $result | Should -BeTrue
        Format-McpReadiness -Readiness $result -Port 7718 | Should -Match '^listening on 7718 \(/health answered 404'
    }

    It 'names a ready server as ready, not merely listening' {
        Format-McpReadiness -Readiness ([pscustomobject]@{ State = 'ready'; Status = 200; Exited = $false }) -Port 7717 |
            Should -Be 'ready on 7717'
    }
}

Describe 'Ensure-McpServer.ps1 against a server that is still starting' {
    It 'reports starting, not not-answering, when it stays starting' {
        $server = Start-StartingServer -StartingRequests -1
        try { $result = Invoke-EnsureAgainst -Port $server.Port -TimeoutSeconds 1 }
        finally { $log = Stop-StartingServer $server }

        $result.ok | Should -BeFalse
        $result.state | Should -Be 'starting'
        $result.note | Should -Match 'still starting'
        @($log | Where-Object { $_ -match '^GET /health ' }).Count | Should -BeGreaterThan 0
    }

    It 'waits for it to finish starting and then lists its tools' {
        # Request 1 is the initialize, answered 503; 2 and 3 are /health saying starting; from 4
        # on it is ready, so /health, then initialize, initialized and tools/list succeed.
        $server = Start-StartingServer -StartingRequests 3
        try { $result = Invoke-EnsureAgainst -Port $server.Port -TimeoutSeconds 10 }
        finally { $log = Stop-StartingServer $server }

        $result.state | Should -Be 'already-running'
        $result.ok | Should -BeTrue
        $result.toolsServed | Should -Be 2
        $log[0] | Should -Match '^POST / .* 503$'
        @($log | Where-Object { $_ -match '^GET /health .* 200$' }).Count | Should -Be 1
    }
}

Describe "Update-McpServer.ps1's listener wait" {
    BeforeAll {
        $path = Join-Path $script:repoRoot 'scripts\Update-McpServer.ps1'
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
        $script:waitBlock = @($ast.FindAll({
                    param($n)
                    $n -is [System.Management.Automation.Language.IfStatementAst] -and
                    $n.Extent.Text.StartsWith('if ($null -eq $port)')
                }, $true))[0].Extent.Text

        # Runs the block in a fresh runspace under the script's own StrictMode and
        # ErrorActionPreference. The clock advances 100 ms per read and sleeps are free, so the
        # 15 s budget costs nothing. The listener pid always matches. /health answers from
        # $Answers in order, repeating the last; 'exit' as an answer makes the process exit.
        function Invoke-WaitBlock {
            param([string[]]$Answers)
            $answerList = ($Answers | ForEach-Object { "'$_'" }) -join ', '
            $body = @(
                'Set-StrictMode -Version Latest'
                '$ErrorActionPreference = ''Stop'''
                ". '$script:common'"
                '$script:clock = [datetime]''2026-11-24T12:00:00'''
                'function Get-Date { $script:clock = $script:clock.AddMilliseconds(100); return $script:clock }'
                'function Start-Sleep { param([int]$Milliseconds) }'
                'function Get-ListenerPid { param([int]$TcpPort) return 4242 }'
                'function Invoke-GraphCarryOverRestore { param($State) }'
                '$script:exited = $false'
                "`$script:answers = [System.Collections.Generic.Queue[string]]::new([string[]]@($answerList))"
                'function Invoke-WebRequest {'
                '    [CmdletBinding()] param($Uri, $Method, $TimeoutSec, [switch]$SkipHttpErrorCheck)'
                '    $next = if ($script:answers.Count -gt 1) { $script:answers.Dequeue() } else { $script:answers.Peek() }'
                '    if ($next -eq ''exit'') { $script:exited = $true; throw ''connection refused'' }'
                '    if ($next -eq ''starting'') { return [pscustomobject]@{ StatusCode = 503; Content = ''{"server":"razorgraph-mcp","state":"starting"}'' } }'
                '    return [pscustomobject]@{ StatusCode = 200; Content = ''{"server":"razorgraph-mcp","state":"ready"}'' }'
                '}'
                '$started = [pscustomobject]@{ Id = 4242; ExitCode = 4 }'
                '$started | Add-Member -MemberType ScriptProperty -Name HasExited -Value { $script:exited }'
                '$port = 7799; $carry = $null; $logDir = ''LOGDIR''; $currentLink = ''CURRENT''; $ProcessName = ''fake-mcp'''
                $script:waitBlock
                '''reached the end'''
            ) -join [Environment]::NewLine

            $ps = [powershell]::Create()
            try {
                $thrown = $null
                $output = $null
                try { $output = $ps.AddScript($body).Invoke() }
                catch { $thrown = $_.Exception.InnerException.Message ?? $_.Exception.Message }
                return [pscustomobject]@{
                    Thrown   = $thrown
                    Output   = @($output)
                    Host     = @($ps.Streams.Information | ForEach-Object { [string]$_.MessageData })
                    Warnings = @($ps.Streams.Warning | ForEach-Object { [string]$_.Message })
                }
            }
            finally { $ps.Dispose() }
        }
    }

    It 'found the block' {
        $script:waitBlock | Should -Match 'Wait-McpServerReady'
    }

    It 'says ready only once /health says ready' {
        $run = Invoke-WaitBlock -Answers 'starting', 'starting', 'ready'
        $run.Thrown | Should -BeNullOrEmpty
        $run.Output | Should -Contain 'reached the end'
        @($run.Host | Where-Object { $_ -like '*started pid 4242 from current, ready on 7799*' }).Count | Should -Be 1
        $run.Warnings.Count | Should -Be 0
    }

    It 'does not report a server that is only starting as ready, and warns instead' {
        $run = Invoke-WaitBlock -Answers 'starting'
        $run.Thrown | Should -BeNullOrEmpty
        @($run.Host | Where-Object { $_ -like '*started pid*' }).Count | Should -Be 0
        @($run.Warnings | Where-Object { $_ -like '*listening on 7799 but is not ready after 15 s (/health: starting)*' }).Count | Should -Be 1
    }

    It 'throws when the server exits after binding but before it is ready' {
        $run = Invoke-WaitBlock -Answers 'starting', 'exit'
        $run.Thrown | Should -Match 'exited \(code 4\) after binding 7799, before it finished starting'
    }
}
