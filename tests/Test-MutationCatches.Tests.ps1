#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for the no-op refusal in scripts\Test-MutationCatches.ps1 -- a
    case-only mutation changes the file and must run; a true no-op must not.

.DESCRIPTION
    The script finds its suite runner as Invoke-DotnetCheck.ps1 beside itself, so
    these tests copy the real script, at test time, into a temp directory next to
    a stub runner. The stub reports the suite green only while the subject file
    still holds its original text, compared case-sensitively, so a mutation that
    lands is caught and one that is refused never reaches the suite. The copy is
    taken fresh on every run, so an edit to the script is what they test.

    Until 2026-10-21 the no-op check compared with PowerShell's case-insensitive
    -eq, so a mutation that only changed case (find '/whatIf?api-version=',
    replace '/whatif?api-version=') was reported 'no-op' and never run, although
    the file bytes differed. Case-only changes are exactly the class that matters
    for ARM paths and scope comparisons.
#>

BeforeAll {
    Set-StrictMode -Version Latest
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("mutation-catches-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $script:fixture | Out-Null

    Copy-Item (Join-Path $script:repoRoot 'scripts\Test-MutationCatches.ps1') $script:fixture
    $script:subjectText = 'POST /whatIf?api-version=2024-03-01'

    # Green exactly while subject.txt is byte-for-byte the original text.
    $stub = @'
[CmdletBinding()]
param([string]$Target, [string]$Configuration, [switch]$NoGraph, [string]$TestFilter)
$subject = Join-Path (Split-Path $Target -Parent) 'subject.txt'
$green = [System.IO.File]::ReadAllText($subject) -ceq 'POST /whatIf?api-version=2024-03-01'
$failures = if ($green) { @() } else { @([pscustomobject]@{ test = 'Subject.KeepsItsCase' }) }
[pscustomobject]@{ status = 'complete'; succeeded = $green; tests = [pscustomobject]@{ failures = $failures } } |
    ConvertTo-Json -Depth 4 -Compress
'@
    Set-Content -Path (Join-Path $script:fixture 'Invoke-DotnetCheck.ps1') -Value $stub -Encoding UTF8
    Set-Content -Path (Join-Path $script:fixture 'Fixture.slnx') -Value '<Solution />' -Encoding UTF8

    function Invoke-Plan {
        # Writes the subject fresh, runs the copied script over a one-mutation
        # plan, and returns its envelope with the exit code and the subject's
        # text afterwards.
        param([string]$Find, [string]$Replace)
        $subject = Join-Path $script:fixture 'subject.txt'
        [System.IO.File]::WriteAllText($subject, $script:subjectText)
        $plan = Join-Path $script:fixture 'plan.json'
        [pscustomobject]@{
            target    = (Join-Path $script:fixture 'Fixture.slnx')
            mutations = @([pscustomobject]@{ name = 'the mutation'; path = 'subject.txt'; find = $Find; replace = $Replace })
        } | ConvertTo-Json -Depth 4 | Set-Content -Path $plan -Encoding UTF8

        $raw = pwsh -NoProfile -File (Join-Path $script:fixture 'Test-MutationCatches.ps1') -Plan $plan | Out-String
        $code = $LASTEXITCODE
        [pscustomobject]@{
            Envelope = $raw | ConvertFrom-Json
            ExitCode = $code
            After    = [System.IO.File]::ReadAllText($subject)
        }
    }
}

AfterAll {
    Remove-Item -Recurse -Force $script:fixture -ErrorAction SilentlyContinue
}

Describe 'Test-MutationCatches no-op refusal' {
    It 'applies a case-only mutation rather than refusing it as a no-op' {
        $result = Invoke-Plan -Find '/whatIf?' -Replace '/whatif?'
        @($result.Envelope.findings).Count | Should -Be 0 -Because "findings were: $($result.Envelope.findings | ConvertTo-Json -Compress)"
        $result.Envelope.caught | Should -Be 1
        @($result.Envelope.caughtDetail)[0].caughtBy | Should -Contain 'Subject.KeepsItsCase'
        $result.ExitCode | Should -Be 0
        $result.After | Should -BeExactly $script:subjectText
    }

    It 'still refuses a mutation that leaves the file identical' {
        $result = Invoke-Plan -Find '/whatIf?' -Replace '/whatIf?'
        $result.Envelope.caught | Should -Be 0
        @($result.Envelope.findings).Count | Should -Be 1
        @($result.Envelope.findings)[0].kind | Should -Be 'no-op'
        $result.ExitCode | Should -Be 1
        $result.After | Should -BeExactly $script:subjectText
    }
}
