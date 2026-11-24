#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\Update-McpServer.ps1 that a full run cannot give: a run publishes,
    swaps a junction and restarts a server, so these read the script's AST and run only the
    statements under test.

.DESCRIPTION
    A run without -ProcessName (a client-owned stdio server) did all its work, then threw at
    the very end under Set-StrictMode: "The variable '$carry' cannot be retrieved because it
    has not been set." $carry was assigned only inside 'if ($ProcessName)', and the final
    carry-over report read it unconditionally. server_rotate always passes -ProcessName, so
    only a hand run hit it. The test below runs, in a fresh runspace under the script's own
    StrictMode and ErrorActionPreference, every statement of the locked try body that touches
    $carry, in source order, with -ProcessName unset, and requires the run to reach its end.
#>
param()

Describe 'Update-McpServer.ps1 without -ProcessName' {
    BeforeAll {
        $path = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Update-McpServer.ps1'
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)

        # The try whose finally releases the rotation lock holds the whole rotation.
        $tryAst = @($ast.FindAll({
                    param($n)
                    $n -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $n.Finally -and
                    $n.Finally.Extent.Text -match 'Exit-RotationLock'
                }, $true))[0]

        $script:carryStatements = @($tryAst.Body.Statements | Where-Object {
                $null -ne $_.Find({
                        param($n)
                        $n -is [System.Management.Automation.Language.VariableExpressionAst] -and
                        $n.VariablePath.UserPath -eq 'carry'
                    }, $true)
            })
    }

    It 'selects the -ProcessName branch and the carry-over report, so the run below exercises them' {
        $texts = @($script:carryStatements | ForEach-Object { $_.Extent.Text })
        @($texts | Where-Object { $_ -match '^if \(\$ProcessName\)' }).Count | Should -Be 1
        @($texts | Where-Object { $_ -match 'Format-GraphCarryOverReport' }).Count | Should -Be 1
    }

    It 'reaches the end of the run with no carry-over, and reports none' {
        # A fresh runspace, so no $carry from Pester's own scopes can stand in for an unset one.
        $body = @(
            'Set-StrictMode -Version Latest'
            '$ErrorActionPreference = ''Stop'''
            '$ProcessName = '''''
            @($script:carryStatements | ForEach-Object { $_.Extent.Text })
            '''reached the end'''
        ) -join [Environment]::NewLine

        $ps = [powershell]::Create()
        try {
            $thrown = $null
            $output = $null
            try { $output = $ps.AddScript($body).Invoke() }
            catch { $thrown = $_.Exception.InnerException.Message ?? $_.Exception.Message }
            $hostLines = @($ps.Streams.Information | ForEach-Object { [string]$_.MessageData })
        }
        finally { $ps.Dispose() }

        $thrown | Should -BeNullOrEmpty
        @($output) | Should -Contain 'reached the end'
        @($hostLines | Where-Object { $_ -like '*no -ProcessName: the client owns this server*' }).Count | Should -Be 1
        @($hostLines | Where-Object { $_ -like '*carry-over*' }).Count | Should -Be 0
    }
}
