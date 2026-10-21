#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for the skill fallback check in scripts\Install-JanetEnvironment.ps1
    -- only a skill that resolves $janet must name the checkout in its fallback.

.DESCRIPTION
    The installer writes user-level state, so these tests only ever run it with
    -DryRun, against a fixture checkout and a fixture skill root under %TEMP%.
    Dry run still reads the User-scope JanetBase and PATH and asks git for the
    fixture's core.hooksPath; it writes nothing, and the fixture is not a git
    repository, so the git read fails quietly as it does for an unset key.

    Until 2026-10-21 the check demanded that every SKILL.md mention the checkout,
    so the two pure-prose skills (checking-in and graph-node-reading), which never
    resolve $janet, each reported a problem and every run exited 1.
#>

BeforeAll {
    Set-StrictMode -Version Latest
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:installer = Join-Path $script:repoRoot 'scripts\Install-JanetEnvironment.ps1'

    function New-Fixture {
        # A checkout with a pre-commit hook and the named skills, and an empty
        # skill root beside it. Returns both paths.
        param([hashtable]$Skills)
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ("install-janet-" + [guid]::NewGuid().ToString('N'))
        $base = Join-Path $root 'JanetHome'
        New-Item -ItemType Directory -Path (Join-Path $base '.githooks') -Force | Out-Null
        Set-Content -Path (Join-Path $base '.githooks\pre-commit') -Value '#!/bin/sh' -Encoding UTF8
        foreach ($name in $Skills.Keys) {
            $dir = Join-Path $base "skills\$name"
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            Set-Content -Path (Join-Path $dir 'SKILL.md') -Value ($Skills[$name] -replace '<BASE>', $base) -Encoding UTF8
        }
        [pscustomobject]@{ Root = $root; Base = $base; SkillRoot = (Join-Path $root 'skills-root') }
    }

    function Invoke-DryRun {
        param($Fixture)
        $raw = pwsh -NoProfile -File $script:installer -Base $Fixture.Base -SkillRoot $Fixture.SkillRoot -DryRun | Out-String
        [pscustomobject]@{ Envelope = $raw | ConvertFrom-Json; ExitCode = $LASTEXITCODE }
    }

    $script:prose = "# Checking in`n`nCommit at every verified unit. Install-JanetEnvironment.ps1 wires the gate.`n"
    $script:resolves = "# Deploy`n`n``````powershell`n`$janet = if (`$env:JanetBase) { `$env:JanetBase } else { '<BASE>' }`n& `$janet\scripts\Get-Research.ps1 -Query 'deploy'`n```````n"
    $script:stale = "# Crash`n`n``````powershell`n`$janet = if (`$env:JanetBase) { `$env:JanetBase } else { 'D:\Elsewhere\JanetHome' }`n& `$janet\scripts\Get-Research.ps1 -Query 'crash'`n```````n"
    $script:fixtures = [System.Collections.Generic.List[object]]::new()
}

AfterAll {
    foreach ($fixture in $script:fixtures) { Remove-Item -Recurse -Force $fixture.Root -ErrorAction SilentlyContinue }
}

Describe 'Install-JanetEnvironment skill fallback check' {
    It 'reports no problem for a skill that never resolves $janet' {
        $fixture = New-Fixture @{ 'checking-in' = $script:prose; 'deploy' = $script:resolves }
        $script:fixtures.Add($fixture)
        $result = Invoke-DryRun $fixture
        @($result.Envelope.problems).Count | Should -Be 0 -Because "problems were: $(@($result.Envelope.problems) -join ' | ')"
        $result.Envelope.ok | Should -BeTrue
        $result.ExitCode | Should -Be 0
        Test-Path $fixture.SkillRoot | Should -BeFalse -Because 'a dry run creates nothing'
    }

    It 'still reports a skill whose $janet fallback names another checkout' {
        $fixture = New-Fixture @{ 'checking-in' = $script:prose; 'crash-analysis' = $script:stale }
        $script:fixtures.Add($fixture)
        $result = Invoke-DryRun $fixture
        @($result.Envelope.problems).Count | Should -Be 1
        @($result.Envelope.problems)[0] | Should -BeLike 'crash-analysis\SKILL.md does not mention*'
        $result.Envelope.ok | Should -BeFalse
        $result.ExitCode | Should -Be 1
    }
}
