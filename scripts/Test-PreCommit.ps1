<#
.SYNOPSIS
    Pre-commit validation: lint, test, config check, and encoding audit.

.DESCRIPTION
    Runs PSScriptAnalyzer on staged .ps1 files, executes the Pester test suite,
    validates toolkit config via Test-Config.ps1, and checks file encoding on
    staged files via Test-FileEncoding.ps1. Returns exit code 0 only when every
    step passes.

.EXAMPLE
    & "$env:JanetBase\.github\scripts\Test-PreCommit.ps1"
#>

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\lib\Invoke-External.ps1"


$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolkitRoot = Split-Path -Parent $scriptRoot
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

$stagedPs1 = @((Invoke-External -NoThrow git diff --cached --name-only --diff-filter=ACM).StdOut |
    Where-Object { $_ -like '*.ps1' } |
    ForEach-Object { Join-Path $toolkitRoot $_ } |
    Where-Object { Test-Path $_ })

if ($stagedPs1.Count -eq 0) {
    Write-Host '  No staged .ps1 files -- skipping.'
    $stepResults['PSScriptAnalyzer'] = $true
} else {
    $settingsPath = Join-Path $toolkitRoot 'PSScriptAnalyzerSettings.psd1'
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

# ---------- 2. Pester Tests ----------
Write-StepHeader 'Pester Tests'

$testsDir = Join-Path $toolkitRoot 'tests'
if (Test-Path $testsDir) {
    $pesterResult = Invoke-Pester -Path $testsDir -Output Minimal -PassThru
    Write-StepResult 'Pester Tests' ($pesterResult.FailedCount -eq 0)
} else {
    Write-Host '  No tests directory found -- skipping.'
    $stepResults['Pester Tests'] = $true
}

# ---------- 3. Config Validation ----------
Write-StepHeader 'Config Validation'

$testConfig = Join-Path $scriptRoot 'Test-Config.ps1'
if (Test-Path $testConfig) {
    try {
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

# ---------- 4. File Encoding ----------
Write-StepHeader 'File Encoding'

$stagedAll = @((Invoke-External -NoThrow git diff --cached --name-only --diff-filter=ACM).StdOut |
    ForEach-Object { Join-Path $toolkitRoot $_ } |
    Where-Object { Test-Path $_ })

$testEncoding = Join-Path $scriptRoot 'Test-FileEncoding.ps1'
if ($stagedAll.Count -eq 0) {
    Write-Host '  No staged files -- skipping.'
    $stepResults['File Encoding'] = $true
} elseif (Test-Path $testEncoding) {
    try {
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
