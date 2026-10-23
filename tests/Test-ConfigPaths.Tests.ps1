#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for the hook-command arm of scripts\Test-ConfigPaths.ps1.

.DESCRIPTION
    Each test writes a fixture project under TestDrive whose .claude\settings.json
    declares hooks, and runs the script with -ProjectDir pointed at it. This repo's own
    .mcp.json and .claude\settings.json are always scanned too, so totals are compared
    against a baseline run over an empty project dir rather than asserted outright, and
    broken entries are filtered to the fixture's file. The user-level settings are left
    out (-SkipUserSettings) except in the one test about them, which points USERPROFILE
    at a fixture -- no real settings file is written by anything here.

    Added 2026-10-21: until then a hook pointing at a deleted script was invisible, and
    startup reported '0 absolute path(s) across 3 file(s) all resolve' over six of them.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test fixture builders: project dirs under TestDrive, where -WhatIf has no meaning.')]
param()

BeforeAll {
    $script:subject = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Test-ConfigPaths.ps1'

    function Invoke-ConfigPaths {
        # Runs the script and returns its envelope as an object.
        [CmdletBinding()]
        param([Parameter(Mandatory)][string]$ProjectDir, [switch]$WithUserSettings)
        $json = if ($WithUserSettings) { & $script:subject -ProjectDir $ProjectDir } else { & $script:subject -ProjectDir $ProjectDir -SkipUserSettings }
        return ($json | ConvertFrom-Json)
    }

    function New-HookProject {
        # A project dir with a .claude\settings.json holding one PreToolUse hook per
        # command, and the named scripts created under it.
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][string]$Name,
            [Parameter(Mandatory)][string[]]$Command,
            [string[]]$Present = @(),
            [string]$SettingsFile = 'settings.json'
        )
        $dir = Join-Path $TestDrive $Name
        New-Item -ItemType Directory -Path (Join-Path $dir '.claude'), (Join-Path $dir 'scripts') -Force | Out-Null
        foreach ($leaf in $Present) { Set-Content -Path (Join-Path $dir "scripts\$leaf") -Value '# fixture hook' -Encoding UTF8 }
        $groups = @($Command | ForEach-Object { [ordered]@{ matcher = 'Write'; hooks = @([ordered]@{ type = 'command'; command = $_ }) } })
        $text = [ordered]@{ hooks = [ordered]@{ PreToolUse = $groups } } | ConvertTo-Json -Depth 8
        Set-Content -Path (Join-Path $dir ".claude\$SettingsFile") -Value $text -Encoding UTF8
        return $dir
    }

    function Get-LiteralCommand {
        # The user-level form: a literal base, then Join-Path, then run if present.
        [CmdletBinding()]
        param([Parameter(Mandatory)][string]$Base, [Parameter(Mandatory)][string]$Leaf)
        return "`$j='$Base'; `$s = Join-Path `$j 'scripts\$Leaf'; if (Test-Path `$s) { & `$s }"
    }

    $script:projectCommand = "`$d = if (`$env:CLAUDE_PROJECT_DIR) { `$env:CLAUDE_PROJECT_DIR } else { (Get-Location).Path }; `$s = Join-Path `$d 'scripts\Missing.ps1'; if (Test-Path `$s) { & `$s }"

    $script:empty = Join-Path $TestDrive 'Empty'
    New-Item -ItemType Directory -Path $script:empty -Force | Out-Null
    $script:baseline = Invoke-ConfigPaths $script:empty
}

Describe 'hook commands in Test-ConfigPaths' {
    It 'resolves a hook whose script exists, and counts it' {
        $dir = Join-Path $TestDrive 'Present'
        $project = New-HookProject 'Present' -Command (Get-LiteralCommand $dir 'Present.ps1') -Present 'Present.ps1'
        $result = Invoke-ConfigPaths $project

        $result.checked | Should -Be ($script:baseline.checked + 1)
        @($result.broken | Where-Object { $_.file -like "$project*" }).Count | Should -Be 0
        @($result.unchecked | Where-Object { $_.file -like "$project*" }).Count | Should -Be 0
    }

    It 'lists a hook whose script is missing under broken' {
        $dir = Join-Path $TestDrive 'Missing'
        $project = New-HookProject 'Missing' -Command (Get-LiteralCommand $dir 'Gone.ps1')
        $result = Invoke-ConfigPaths $project

        $result.ok | Should -BeFalse
        $result.checked | Should -Be ($script:baseline.checked + 1)
        $mine = @($result.broken | Where-Object { $_.file -like "$project*" })
        $mine.Count | Should -Be 1
        $mine[0].rule | Should -Be 'hooks.PreToolUse[0].hooks[0].command'
        $mine[0].path | Should -Be (Join-Path $dir 'scripts\Gone.ps1')
    }

    It 'resolves a project-form hook against the project that owns the .claude dir' {
        $project = New-HookProject 'ProjectForm' -Command $script:projectCommand
        $result = Invoke-ConfigPaths $project

        $mine = @($result.broken | Where-Object { $_.file -like "$project*" })
        $mine.Count | Should -Be 1
        $mine[0].path | Should -Be (Join-Path $project 'scripts\Missing.ps1')
    }

    It 'checks a bare absolute .ps1 path in a hook command' {
        $gone = Join-Path $TestDrive 'Absolute\nowhere\Gone.ps1'
        $project = New-HookProject 'Absolute' -Command "pwsh -NoProfile -File $gone"
        $result = Invoke-ConfigPaths $project

        $mine = @($result.broken | Where-Object { $_.file -like "$project*" })
        $mine.Count | Should -Be 1
        $mine[0].path | Should -Be $gone
    }

    It 'scans the project settings.local.json, where a hook can also be declared' {
        # Startup reads settings.local.json when it decides ENFORCED, so a hook declared
        # there pointing at a deleted script was counted wired and never checked here.
        $project = New-HookProject 'LocalSettings' -Command $script:projectCommand -SettingsFile 'settings.local.json'
        $local = Join-Path $project '.claude\settings.local.json'
        $result = Invoke-ConfigPaths $project

        $result.filesScanned | Should -Contain $local
        $result.checked | Should -Be ($script:baseline.checked + 1)
        $mine = @($result.broken | Where-Object { $_.file -eq $local })
        $mine.Count | Should -Be 1
        $mine[0].path | Should -Be (Join-Path $project 'scripts\Missing.ps1')
    }

    It 'leaves a project-form hook in the user-level file unchecked rather than guessing' {
        $profileDir = Join-Path $TestDrive 'Profile'
        New-Item -ItemType Directory -Path (Join-Path $profileDir '.claude') -Force | Out-Null
        $text = [ordered]@{ hooks = [ordered]@{ PreToolUse = @([ordered]@{ matcher = 'Write'; hooks = @([ordered]@{ type = 'command'; command = $script:projectCommand }) }) } } |
            ConvertTo-Json -Depth 8
        Set-Content -Path (Join-Path $profileDir '.claude\settings.json') -Value $text -Encoding UTF8

        $prior = $env:USERPROFILE
        $env:USERPROFILE = $profileDir
        try { $result = Invoke-ConfigPaths $script:empty -WithUserSettings }
        finally { $env:USERPROFILE = $prior }

        $mine = @($result.unchecked | Where-Object { $_.file -like "$profileDir*" })
        $mine.Count | Should -Be 1
        $mine[0].reason | Should -BeLike '*project dir*'
        @($result.broken | Where-Object { $_.file -like "$profileDir*" }).Count | Should -Be 0
    }
}
