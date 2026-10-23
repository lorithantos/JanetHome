<#
.SYNOPSIS
    Pre-commit validation: lint, test, config check, and encoding audit.

.DESCRIPTION
    Runs PSScriptAnalyzer and the deep-nesting gate on staged .ps1 files, builds every
    solution at the repo root through `janet check` when the commit stages a C# or build
    file, executes the tracked Pester test files under tests\, validates toolkit config via Test-Config.ps1, and checks
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
. "$PSScriptRoot\lib\TrackedTests.ps1"

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

# ---------- 2b. Solution Build ----------
# Ahead of the test steps, and the only step that compiles anything. Until 2026-10-21 nothing
# in the commit path built Janet.Cli: no test project referenced it, so `dotnet test` never
# compiled it, and on 2026-09-07 a raw string literal's indentation break (CS8999 in its
# Program.cs) passed both test runs and this gate. The verdict and the errors are read from
# the `janet check` envelope, never scraped from msbuild text.
#
# CONDITIONAL on the staged set: it runs when the commit adds, changes or deletes a build
# input. A commit that stages none cannot change the verdict, and in a repo with a slow
# solution an unconditional build would tax every commit for nothing. Measured here the same
# day: about 1s for an up-to-date build, more once a project has to recompile. Like every
# step but the encoding audit's file list, it checks the WORKING TREE, not the index.
# --no-graph because a gate must not refresh anything as a side effect.
Write-StepHeader 'Solution Build'

$buildExtensions = @('.cs', '.csproj', '.sln', '.slnx', '.props', '.targets', '.editorconfig')
$buildFiles = @('global.json', 'nuget.config')
$stagedBuildInputs = @((Invoke-External -NoThrow $git diff --cached --name-only --diff-filter=ACMRD).StdOut |
    Where-Object { $_ -and (([IO.Path]::GetExtension($_) -in $buildExtensions) -or ((Split-Path $_ -Leaf) -in $buildFiles)) })
$solutions = @(Get-ChildItem -LiteralPath $repoRoot -File | Where-Object Extension -in '.sln', '.slnx')
$janetCli = Get-Command janet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1

function Test-SolutionBuild ([string]$Janet, [System.IO.FileInfo]$Solution) {
    $check = Invoke-External -NoThrow -Quiet -TimeoutSec 600 -WorkingDirectory $repoRoot `
        $Janet check --target $Solution.FullName --no-tests --no-graph

    $envelope = $null
    try { $envelope = ($check.StdOut -join "`n") | ConvertFrom-Json } catch { $envelope = $null }

    if ($null -eq $envelope -or $envelope.status -ne 'complete') {
        # No envelope is a check that could not run, which is not a check that passed.
        Write-Host "  janet check returned no complete envelope for $($Solution.Name) (exit $($check.ExitCode)): $($check.StdErr -join ' ')" -ForegroundColor Red
        return $false
    }

    if ($envelope.build.succeeded) {
        Write-Host "  $($Solution.Name) builds ($($envelope.build.durationSeconds)s)."
        return $true
    }

    Write-Host "  $($Solution.Name) does not build:" -ForegroundColor Red
    foreach ($buildError in @($envelope.build.errors)) {
        Write-Host "    $($buildError.file)($($buildError.line)): $($buildError.code) $($buildError.message)" -ForegroundColor Red
    }
    if ($envelope.build.diagnosis) {
        Write-Host "    $($envelope.build.diagnosis)" -ForegroundColor Yellow
    }
    return $false
}

if ($stagedBuildInputs.Count -eq 0) {
    Write-Host '  No staged C# or build files -- skipping.'
    $stepResults['Solution Build'] = $true
} elseif ($solutions.Count -eq 0) {
    Write-Host '  Build files are staged but there is no .sln or .slnx at the repo root -- nothing to build, skipping.' -ForegroundColor Yellow
    $stepResults['Solution Build'] = $true
} elseif ($null -eq $janetCli) {
    # Same rule as the analyzer: build inputs staged and no way to build them is a failure.
    Write-Host '  The janet CLI is not on PATH and build files are staged (dotnet tool install -g Janet.Cli).' -ForegroundColor Red
    Write-StepResult 'Solution Build' $false
} else {
    $built = $true
    foreach ($solution in $solutions) {
        if (-not (Test-SolutionBuild -Janet $janetCli.Source -Solution $solution)) { $built = $false }
    }
    Write-StepResult 'Solution Build' $built
}

# ---------- 3. Pester Tests ----------
Write-StepHeader 'Pester Tests'

# Under this hook git exports GIT_INDEX_FILE -- for 'commit --only' and 'commit -a' a
# temporary index the commit is written from -- and fixtures run git in scratch repos of
# their own. An absolute GIT_INDEX_FILE wins over 'git -C': reproduced 2026-10-21, a
# fixture's 'add -A' in its temp repo rewrote the commit-in-progress index. So every GIT_*
# variable is lifted here, once, for this step alone, and no test has to know. They are put
# back afterwards because the encoding audit's staged list ('git diff --cached') must read
# the index the commit is made from; without it a '--only' commit would be audited against
# whatever else sits staged in .git\index.
function Invoke-PesterWithoutGitEnv ([string[]]$Path) {
    $saved = @(Get-ChildItem Env: | Where-Object Name -like 'GIT_*')
    foreach ($variable in $saved) { Remove-Item -LiteralPath "Env:\$($variable.Name)" }
    try {
        return Invoke-Pester -Path $Path -Output Minimal -PassThru
    } finally {
        foreach ($variable in $saved) { Set-Item -LiteralPath "Env:\$($variable.Name)" -Value $variable.Value }
    }
}

# Keyed to the presence of test files, not a tests directory: a repo whose
# tests\ holds fixtures for some other language must not conscript Pester.
#
# TRACKED files only, since 2026-10-23 (thread item 58). Running every *.Tests.ps1 on disk
# let another session's in-flight, untracked test file fail this session's commit. ls-files
# runs here, BEFORE the GIT_* lift, so it reads the index the commit is written from: a test
# staged new in this commit is listed and runs, an untracked one is named and skipped.
$trackedTests = Invoke-External -NoThrow -WorkingDirectory $repoRoot $git ls-files -- tests
$pesterFiles = Select-TrackedTestFile -TrackedPath @($trackedTests.StdOut | Where-Object { $_ }) -RepoRoot $repoRoot
$untrackedTests = @((Invoke-External -NoThrow -WorkingDirectory $repoRoot $git ls-files --others --exclude-standard -- tests).StdOut |
    Where-Object { $_ -like '*.Tests.ps1' })
foreach ($untracked in $untrackedTests) {
    Write-Host "  Skipping untracked $untracked -- not part of this commit." -ForegroundColor Yellow
}
if (-not $trackedTests.Success) {
    Write-Host "  git ls-files failed (exit $($trackedTests.ExitCode)): $($trackedTests.StdErr -join ' ')" -ForegroundColor Red
    Write-StepResult 'Pester Tests' $false
} elseif ($pesterFiles.Count -gt 0) {
    # -Output/-PassThru as used here are Pester 5 syntax; the 3.4.0 that ships
    # in Windows throws on them. Same failure philosophy as the analyzer guard.
    $pester = Get-Module -ListAvailable Pester | Sort-Object Version -Descending | Select-Object -First 1
    if ($null -eq $pester -or $pester.Version.Major -lt 5) {
        $found = if ($null -eq $pester) { 'none' } else { $pester.Version }
        Write-Host "  Pester 5+ required to run the tests directory (found: $found). Install-Module Pester -Force." -ForegroundColor Red
        Write-StepResult 'Pester Tests' $false
    } else {
        Write-Host "  $($pesterFiles.Count) tracked test file(s)."
        $pesterResult = Invoke-PesterWithoutGitEnv $pesterFiles
        Write-StepResult 'Pester Tests' ($pesterResult.FailedCount -eq 0)
    }
} else {
    Write-Host '  No tracked *.Tests.ps1 files under tests\ -- skipping.'
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
