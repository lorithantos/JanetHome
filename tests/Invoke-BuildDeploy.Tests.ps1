#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for the verify probe in scripts\Invoke-BuildDeploy.ps1 -- a probe
    that gets no HTTP response is warming, not a failed deploy.

.DESCRIPTION
    The script is one top-level pipeline that deploys when it runs, so it cannot
    be dot-sourced. These tests lift the verify loop's own try/catch out of the
    script's AST, together with Get-Prop, and run that text under StrictMode
    against real requests: a refused connection, and a local HttpListener that
    answers with the status named in the path. They run the script's text, not a
    copy of it, so an edit to the catch is what they test.

    The load-bearing case is 'refused'. Until 2026-10-17 the catch read
    $_.Exception.Response bare, and an exception with no Response (a timeout, or
    a connection refused while the site restarts) threw "The property 'Response'
    cannot be found on this object" out of the catch. That ended the loop, and a
    deploy that had landed was reported as DEPLOYMENT FAILED. It happened to the
    Neelam test deploys of 2026-10-03 and 2026-10-09.
#>

BeforeAll {
    Set-StrictMode -Version Latest
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $scriptPath = Join-Path $script:repoRoot 'scripts\Invoke-BuildDeploy.ps1'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$null)

    $getProp = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-Prop'
        }, $true)
    . ([scriptblock]::Create($getProp.Extent.Text))

    # The innermost try whose body makes the probe request.
    $tries = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.TryStatementAst] -and
            $node.Body.Extent.Text -match 'Invoke-WebRequest -Uri \$verifyUrl'
        }, $true)
    $script:probe = [scriptblock]::Create(($tries | Sort-Object { $_.Extent.Text.Length } | Select-Object -First 1).Extent.Text)

    $script:port = Get-Random -Minimum 20000 -Maximum 40000
    $script:listener = [System.Net.HttpListener]::new()
    $script:listener.Prefixes.Add("http://127.0.0.1:$script:port/")
    $script:listener.Start()
    $script:server = [powershell]::Create()
    [void]$script:server.AddScript({
            param($listening)
            while ($listening.IsListening) {
                try { $context = $listening.GetContext() } catch { break }
                $context.Response.StatusCode = [int]$context.Request.Url.AbsolutePath.Trim('/')
                $context.Response.Close()
            }
        }).AddArgument($script:listener)
    $script:serverHandle = $script:server.BeginInvoke()

    function Invoke-Probe {
        # Runs the lifted try/catch in this scope with the loop's variables set,
        # and returns what it wrote plus whether it marked the site healthy.
        param([string]$Url)
        $verifyUrl = $Url
        $attempt = 1
        $attempts = 12
        $warming = @(503)
        $healthy = $false
        $output = . $script:probe 6>&1 | Out-String
        [pscustomobject]@{ Output = $output.Trim(); Healthy = $healthy }
    }
}

AfterAll {
    $script:listener.Stop()
    $script:listener.Close()
    [void]$script:server.EndInvoke($script:serverHandle)
    $script:server.Dispose()
}

Describe 'Invoke-BuildDeploy verify probe' {
    It 'treats a refused connection as no response yet, without throwing' {
        $result = Invoke-Probe -Url 'http://127.0.0.1:1/'
        $result.Output | Should -Match 'no response yet'
        $result.Healthy | Should -BeFalse
    }

    It 'reports a warming status as warming up' {
        $result = Invoke-Probe -Url "http://127.0.0.1:$script:port/503"
        $result.Output | Should -Match 'warming up \(503\)'
        $result.Healthy | Should -BeFalse
    }

    It 'reports any other status by number' {
        $result = Invoke-Probe -Url "http://127.0.0.1:$script:port/500"
        $result.Output | Should -Match 'HTTP 500'
        $result.Healthy | Should -BeFalse
    }

    It 'marks a 200 healthy' {
        $result = Invoke-Probe -Url "http://127.0.0.1:$script:port/200"
        $result.Output | Should -Match 'Healthy \(HTTP 200\)'
        $result.Healthy | Should -BeTrue
    }
}
