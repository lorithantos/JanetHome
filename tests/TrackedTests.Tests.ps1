#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\lib\TrackedTests.ps1 (Select-TrackedTestFile), the pre-commit
    gate's choice of which Pester files to run.

.DESCRIPTION
    Thread item 58: the gate ran every *.Tests.ps1 on disk, so another session's untracked,
    in-flight test file could fail this session's commit. The gate now runs the files git
    tracks. These cases pin the filter, and one runs real git in a scratch repo to pin that a
    test file STAGED NEW in the commit is selected while an untracked one is not.
#>

BeforeAll {
    . (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\lib\TrackedTests.ps1')

    function Add-FixtureFile ([string]$Root, [string]$Relative) {
        $path = Join-Path $Root $Relative
        $null = New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force
        Set-Content -LiteralPath $path -Value 'Describe x { It y { } }' -Encoding utf8
        return $path
    }
}

Describe 'Select-TrackedTestFile' {

    BeforeEach {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('n'))
        $null = New-Item -ItemType Directory -Path $root
    }

    It 'returns the full path of each listed tests\ *.Tests.ps1, nested ones included' {
        $a = Add-FixtureFile $root 'tests\A.Tests.ps1'
        $b = Add-FixtureFile $root 'tests\sub\B.Tests.ps1'
        $selected = Select-TrackedTestFile -TrackedPath 'tests/A.Tests.ps1', 'tests/sub/B.Tests.ps1' -RepoRoot $root
        $selected | Should -Be @($a, $b)
    }

    It 'leaves out a test file on disk that git does not list' {
        $tracked = Add-FixtureFile $root 'tests\Tracked.Tests.ps1'
        $null = Add-FixtureFile $root 'tests\Zz-Untracked.Tests.ps1'
        $selected = Select-TrackedTestFile -TrackedPath 'tests/Tracked.Tests.ps1' -RepoRoot $root
        $selected | Should -Be @($tracked)
    }

    It 'ignores listed paths outside tests\ or not named *.Tests.ps1' {
        $null = Add-FixtureFile $root 'scripts\Elsewhere.Tests.ps1'
        $null = Add-FixtureFile $root 'tests\helper.ps1'
        $null = Add-FixtureFile $root 'tests\fixtures\data.json'
        $selected = Select-TrackedTestFile -TrackedPath 'scripts/Elsewhere.Tests.ps1', 'tests/helper.ps1', 'tests/fixtures/data.json' -RepoRoot $root
        $selected.Count | Should -Be 0
    }

    It 'drops a listed file that is missing from the working tree' {
        $selected = Select-TrackedTestFile -TrackedPath 'tests/Deleted.Tests.ps1' -RepoRoot $root
        $selected.Count | Should -Be 0
    }

    It 'returns an empty array, not null, for empty input' {
        $selected = Select-TrackedTestFile -TrackedPath @() -RepoRoot $root
        $null -eq $selected | Should -BeFalse
        $selected.Count | Should -Be 0
    }

    It 'selects a committed and a staged-new test from real git ls-files, and not an untracked one' {
        # GIT_* from an enclosing hook would point git at the wrong index; the gate lifts them
        # for this step and so does this case.
        $saved = @(Get-ChildItem Env: | Where-Object Name -like 'GIT_*')
        foreach ($variable in $saved) { Remove-Item -LiteralPath "Env:\$($variable.Name)" }
        try {
            git init --quiet $root
            git -C $root config user.email 'fixture@example.invalid'
            git -C $root config user.name 'fixture'
            git -C $root config commit.gpgsign false
            $committed = Add-FixtureFile $root 'tests\Committed.Tests.ps1'
            git -C $root add -- tests/Committed.Tests.ps1
            git -C $root commit --quiet -m 'fixture'
            $stagedNew = Add-FixtureFile $root 'tests\StagedNew.Tests.ps1'
            git -C $root add -- tests/StagedNew.Tests.ps1
            $null = Add-FixtureFile $root 'tests\Untracked.Tests.ps1'
            $listed = @(git -C $root ls-files -- tests)
        } finally {
            foreach ($variable in $saved) { Set-Item -LiteralPath "Env:\$($variable.Name)" -Value $variable.Value }
        }
        $selected = Select-TrackedTestFile -TrackedPath $listed -RepoRoot $root
        $selected | Should -Be @($committed, $stagedNew)
    }
}
