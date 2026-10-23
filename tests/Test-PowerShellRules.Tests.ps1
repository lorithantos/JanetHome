#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\Test-PowerShellRules.ps1's OrderedIntKey rule.

.DESCRIPTION
    On 2026-10-23 a script wrote 16 thread notes from [ordered]@{ 20 = '...'; 7 = '...' }
    indexed with each key. An int index on an OrderedDictionary is positional, so 15
    notes went in empty and one carried another item's text, every write reporting
    success. These cases pin what the rule flags and what it must leave alone.
#>

BeforeAll {
    $script:linter = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Test-PowerShellRules.ps1'

    function Invoke-Lint {
        # Lints one fixture and emits the OrderedIntKey findings. Emits, so it unrolls:
        # wrap the call in @() before .Count (house rule 1).
        [CmdletBinding()]
        param([string]$Source, [string[]]$ExcludeRule = @())
        $path = Join-Path $TestDrive ('fixture-' + [guid]::NewGuid().ToString('n') + '.ps1')
        Set-Content -LiteralPath $path -Value $Source -Encoding utf8
        $result = & $script:linter -Path $path -ExcludeRule $ExcludeRule | ConvertFrom-Json
        $result.findings | Where-Object { $_.rule -eq 'OrderedIntKey' }
    }
}

Describe 'Test-PowerShellRules OrderedIntKey' {

    It 'flags each integer key of the incident''s shape' {
        $findings = @(Invoke-Lint -Source "`$notes = [ordered]@{ 20 = 'a'; 7 = 'b' }")
        $findings.Count | Should -Be 2
        $findings[0].line | Should -Be 1
        $findings[0].message | Should -Match 'POSITIONAL'
    }

    It 'flags <name>' -ForEach @(
        @{ name = 'one integer key among strings'; source = "`$o = [ordered]@{ a = 1; 2 = 'x' }" }
        @{ name = 'a negative key'; source = "`$o = [ordered]@{ -1 = 'x' }" }
        @{ name = 'a hex key'; source = "`$o = [ordered]@{ 0x10 = 'x' }" }
        @{ name = 'a long key'; source = "`$o = [ordered]@{ 5L = 'x' }" }
        @{ name = 'the cast spelled [Ordered]'; source = "`$o = [Ordered]@{ 3 = 'x' }" }
    ) {
        @(Invoke-Lint -Source $source).Count | Should -Be 1
    }

    It 'reports the line of the key in a multi-line table' {
        $source = @(
            '$o = [ordered]@{'
            '    name = ''x'''
            '    42   = ''y'''
            '}'
        ) -join "`n"
        $findings = @(Invoke-Lint -Source $source)
        $findings.Count | Should -Be 1
        $findings[0].line | Should -Be 3
        $findings[0].text | Should -Be '42   = ''y'''
    }

    It 'leaves alone <name>' -ForEach @(
        @{ name = 'a plain hashtable keyed by integers'; source = "`$h = @{ 20 = 'a'; 7 = 'b' }" }
        @{ name = 'string keys that look like numbers'; source = "`$o = [ordered]@{ '20' = 'a'; `"7`" = 'b' }" }
        @{ name = 'bareword keys'; source = "`$o = [ordered]@{ pattern = 'x'; folder = 'y' }" }
        @{ name = 'a decimal-point key'; source = "`$o = [ordered]@{ 1.5 = 'x' }" }
        @{ name = 'a variable key'; source = "`$o = [ordered]@{ `$n = 'x' }" }
        @{ name = 'the shape in a comment'; source = "# `$o = [ordered]@{ 20 = 'a' }`n`$x = 1" }
        @{ name = 'the shape in a string'; source = "`$s = '[ordered]@{ 20 = ''a'' }'" }
    ) {
        @(Invoke-Lint -Source $source).Count | Should -Be 0
    }

    It 'is skipped by -ExcludeRule OrderedIntKey' {
        @(Invoke-Lint -Source "`$o = [ordered]@{ 1 = 'x' }" -ExcludeRule 'OrderedIntKey').Count | Should -Be 0
    }

    It 'fails the run, as every other rule does' {
        $path = Join-Path $TestDrive 'exit.ps1'
        Set-Content -LiteralPath $path -Value "`$o = [ordered]@{ 1 = 'x' }" -Encoding utf8
        & $script:linter -Path $path | Out-Null
        $LASTEXITCODE | Should -Be 1
    }
}
