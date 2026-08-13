<#
.SYNOPSIS
    Pre-commit validation: lint, test, config check, and encoding audit.

.DESCRIPTION
    Runs PSScriptAnalyzer and the deep-nesting gate on staged .ps1 files, executes
    the Pester test suite, validates toolkit config via Test-Config.ps1, and checks
    file encoding on staged files via Test-FileEncoding.ps1. Returns exit code 0
    only when every step passes.

    Repo-agnostic by design (see note.pre-checkin-style-janet-level): staged files
    are resolved against the repo git reports from the current directory, not
    against this toolkit's own root, so the same gate serves every repo.

.EXAMPLE
    & "$env:JanetBase\scripts\Test-PreCommit.ps1"
#>

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\lib\Invoke-External.ps1"

# git is the source of truth for what is staged; without it every step would
# see an empty staged list and pass while checking nothing -- the exact quiet
# degradation this gate exists to stop. Fail loudly instead. Not every machine
# has git on PATH (a Visual Studio install without standalone git is common),
# so resolve it: JANET_GIT if set, then PATH, then the VS-bundled copy.
$git = $null
# -CommandType Application is load-bearing: this toolkit ships scripts\git.ps1,
# and when that directory is reachable on PATH a bare `Get-Command git` returns
# the SHIM. Invoke-External then cannot start a .ps1 as a process, git looks
# missing, and the gate aborts having checked nothing -- observed 2026-08-10.
# The extension check is belt: a shim by any other name is still not an exe.
$gitCandidates = @(
    $env:JANET_GIT
    (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)?.Source
) + @(Get-ChildItem "$env:ProgramFiles\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe" -ErrorAction SilentlyContinue | ForEach-Object FullName)
foreach ($candidate in $gitCandidates) {
    if ($candidate -and (Test-Path $candidate) -and $candidate -like '*.exe') {
        $git = $candidate
        break
    }
}
if (-not $git) {
    Write-Host 'git not found (checked JANET_GIT, PATH, VS-bundled). Cannot determine staged files; aborting.' -ForegroundColor Red
    exit 1
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

# The repo under check is wherever the commit is happening, which is not
# necessarily this toolkit's repo. Joining staged paths to the toolkit root made
# the gate check the wrong tree when run from anywhere else -- quietly, since the
# joined paths simply failed Test-Path and vanished from every staged list.
$topLevel = Invoke-External -NoThrow $git rev-parse --show-toplevel
if (-not $topLevel.Success) {
    Write-Host 'Not inside a git repository; nothing to gate. Aborting.' -ForegroundColor Red
    exit 1
}
$repoRoot = [string]($topLevel.StdOut | Select-Object -First 1)

$stepResults = [ordered]@{}
$overallPass = $true

function Write-StepHeader ([string]$Name) {
    Write-Host "`n--- $Name ---" -ForegroundColor Cyan
}

function Write-StepResult ([string]$Name, [bool]$Passed) {
    if ($Passed) {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        $script:overallPass = $false
    }
    $script:stepResults[$Name] = $Passed
}

# ---------- 1. PSScriptAnalyzer on staged .ps1 files ----------
Write-StepHeader 'PSScriptAnalyzer'

$stagedPs1 = @((Invoke-External -NoThrow $git diff --cached --name-only --diff-filter=ACM).StdOut |
    Where-Object { $_ -like '*.ps1' } |
    ForEach-Object { Join-Path $repoRoot $_ } |
    Where-Object { Test-Path $_ })

if ($stagedPs1.Count -eq 0) {
    Write-Host '  No staged .ps1 files -- skipping.'
    $stepResults['PSScriptAnalyzer'] = $true
} elseif ($null -eq (Get-Command Invoke-ScriptAnalyzer -ErrorAction SilentlyContinue)) {
    # A missing linter with lintable files staged is a failure, not a skip:
    # a gate that silently checks less than it claims is worse than no gate.
    Write-Host '  PSScriptAnalyzer is not installed (Install-Module PSScriptAnalyzer) and .ps1 files are staged.' -ForegroundColor Red
    Write-StepResult 'PSScriptAnalyzer' $false
} else {
    $settingsPath = Join-Path $repoRoot 'PSScriptAnalyzerSettings.psd1'
    $analyzerArgs = @{ Severity = @('Error', 'Warning') }
    if (Test-Path $settingsPath) {
        $analyzerArgs['Settings'] = $settingsPath
    }

    $violations = @()
    foreach ($file in $stagedPs1) {
        $violations += @(Invoke-ScriptAnalyzer -Path $file @analyzerArgs)
    }

    if ($violations.Count -gt 0) {
        $violations | Format-Table RuleName, Severity, ScriptName, Line, Message -AutoSize -Wrap
    }

    $errors = @($violations | Where-Object Severity -eq 'Error')
    Write-StepResult 'PSScriptAnalyzer' ($errors.Count -eq 0)
}

# ---------- 2. Deep Nesting ----------
Write-StepHeader 'Deep Nesting'

# Gate threshold 6, from the 2026-08-03 calibration sweep: everything >= 6 in
# both this toolkit and RazorGraphTool proved refactor-worthy, while 5 is
# case-by-case (ExtractCallEdges stayed at 5 deliberately). Advisory sweeps at
# lower thresholds go straight to Test-DeepNesting.ps1. C# gets the same rule
# from RazorGraph deep_methods / query --deep; there is no fast per-commit C#
# path yet, so this step covers PowerShell only.
$maxNesting = 6
$testNesting = Join-Path $scriptRoot 'Test-DeepNesting.ps1'
if ($stagedPs1.Count -eq 0) {
    Write-Host '  No staged .ps1 files -- skipping.'
    $stepResults['Deep Nesting'] = $true
} elseif (Test-Path $testNesting) {
    $global:LASTEXITCODE = 0
    & $testNesting -Path $stagedPs1 -MinDepth $maxNesting -Text
    Write-StepResult 'Deep Nesting' ($LASTEXITCODE -eq 0)
} else {
    Write-Host '  Test-DeepNesting.ps1 not found and .ps1 files are staged.' -ForegroundColor Red
    Write-StepResult 'Deep Nesting' $false
}

# ---------- 3. Pester Tests ----------
Write-StepHeader 'Pester Tests'

# Keyed to the presence of test files, not a tests directory: a repo whose
# tests\ holds fixtures for some other language must not conscript Pester.
$testsDir = Join-Path $repoRoot 'tests'
$pesterFiles = if (Test-Path $testsDir) { @(Get-ChildItem $testsDir -Recurse -Filter '*.Tests.ps1' -File) } else { @() }
if ($pesterFiles.Count -gt 0) {
    # -Output/-PassThru as used here are Pester 5 syntax; the 3.4.0 that ships
    # in Windows throws on them. Same failure philosophy as the analyzer guard.
    $pester = Get-Module -ListAvailable Pester | Sort-Object Version -Descending | Select-Object -First 1
    if ($null -eq $pester -or $pester.Version.Major -lt 5) {
        $found = if ($null -eq $pester) { 'none' } else { $pester.Version }
        Write-Host "  Pester 5+ required to run the tests directory (found: $found). Install-Module Pester -Force." -ForegroundColor Red
        Write-StepResult 'Pester Tests' $false
    } else {
        $pesterResult = Invoke-Pester -Path $testsDir -Output Minimal -PassThru
        Write-StepResult 'Pester Tests' ($pesterResult.FailedCount -eq 0)
    }
} else {
    Write-Host '  No *.Tests.ps1 files found -- skipping.'
    $stepResults['Pester Tests'] = $true
}

# ---------- 4. Config Validation ----------
Write-StepHeader 'Config Validation'

$testConfig = Join-Path $scriptRoot 'Test-Config.ps1'
if (Test-Path $testConfig) {
    try {
        # A child script that never calls exit leaves $LASTEXITCODE holding
        # whatever an earlier step's child set it to -- reset, or a passing
        # step inherits a failing verdict from a stranger.
        $global:LASTEXITCODE = 0
        & $testConfig
        Write-StepResult 'Config Validation' ($LASTEXITCODE -eq 0)
    } catch {
        Write-Host "  Error: $_" -ForegroundColor Red
        Write-StepResult 'Config Validation' $false
    }
} else {
    Write-Host '  Test-Config.ps1 not found -- skipping.'
    $stepResults['Config Validation'] = $true
}

# ---------- 5. File Encoding ----------
Write-StepHeader 'File Encoding'

$stagedAll = @((Invoke-External -NoThrow $git diff --cached --name-only --diff-filter=ACM).StdOut |
    ForEach-Object { Join-Path $repoRoot $_ } |
    Where-Object { Test-Path $_ })

$testEncoding = Join-Path $scriptRoot 'Test-FileEncoding.ps1'
if ($stagedAll.Count -eq 0) {
    Write-Host '  No staged files -- skipping.'
    $stepResults['File Encoding'] = $true
} elseif (Test-Path $testEncoding) {
    try {
        $global:LASTEXITCODE = 0
        & $testEncoding -Path $stagedAll
        Write-StepResult 'File Encoding' ($LASTEXITCODE -eq 0 -or $null -eq $LASTEXITCODE)
    } catch {
        Write-Host "  Error: $_" -ForegroundColor Red
        Write-StepResult 'File Encoding' $false
    }
} else {
    Write-Host '  Test-FileEncoding.ps1 not found -- skipping.'
    $stepResults['File Encoding'] = $true
}

# ---------- 6. Output Contracts ----------
# Only where a repo declares formats. Everything else here is repo-agnostic and this stays
# so: no contracts directory means there is nothing to hold to a schema, which is a
# different thing from a check that failed.
$contracts = Join-Path $repoRoot 'contracts'
$testContracts = Join-Path $scriptRoot 'Test-OutputContracts.ps1'

if (Test-Path $contracts) {
    Write-StepHeader 'Output Contracts'

    if (Test-Path $testContracts) {
        try {
            $global:LASTEXITCODE = 0

            # Against HEAD, matching what is about to be committed: the change rule asks
            # whether the schema moved in THIS commit, not since some older point.
            & $testContracts -Text -Against HEAD
            Write-StepResult 'Output Contracts' ($LASTEXITCODE -eq 0 -or $null -eq $LASTEXITCODE)
        } catch {
            Write-Host "  Error: $_" -ForegroundColor Red
            Write-StepResult 'Output Contracts' $false
        }
    } else {
        Write-Host '  Test-OutputContracts.ps1 not found and contracts\ exists.' -ForegroundColor Red
        Write-StepResult 'Output Contracts' $false
    }
}

# ---------- Summary ----------
Write-Host "`n=== Pre-Commit Summary ===" -ForegroundColor Cyan
foreach ($kv in $stepResults.GetEnumerator()) {
    $icon = if ($kv.Value) { '[PASS]' } else { '[FAIL]' }
    $color = if ($kv.Value) { 'Green' } else { 'Red' }
    Write-Host "  $icon $($kv.Key)" -ForegroundColor $color
}

if ($overallPass) {
    Write-Host "`nAll checks passed." -ForegroundColor Green
    exit 0
} else {
    Write-Host "`nOne or more checks failed. Fix before committing." -ForegroundColor Red
    exit 1
}
