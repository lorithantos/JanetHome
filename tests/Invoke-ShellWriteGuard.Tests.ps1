#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\Invoke-ShellWriteGuard.ps1 -- which shell writes it refuses,
    and the commands it must never get in the way of.

.DESCRIPTION
    Every Write|Edit hook here was routed around by a shell write until 2026-10-23: a
    sed -i on a script, a heredoc onto a new one, a Set-Content onto research.json. The
    deny cases are those shapes, one per writer; the pass cases are the boundary the
    decision drew -- git, dotnet and the janet CLI, reads, and writes outside a repo.

    Each test drives the script with -InputJson, as the other guard tests do. A deny is
    asserted on permissionDecision in the envelope, never on the exit code: the script
    exits 0 on every path, including when it has crashed.
#>

BeforeAll {
    $script:guard = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Invoke-ShellWriteGuard.ps1'

    # A git working tree holding a script and a catalog; TestDrive sits under %TEMP%,
    # outside any repo, so plainDir is the scratchpad's stand-in.
    $script:repo = Join-Path $TestDrive 'repo'
    New-Item -ItemType Directory -Path (Join-Path $script:repo 'scripts') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $script:repo 'scripts\Foo.ps1') -Value 'Write-Output 1' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $script:repo 'research.json') -Value '{}' -Encoding utf8
    & git -C $script:repo init -q
    $script:plainDir = Join-Path $TestDrive 'plain'
    New-Item -ItemType Directory -Path $script:plainDir | Out-Null

    function Invoke-GuardRaw {
        # The parsed hookSpecificOutput, or $null for silence.
        [CmdletBinding()]
        param([string]$Command, [string]$ToolName = 'Bash', [string]$Cwd = $script:repo, [string]$Raw)
        $json = if ($Raw) { $Raw } else { @{ tool_name = $ToolName; tool_input = @{ command = $Command }; cwd = $Cwd } | ConvertTo-Json -Compress }
        $out = & $script:guard -InputJson $json 2>&1 | Out-String
        if ([string]::IsNullOrWhiteSpace($out)) { return $null }
        return ($out | ConvertFrom-Json).hookSpecificOutput
    }

    function Invoke-Guard {
        # Answers 'deny' or 'pass'.
        [CmdletBinding()]
        param([string]$Command, [string]$ToolName = 'Bash', [string]$Cwd = $script:repo)
        $output = Invoke-GuardRaw @PSBoundParameters
        if ($null -ne $output -and $output.PSObject.Properties.Name -contains 'permissionDecision' -and
            $output.permissionDecision -eq 'deny') { return 'deny' }
        return 'pass'
    }
}

Describe 'Invoke-ShellWriteGuard' {

    Context 'a Bash write to a guarded file in a repo is refused' {
        It 'refuses <name>' -ForEach @(
            @{ name = 'sed -i'; command = 'sed -i s/1/2/ scripts/Foo.ps1' }
            @{ name = 'sed -i with a suffix and -e'; command = "sed -i.bak -e 's/1/2/' scripts/Foo.ps1" }
            @{ name = 'sed -ni, the flag inside a cluster'; command = "sed -ni 's/1/2/p' scripts/Foo.ps1" }
            @{ name = 'perl -pi -e'; command = 'perl -pi -e s/1/2/ scripts/Foo.ps1' }
            @{ name = 'a > redirect onto a script'; command = 'echo x > scripts/Foo.ps1' }
            @{ name = 'a >> redirect onto research.json'; command = 'echo x >> research.json' }
            @{ name = 'a &> redirect'; command = 'some-tool &> scripts/Foo.ps1' }
            @{ name = 'a bare > that truncates a script'; command = '> scripts/Foo.ps1' }
            @{ name = 'a heredoc onto a NEW script'; command = "cat > scripts/New.ps1 <<'EOF'`nWrite-Output 1`nEOF" }
            @{ name = 'tee -a'; command = 'echo x | tee -a scripts/Foo.ps1' }
            @{ name = 'cp into the scripts directory'; command = 'cp /c/temp/x.ps1 scripts/' }
            @{ name = 'mv onto a script'; command = 'mv notes.txt scripts/Foo.ps1' }
            @{ name = 'cp -t into the scripts directory'; command = 'cp -t scripts /c/temp/x.ps1' }
            @{ name = 'a write after cd'; command = 'cd scripts && sed -i s/1/2/ Foo.ps1' }
            @{ name = 'a write nested in pwsh -Command'; command = 'pwsh -NoProfile -Command "Set-Content scripts\Foo.ps1 x"' }
            @{ name = 'a redirect of git output onto a script'; command = 'git show HEAD:scripts/Foo.ps1 > scripts/Foo.ps1' }
        ) {
            Invoke-Guard -Command $command | Should -Be 'deny'
        }
    }

    Context 'a PowerShell write to a guarded file in a repo is refused' {
        It 'refuses <name>' -ForEach @(
            @{ name = 'Set-Content'; command = 'Set-Content scripts\Foo.ps1 -Value x' }
            @{ name = '-replace piped into Set-Content'; command = "(Get-Content research.json) -replace 'a','b' | Set-Content research.json" }
            @{ name = 'Add-Content -Path'; command = 'Add-Content -Path scripts\Foo.ps1 -Value x' }
            @{ name = 'Add-Content with abbreviated parameters, value first'; command = 'Add-Content -Va x -Pa scripts\Foo.ps1' }
            @{ name = 'the copy alias with named parameters'; command = 'copy -Destination scripts\ -Path C:\temp\a.ps1' }
            @{ name = 'Out-File'; command = "'x' | Out-File scripts\Foo.ps1" }
            @{ name = 'Out-File -FilePath with -Append'; command = "'x' | Out-File -Append -FilePath scripts\Foo.ps1" }
            @{ name = 'Tee-Object onto a script'; command = "'x' | Tee-Object scripts\Foo.ps1" }
            @{ name = 'New-Item -Value'; command = 'New-Item -Path scripts -Name New.ps1 -Value x' }
            @{ name = '[IO.File]::WriteAllText'; command = "[IO.File]::WriteAllText('scripts\Foo.ps1', 'x')" }
            @{ name = '[System.IO.File]::Copy onto a script'; command = "[System.IO.File]::Copy('C:\temp\a.txt', 'scripts\Foo.ps1', `$true)" }
            @{ name = 'Copy-Item -Destination a directory'; command = 'Copy-Item C:\temp\a.ps1 -Destination scripts\' }
            @{ name = 'Move-Item onto research.candidate.json'; command = 'Move-Item x.txt research.candidate.json' }
            @{ name = 'a > redirect'; command = '"x" > scripts\Foo.ps1' }
        ) {
            Invoke-Guard -ToolName 'PowerShell' -Command $command | Should -Be 'deny'
        }
    }

    Context 'the denial says what to use instead' {
        It 'sends a .ps1 write to Edit or Write, naming the hooks it routed around' {
            $output = Invoke-GuardRaw -Command 'sed -i s/1/2/ scripts/Foo.ps1'
            $output.permissionDecisionReason | Should -Match ([regex]::Escape((Join-Path $script:repo 'scripts\Foo.ps1')))
            $output.permissionDecisionReason | Should -Match 'through sed -i'
            $output.permissionDecisionReason | Should -Match 'Edit or Write tool'
            $output.permissionDecisionReason | Should -Match 'lint hook'
        }

        It 'sends a research.json write to janet research' {
            $output = Invoke-GuardRaw -Command 'echo x >> research.json'
            $output.permissionDecisionReason | Should -Match 'janet research update'
        }

        It 'says the preserved copy has no write path at all' {
            $output = Invoke-GuardRaw -ToolName 'PowerShell' -Command 'Set-Content research.previous.json x'
            $output.permissionDecision | Should -Be 'deny'
            $output.permissionDecisionReason | Should -Match 'no write path at all'
        }
    }

    Context 'what it must never get in the way of' {
        It 'lets <name> through' -ForEach @(
            @{ name = 'git checkout of a script'; tool = 'Bash'; command = 'git checkout -- scripts/Foo.ps1' }
            @{ name = 'git commit naming scripts and the catalog'; tool = 'Bash'; command = 'git commit -F msg.txt -- scripts/Foo.ps1 research.json' }
            @{ name = 'dotnet build writing a log'; tool = 'Bash'; command = 'dotnet build scripts.sln > build.log' }
            @{ name = 'janet research update, the sanctioned path'; tool = 'Bash'; command = 'janet research update --base . --id x --json-path research.json' }
            @{ name = 'a read with cat'; tool = 'Bash'; command = 'cat scripts/Foo.ps1' }
            @{ name = 'a read with sed -n'; tool = 'Bash'; command = 'sed -n 1,80p scripts/Foo.ps1' }
            @{ name = 'sed -E, which is not -i'; tool = 'Bash'; command = 'sed -E s/1/2/ scripts/Foo.ps1' }
            @{ name = 'perl -I, which is not -i'; tool = 'Bash'; command = 'perl -Ilib -ne print scripts/Foo.ps1' }
            @{ name = 'a read with Get-Content'; tool = 'PowerShell'; command = 'Get-Content research.json -TotalCount 5' }
            @{ name = 'a script run with its output redirected elsewhere'; tool = 'Bash'; command = 'pwsh scripts/Foo.ps1 -Path scripts/Foo.ps1 > out.json 2>/dev/null' }
            @{ name = 'output thrown away to $null'; tool = 'PowerShell'; command = '& .\scripts\Foo.ps1 > $null' }
            @{ name = 'a new directory named like a script'; tool = 'PowerShell'; command = 'New-Item -ItemType Directory -Path scripts\Foo.ps1.d' }
            @{ name = 'Tee-Object into a variable'; tool = 'PowerShell'; command = 'Get-Content scripts\Foo.ps1 | Tee-Object -Variable v' }
            @{ name = 'a write to an unguarded file in the repo'; tool = 'PowerShell'; command = 'Set-Content scripts\notes.txt -Value x' }
        ) {
            Invoke-GuardRaw -ToolName $tool -Command $command | Should -BeNullOrEmpty
        }

        It 'lets a write to a guarded name outside any repo through: <tool>' -ForEach @(
            @{ tool = 'Bash'; target = 'Fix.ps1'; form = 'sed -i s/1/2/ {0}' }
            @{ tool = 'PowerShell'; target = 'research.json'; form = 'Set-Content -Path ''{0}'' -Value x' }
        ) {
            $path = Join-Path $script:plainDir $target
            $shown = if ($tool -eq 'Bash') { $path -replace '\\', '/' } else { $path }
            Invoke-GuardRaw -ToolName $tool -Command ($form -f $shown) | Should -BeNullOrEmpty
        }

        It 'lets a relative write through when the cwd is outside any repo' {
            Invoke-GuardRaw -Command 'echo x > Fix.ps1' -Cwd $script:plainDir | Should -BeNullOrEmpty
        }

        It 'follows cd out of the repo before judging a relative write' {
            # Without following cd this resolves to <repo>\Fix.ps1 and is refused.
            $outside = $script:plainDir -replace '\\', '/'
            Invoke-GuardRaw -Command "cd '$outside' && sed -i s/1/2/ Fix.ps1" | Should -BeNullOrEmpty
        }

        It 'ignores tools that are not a shell' {
            $raw = @{ tool_name = 'Write'; tool_input = @{ file_path = (Join-Path $script:repo 'scripts\Foo.ps1'); command = 'echo x > scripts/Foo.ps1' } } | ConvertTo-Json -Compress
            Invoke-GuardRaw -Raw $raw | Should -BeNullOrEmpty
        }
    }

    Context 'doubt, the escape hatch and failure' {
        It 'runs a write it cannot resolve that names a guarded file, and says so without allowing' {
            $output = Invoke-GuardRaw -ToolName 'PowerShell' -Command 'Set-Content -Path "$dir\Foo.ps1" -Value x'
            $output.PSObject.Properties.Name | Should -Not -Contain 'permissionDecision'
            $output.additionalContext | Should -Match 'could not resolve'
            $output.additionalContext | Should -Match ([regex]::Escape('$dir\Foo.ps1'))
        }

        It 'stays silent on an unresolvable write that names nothing guarded' {
            Invoke-GuardRaw -ToolName 'PowerShell' -Command 'Set-Content -Path $f -Value research' | Should -BeNullOrEmpty
        }

        It 'passes everything when JANET_SHELL_WRITE_GUARD is off' {
            $previous = $env:JANET_SHELL_WRITE_GUARD
            $env:JANET_SHELL_WRITE_GUARD = 'off'
            try { Invoke-Guard -Command 'sed -i s/1/2/ scripts/Foo.ps1' | Should -Be 'pass' }
            finally { $env:JANET_SHELL_WRITE_GUARD = $previous }
        }

        It 'fails open on a malformed payload, and says so' {
            $output = Invoke-GuardRaw -Raw '{"tool_name": "Bash", research.ps1'
            $output.PSObject.Properties.Name | Should -Not -Contain 'permissionDecision'
            $output.additionalContext | Should -Match 'Shell write guard failed'
        }
    }
}
