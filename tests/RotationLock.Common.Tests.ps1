#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\RotationLock.Common.ps1 -- the lock that makes MCP server
    rotations run one at a time across both servers.

.DESCRIPTION
    On 2026-11-24 a janet rotation and a razorgraph rotation overlapped: janet's CLI
    reinstall broke razorgraph's carry-over. Update-McpServer.ps1 now holds this lock for its
    whole run. These tests take the lock directly against a TestDrive path and never run a
    rotation: a second taker waits while the first holds it, goes ahead when it lets go,
    gives up naming the holder when it never does, and finds it free after a holder that
    threw -- in this process through the try/finally the rotation uses, and in a child
    process that died holding it. One more pins that a process started while the lock is
    held (the rotation starts the new server) does not inherit it, and the last reads
    Update-McpServer.ps1's AST to hold the wiring: taken before the publish, released in a
    finally that covers everything through the CLI reinstall.
#>
param()

BeforeAll {
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:helper = Join-Path $script:repoRoot 'scripts\RotationLock.Common.ps1'
    . $script:helper
}

Describe 'Enter-RotationLock' {
    BeforeEach {
        $script:lockPath = Join-Path $TestDrive ("rotation-{0}.lock" -f [guid]::NewGuid().ToString('N'))
        $script:open = [System.Collections.Generic.List[object]]::new()
        $script:logged = [System.Collections.Generic.List[string]]::new()
        $script:log = { param($line) $script:logged.Add($line) }
    }

    AfterEach {
        foreach ($s in $script:open) { Exit-RotationLock -Lock $s }
    }

    It 'takes a free lock at once, records the holder, and logs no wait' {
        $first = Enter-RotationLock -Path $script:lockPath -Holder 'janet-mcp rotation (pid 1)' -TimeoutSeconds 1 -Log $script:log
        $script:open.Add($first)

        $first | Should -BeOfType [System.IO.FileStream]
        Get-RotationLockHolder -Path $script:lockPath | Should -Be 'janet-mcp rotation (pid 1)'
        $script:logged.Count | Should -Be 0
    }

    It 'makes a second taker wait while the first holds it, and go ahead once it lets go' {
        $first = Enter-RotationLock -Path $script:lockPath -Holder 'janet-mcp rotation (pid 1)' -TimeoutSeconds 1
        $script:open.Add($first)

        # The holder lets go 1.5 s from now, from another thread, the way the other rotation
        # finishing would.
        $release = Start-ThreadJob -ScriptBlock {
            . $using:helper
            Start-Sleep -Milliseconds 1500
            Exit-RotationLock -Lock $using:first
        }

        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $second = Enter-RotationLock -Path $script:lockPath -Holder 'RazorGraph.Mcp rotation (pid 2)' -TimeoutSeconds 20 -PollMilliseconds 50 -Log $script:log
        $clock.Stop()
        $script:open.Add($second)
        $release | Wait-Job | Receive-Job
        $release | Remove-Job

        $clock.Elapsed.TotalSeconds | Should -BeGreaterThan 1.2
        $clock.Elapsed.TotalSeconds | Should -BeLessThan 15
        $second | Should -BeOfType [System.IO.FileStream]
        Get-RotationLockHolder -Path $script:lockPath | Should -Be 'RazorGraph.Mcp rotation (pid 2)'

        # One line, however many polls, and it names the rotation in the way.
        $script:logged.Count | Should -Be 1
        $script:logged[0] | Should -BeLike 'waiting for the rotation lock, held by janet-mcp rotation (pid 1); up to 20 s'
    }

    It 'gives up after the timeout, naming the holder, when the holder never lets go' {
        $first = Enter-RotationLock -Path $script:lockPath -Holder 'janet-mcp rotation (pid 1, since 2026-11-24T19:14:07Z)' -TimeoutSeconds 1
        $script:open.Add($first)

        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $thrown = $null
        try { $null = Enter-RotationLock -Path $script:lockPath -Holder 'RazorGraph.Mcp rotation (pid 2)' -TimeoutSeconds 1 -PollMilliseconds 50 -Log $script:log }
        catch { $thrown = $_.Exception.Message }
        $clock.Stop()

        $thrown | Should -Not -BeNullOrEmpty
        $thrown | Should -BeLike "Gave up after 1 s waiting for the rotation lock *, held by janet-mcp rotation (pid 1, since 2026-11-24T19:14:07Z).*"
        $thrown | Should -BeLike '*Nothing was published, swapped or restarted.*'
        $clock.Elapsed.TotalSeconds | Should -BeGreaterOrEqual 1
        # The holder is untouched: giving up must not take or clear someone else's lock.
        Get-RotationLockHolder -Path $script:lockPath | Should -Be 'janet-mcp rotation (pid 1, since 2026-11-24T19:14:07Z)'
    }

    It 'says so when the holder recorded no name' {
        # Held the way a holder is between its open and its write.
        $bare = [System.IO.FileStream]::new($script:lockPath, [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
        $script:open.Add($bare)

        { Enter-RotationLock -Path $script:lockPath -Holder 'x' -TimeoutSeconds 0 -Log $script:log } |
            Should -Throw '*held by a process that did not record itself*'
    }

    It 'is free again after a holder that threw inside the try/finally the rotation uses' {
        $rotate = {
            $lock = Enter-RotationLock -Path $script:lockPath -Holder 'janet-mcp rotation (pid 1)' -TimeoutSeconds 1
            try { throw 'publish failed' }
            finally { Exit-RotationLock -Lock $lock }
        }
        $rotate | Should -Throw 'publish failed'

        $next = Enter-RotationLock -Path $script:lockPath -Holder 'RazorGraph.Mcp rotation (pid 2)' -TimeoutSeconds 1 -Log $script:log
        $script:open.Add($next)
        $script:logged.Count | Should -Be 0
        Get-RotationLockHolder -Path $script:lockPath | Should -Be 'RazorGraph.Mcp rotation (pid 2)'
    }

    It 'is free again after a holder process that threw and died with no finally at all' {
        $child = Join-Path $TestDrive 'Hold-AndThrow.ps1'
        @'
param([string]$Helper, [string]$LockPath)
$ErrorActionPreference = 'Stop'
. $Helper
$held = Enter-RotationLock -Path $LockPath -Holder "child rotation (pid $PID)" -TimeoutSeconds 5
throw "died holding $($held.Name)"
'@ | Set-Content -LiteralPath $child -Encoding utf8

        $output = & pwsh -NoProfile -NonInteractive -File $child -Helper $script:helper -LockPath $script:lockPath 2>&1
        $LASTEXITCODE | Should -Not -Be 0 -Because "the child must have thrown: $output"
        # It really held the lock: its name is still in the file, which only a holder writes.
        Get-RotationLockHolder -Path $script:lockPath | Should -BeLike 'child rotation (pid *)'

        $next = Enter-RotationLock -Path $script:lockPath -Holder 'janet-mcp rotation (pid 3)' -TimeoutSeconds 1 -Log $script:log
        $script:open.Add($next)
        $script:logged.Count | Should -Be 0
    }

    It 'is not inherited by a process started while it is held' {
        # Update-McpServer.ps1 starts the new server with redirected output while holding the
        # lock, and redirection makes Start-Process inherit every INHERITABLE handle. If the
        # lock were one, the server would hold it for life and every later rotation would time out.
        $first = Enter-RotationLock -Path $script:lockPath -Holder 'janet-mcp rotation (pid 1)' -TimeoutSeconds 1
        $script:open.Add($first)
        $launch = @{
            FilePath               = 'pwsh'
            ArgumentList           = '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 4'
            PassThru               = $true
            WindowStyle            = 'Hidden'
            RedirectStandardOutput = Join-Path $TestDrive 'server.out.log'
            RedirectStandardError  = Join-Path $TestDrive 'server.err.log'
        }
        $server = Start-Process @launch
        try {
            Start-Sleep -Milliseconds 500
            Exit-RotationLock -Lock $first

            $next = Enter-RotationLock -Path $script:lockPath -Holder 'RazorGraph.Mcp rotation (pid 2)' -TimeoutSeconds 1 -Log $script:log
            $script:open.Add($next)
            $script:logged.Count | Should -Be 0
            $server.HasExited | Should -BeFalse -Because 'the started process must still be alive for this to prove anything'
        }
        finally { $server.WaitForExit() }
    }
}

Describe 'Update-McpServer.ps1 holds the rotation lock for its whole run' {
    BeforeAll {
        $path = Join-Path $script:repoRoot 'scripts\Update-McpServer.ps1'
        $script:ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
        $script:commands = @($script:ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))

        function Select-CommandUse {
            param([string]$Name, [string]$Containing)
            return @($script:commands | Where-Object {
                    $_.GetCommandName() -eq $Name -and (-not $Containing -or $_.Extent.Text -like "*$Containing*")
                })
        }
    }

    It 'takes the lock once, and releases it in the finally of the try that follows' {
        $enter = @(Select-CommandUse 'Enter-RotationLock')
        $enter.Count | Should -Be 1

        $tries = @($script:ast.FindAll({
                    param($n)
                    $n -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $n.Finally -and
                    $n.Finally.Extent.Text -match 'Exit-RotationLock\s+-Lock\s+\$rotationLock'
                }, $true))
        $tries.Count | Should -Be 1

        $enter[0].Extent.StartOffset | Should -BeLessThan $tries[0].Extent.StartOffset
        # Command -> pipeline -> assignment: the handle the finally releases is this one.
        $enter[0].Parent.Parent.Extent.Text | Should -BeLike '$rotationLock = Enter-RotationLock*'
    }

    It 'covers the <Step> inside that try' -ForEach @(
        @{ Step = 'publish'; Name = 'dotnet'; Containing = 'publish' }
        @{ Step = 'junction swap'; Name = 'New-Item'; Containing = 'Junction' }
        @{ Step = 'stop'; Name = 'Stop-Process'; Containing = '' }
        @{ Step = 'restart'; Name = 'Start-Process'; Containing = '' }
        @{ Step = 'carry-over save'; Name = 'Save-GraphCarryOver'; Containing = '' }
        @{ Step = 'carry-over load'; Name = 'Invoke-GraphCarryOverRestore'; Containing = '' }
        @{ Step = 'CLI reinstall'; Name = 'dotnet'; Containing = 'tool install' }
        @{ Step = 'carry-over report'; Name = 'Format-GraphCarryOverReport'; Containing = '' }
    ) {
        $tryAst = @($script:ast.FindAll({
                    param($n)
                    $n -is [System.Management.Automation.Language.TryStatementAst] -and $null -ne $n.Finally -and
                    $n.Finally.Extent.Text -match 'Exit-RotationLock'
                }, $true))[0]
        $tryAst | Should -Not -BeNullOrEmpty -Because 'some try must release the lock in its finally'
        $body = $tryAst.Body.Extent

        $found = @(Select-CommandUse $Name $Containing)
        $found.Count | Should -BeGreaterThan 0
        foreach ($c in $found) {
            $c.Extent.StartOffset | Should -BeGreaterThan $body.StartOffset -Because "$($c.Extent.Text) must run under the lock"
            $c.Extent.EndOffset | Should -BeLessThan $body.EndOffset -Because "$($c.Extent.Text) must run under the lock"
        }
    }
}
