#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\GraphCarryOver.Common.ps1 -- which graphs a razorgraph
    rotation carries over, and the order it loads them back in.

.DESCRIPTION
    Update-McpServer.ps1 used to drop every loaded graph on a razorgraph rotation. It now
    keeps the newest graph per (main repo, graph kind) and reloads the previously current
    one last. These tests hold that decision without a server: Select-GraphCarryOver is
    given list_graphs-shaped rows, and the repo and kind resolvers either run for real
    against a throwaway git repo under TestDrive or are stubbed where the disk is not
    the question.
#>

BeforeAll {
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    . (Join-Path $script:repoRoot 'scripts\GraphCarryOver.Common.ps1')

    function New-Row {
        param([string]$Id, [string]$Source, [object]$BuiltAt, [object]$LoadedAt = '2026-10-11T09:00:00+00:00', [switch]$Current)
        $row = [ordered]@{ graphId = $Id; source = $Source }
        if ($null -ne $BuiltAt) { $row.builtAt = $BuiltAt }
        if ($null -ne $LoadedAt) { $row.loadedAt = $LoadedAt }
        $row.isCurrent = [bool]$Current
        return [PSCustomObject]$row
    }

    # Every source is its own kind of nothing: the stubs make repo and kind a pure lookup.
    $script:repoOf = { param($s) Split-Path $s -Parent }
    $script:dotnet = { param($s) 'dotnet' }

    # Under the pre-commit hook git exports GIT_INDEX_FILE, and every git call below would
    # inherit it: 'add -A' wrote this scratch repo's files into the commit being made, and
    # the worktree checked out empty. The scratch repo must find its own index.
    $script:gitEnv = @{}
    foreach ($name in 'GIT_INDEX_FILE', 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_OBJECT_DIRECTORY', 'GIT_COMMON_DIR') {
        $script:gitEnv[$name] = [Environment]::GetEnvironmentVariable($name)
        # Remove-Item, not SetEnvironmentVariable($name, $null): PowerShell hands .NET an
        # empty string for $null, and git reads GIT_DIR='' as "not a git repository: ''".
        Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
    }

    # A real repo with a real agent worktree, because "a worktree folds into its main repo"
    # is a claim about git, and a stub would only test the stub.
    $script:main = Join-Path $TestDrive 'neelam-aesthetics'
    New-Item -ItemType Directory -Path $script:main | Out-Null
    'x' | Set-Content -LiteralPath (Join-Path $script:main 'Neelam.sln')
    New-Item -ItemType Directory -Path (Join-Path $script:main 'createflickrset.lrdevplugin') | Out-Null
    'return {}' | Set-Content -LiteralPath (Join-Path $script:main 'createflickrset.lrdevplugin\Info.lua')
    & git -C $script:main init -q 2>$null
    & git -C $script:main -c user.name=t -c user.email=t@t add -A 2>$null
    & git -C $script:main -c user.name=t -c user.email=t@t commit -q -m init 2>$null
    $script:worktree = Join-Path $script:main '.claude\worktrees\agent-xyz'
    & git -C $script:main worktree add -q $script:worktree 2>$null
    $script:mainFull = ConvertTo-GraphCarryOverPath $script:main
}

AfterAll {
    foreach ($name in $script:gitEnv.Keys) {
        if ($null -ne $script:gitEnv[$name]) { Set-Item -LiteralPath "Env:\$name" -Value $script:gitEnv[$name] }
    }

    # git writes its object files read-only, and Pester's TestDrive cleanup cannot delete
    # a read-only file -- the container fails after every test has passed.
    Get-ChildItem -LiteralPath $TestDrive -Recurse -Force -File -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Attributes = 'Normal' }
}

Describe 'Select-GraphCarryOver' {

    It 'keeps the newest build in a repo and names it as the winner' {
        $plan = Select-GraphCarryOver -ResolveRepo $script:repoOf -ResolveKind $script:dotnet -Graph @(
            New-Row -Id 'old' -Source 'C:\r\A.sln' -BuiltAt '2026-10-10T08:00:00+00:00'
            New-Row -Id 'new' -Source 'C:\r\A.sln' -BuiltAt '2026-10-11T08:00:00+00:00'
            New-Row -Id 'mid' -Source 'C:\r\B.slnx' -BuiltAt '2026-10-10T12:00:00+00:00')

        @($plan.Keep.GraphId) | Should -Be @('new')
        $plan.Drop.Count | Should -Be 2
        @($plan.Drop | Where-Object Winner -ne 'new').Count | Should -Be 0
        ($plan.Drop | Where-Object GraphId -eq 'old').Reason | Should -Match "older build in the same repo.*'new' won"
    }

    It 'folds an agent worktree graph into the repo it belongs to' {
        Test-Path -LiteralPath (Join-Path $script:worktree 'Neelam.sln') | Should -BeTrue
        Get-GraphRepoRoot -Source (Join-Path $script:worktree 'Neelam.sln') | Should -Be $script:mainFull

        $plan = Select-GraphCarryOver -Graph @(
            New-Row -Id 'neelam' -Source (Join-Path $script:main 'Neelam.sln') -BuiltAt '2026-10-11T08:00:00+00:00'
            New-Row -Id 'neelam-agent' -Source (Join-Path $script:worktree 'Neelam.sln') -BuiltAt '2026-10-11T07:00:00+00:00')

        @($plan.Keep.GraphId) | Should -Be @('neelam')
        $plan.Drop[0].GraphId | Should -Be 'neelam-agent'
        $plan.Drop[0].RepoRoot | Should -Be $script:mainFull
    }

    It 'keeps a Lua graph beside the C# graph of the same repo' {
        $plugin = Join-Path $script:main 'createflickrset.lrdevplugin'
        Get-GraphKind -Source $plugin | Should -Be 'lua'
        Get-GraphKind -Source (Join-Path $script:main 'Neelam.sln') | Should -Be 'dotnet'

        # The Lua graph has no builtAt at all and the later loadedAt: were they one group,
        # it would win and the C# graph would be the one dropped.
        $plan = Select-GraphCarryOver -Graph @(
            New-Row -Id 'neelam' -Source (Join-Path $script:main 'Neelam.sln') -BuiltAt '2026-10-11T08:00:00+00:00'
            New-Row -Id 'lightroom-plugin' -Source $plugin -LoadedAt '2026-10-11T10:00:00+00:00')

        @($plan.Keep.GraphId | Sort-Object) | Should -Be @('lightroom-plugin', 'neelam')
        $plan.Drop.Count | Should -Be 0
    }

    It 'folds a deleted worktree into its repo through the nearest existing ancestor' {
        $gone = Join-Path $script:main '.claude\worktrees\agent-gone\Neelam.sln'
        Test-Path -LiteralPath (Split-Path $gone -Parent) | Should -BeFalse
        Get-GraphRepoRoot -Source $gone | Should -Be $script:mainFull

        # The nearest existing ancestor is itself a worktree: still the main repo.
        $goneInWorktree = Join-Path $script:worktree 'src\removed\Thing.csproj'
        Get-GraphRepoRoot -Source $goneInWorktree | Should -Be $script:mainFull

        $plan = Select-GraphCarryOver -Graph @(
            New-Row -Id 'neelam' -Source (Join-Path $script:main 'Neelam.sln') -BuiltAt '2026-10-11T08:00:00+00:00'
            New-Row -Id 'neelam-gone' -Source $gone -BuiltAt '2026-10-11T07:00:00+00:00')
        @($plan.Keep.GraphId) | Should -Be @('neelam')
    }

    It 'falls back to the source directory when no existing ancestor is in a git repo' {
        $loose = Join-Path $TestDrive 'not-a-repo'
        New-Item -ItemType Directory -Path $loose | Out-Null
        Get-GraphRepoRoot -Source (Join-Path $loose 'Loose.sln') | Should -Be (ConvertTo-GraphCarryOverPath $loose)

        $gone = Join-Path $TestDrive 'deleted\Gone.slnx'
        Get-GraphRepoRoot -Source $gone | Should -Be (ConvertTo-GraphCarryOverPath (Split-Path $gone -Parent))
    }

    It 'uses loadedAt when builtAt is missing' {
        $plan = Select-GraphCarryOver -ResolveRepo $script:repoOf -ResolveKind $script:dotnet -Graph @(
            New-Row -Id 'stamped' -Source 'C:\r\A.sln' -BuiltAt '2026-10-10T08:00:00+00:00' -LoadedAt '2026-10-10T08:01:00+00:00'
            New-Row -Id 'unstamped' -Source 'C:\r\A.sln' -LoadedAt '2026-10-11T08:00:00+00:00')

        @($plan.Keep.GraphId) | Should -Be @('unstamped')
        $plan.Drop[0].Reason | Should -Match 'loadedAt 2026-10-11 08:00:00\.000Z vs builtAt'
    }

    It 'loads the previously current graph last' {
        $plan = Select-GraphCarryOver -Current 'b' -ResolveRepo $script:repoOf -ResolveKind $script:dotnet -Graph @(
            New-Row -Id 'a' -Source 'C:\a\A.sln' -BuiltAt '2026-10-11T09:00:00+00:00'
            New-Row -Id 'b' -Source 'C:\b\B.sln' -BuiltAt '2026-10-10T09:00:00+00:00' -Current
            New-Row -Id 'c' -Source 'C:\c\C.sln' -BuiltAt '2026-10-09T09:00:00+00:00')

        @($plan.Keep.GraphId) | Should -Be @('c', 'a', 'b')
        $plan.LastLoaded | Should -Be 'b'
        $plan.CurrentKept | Should -BeTrue
    }

    It 'says so when the current graph loses the dedupe' {
        $plan = Select-GraphCarryOver -Current 'stale' -ResolveRepo $script:repoOf -ResolveKind $script:dotnet -Graph @(
            New-Row -Id 'stale' -Source 'C:\r\A.sln' -BuiltAt '2026-10-10T08:00:00+00:00' -Current
            New-Row -Id 'fresh' -Source 'C:\r\A.sln' -BuiltAt '2026-10-11T08:00:00+00:00'
            New-Row -Id 'other' -Source 'C:\o\O.sln' -BuiltAt '2026-10-09T08:00:00+00:00')

        $plan.CurrentKept | Should -BeFalse
        $plan.LastLoaded | Should -Be 'fresh'
        @($plan.Keep.GraphId) | Should -Be @('other', 'fresh')

        $lines = Format-GraphCarryOverReport -State ([PSCustomObject]@{
                Port = 7718; Plan = $plan; HeldCount = 3; Saved = $plan.Keep; Loaded = $plan.Keep
                Failures = @(); CurrentAfter = 'fresh'; Stage = 'loaded'; Note = $null; Directory = 'D'
            })
        $lines | Should -Contain "  current 'stale' lost the dedupe, so current is whatever loaded last ('fresh')."
    }

    It 'maps a graph carried by an earlier rotation back to its original source' {
        $remembered = @{ 'C:\t\razorgraph-rotation\neelam.json' = @{ source = 'C:\r\A.sln'; kind = 'dotnet' } }
        $plan = Select-GraphCarryOver -Remembered $remembered -ResolveRepo $script:repoOf -ResolveKind { param($s) Get-GraphKind -Source $s } -Graph @(
            New-Row -Id 'neelam' -Source 'C:\t\razorgraph-rotation\neelam.json' -BuiltAt '2026-10-10T08:00:00+00:00'
            New-Row -Id 'neelam2' -Source 'C:\r\A.sln' -BuiltAt '2026-10-11T08:00:00+00:00')

        @($plan.Keep.GraphId) | Should -Be @('neelam2')
        $plan.Drop[0].Source | Should -Be 'C:\r\A.sln'
    }

    It 'never deduplicates a graph loaded from a saved file it cannot place' {
        $plan = Select-GraphCarryOver -ResolveRepo $script:repoOf -ResolveKind { param($s) Get-GraphKind -Source $s } -Graph @(
            New-Row -Id 'cs' -Source 'C:\r\.graphs\cs.json' -BuiltAt '2026-10-11T08:00:00+00:00'
            New-Row -Id 'lua' -Source 'C:\r\.graphs\lua.json' -BuiltAt '2026-10-10T08:00:00+00:00')

        $plan.Drop.Count | Should -Be 0
        @($plan.Keep.GraphId) | Should -Be @('lua', 'cs')
    }
}
