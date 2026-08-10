<#
.SYNOPSIS
    Builds and tests a .NET target and reports the result as structured JSON
    instead of console scrollback.

.DESCRIPTION
    dotnet's own output buries the payload: one real warning drowns in fifteen
    restore warnings, and a failed test's assert message takes three re-runs
    with different console filters to extract. This script runs the build and
    the tests once and emits what a session actually needs, parsed with a JSON
    reader rather than grepped out of text.

    The contract: errors are always verbatim and never truncated. Warnings are
    deduplicated and grouped by code with counts -- collapsed, never silently
    dropped; the envelope reports what it omitted. Test failures carry their
    payload up front: test name, the assert message in full, and the top of
    the stack. Failures are read from TRX files, which are structured, not
    scraped from the console. The script's exit code means exactly one thing:
    0 when the build succeeded and every test passed.

    Output is JSON by default (the consumer is a model); -Text is the human
    opt-in. The envelope stamps 'contract' so a reader can detect drift: if
    the number differs from what the research node documents, or fields
    appear that the node does not list, trust the JSON in front of you and
    re-read this help.

.PARAMETER Target
    A .sln/.slnx/.csproj file, or a directory containing exactly one.
    Defaults to the current directory.

.PARAMETER Configuration
    Build configuration. Default Debug.

.PARAMETER NoTests
    Build only. The tests field comes back null.

.PARAMETER TestFilter
    Passed to dotnet test --filter. Counters then describe the filtered run,
    not the whole suite.

.PARAMETER Text
    Human-readable output instead of JSON.

.PARAMETER Pretty
    Indent the JSON. Debugging by eye only.

.OUTPUTS
    JSON: { contract, target, configuration, succeeded,
            build:  { succeeded, durationSeconds, errors[], warnings[],
                      warningCount },
            tests:  null | { succeeded, total, passed, failed, skipped,
                             failures[], assemblies[] } }
    errors[]:    { file, line, code, message } -- every instance, verbatim.
    warnings[]:  { code, count, instances[], omittedInstances } -- instances
                 deduplicated; a positive omittedInstances is the honest
                 truncation marker.
    failures[]:  { test, message, stack[] } -- message verbatim and whole.
    tests is null when -NoTests was passed or the build failed; which one is
    visible from build.succeeded.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-DotnetCheck.ps1" -Target D:\Repos\RetirementCore
    Build + full test run as compact JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-DotnetCheck.ps1" -Target .\App.sln -NoTests -Text
    Build only, human-readable.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-DotnetCheck.ps1" -TestFilter GridSortMemoryTests -Pretty
    One test class, indented JSON.
#>
[CmdletBinding()]
param(
    [string]$Target = '.',
    [string]$Configuration = 'Debug',
    [switch]$NoTests,
    [string]$TestFilter,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-BuildTarget {
    param([string]$Given)

    $resolved = Resolve-Path -LiteralPath $Given
    $item = Get-Item -LiteralPath $resolved
    if (-not $item.PSIsContainer) { return $item.FullName }

    $candidates = @(Get-ChildItem -LiteralPath $item.FullName -File |
        Where-Object { $_.Extension -in '.sln', '.slnx', '.csproj' })
    if ($candidates.Count -eq 1) { return $candidates[0].FullName }

    $names = ($candidates | ForEach-Object Name) -join ', '
    throw ("Target directory holds $($candidates.Count) buildable files " +
        "($names) -- name one explicitly.")
}

# MSBuild diagnostics come in two canonical shapes:
#   path(line,col): warning CODE: message [project]
#   path : warning CODE: message [project]
# The [project] suffix is why one physical warning appears once per project
# that compiles the file (the WPF temp project triples them); instances are
# therefore deduplicated ignoring project.
$script:FileDiagnostic = '^(?<file>.+?)\((?<line>\d+),\d+\):\s+' +
    '(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+):\s+(?<message>.*?)' +
    '(\s+\[[^\]]+\])?\s*$'
$script:BareDiagnostic = '^(?<file>[^:(]+?)\s*:\s+' +
    '(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+):\s+(?<message>.*?)' +
    '(\s+\[[^\]]+\])?\s*$'

function Read-BuildOutput {
    # Returns a comma-wrapped array of { file, line, severity, code, message },
    # deduplicated on everything but project.
    param([string[]]$Lines)

    $seen = @{}
    $found = foreach ($line in $Lines) {
        if ($line -match $script:FileDiagnostic -or
            $line -match $script:BareDiagnostic) {
            $lineNumber = if ($Matches.ContainsKey('line')) {
                [int]$Matches.line
            } else {
                $null
            }
            $entry = [pscustomobject]@{
                file     = $Matches.file.Trim()
                line     = $lineNumber
                severity = $Matches.severity
                code     = $Matches.code
                message  = $Matches.message
            }
            $key = '{0}|{1}|{2}|{3}' -f $entry.file, $entry.line,
                $entry.code, $entry.message
            if (-not $seen.ContainsKey($key)) {
                $seen[$key] = $true
                $entry
            }
        }
    }
    return ,@($found)
}

function Group-WarningInstance {
    # Returns a comma-wrapped array of { code, count, instances, omittedInstances }.
    # count is deduplicated instances; listing caps at $Cap per code and says so.
    param([object[]]$Warnings, [int]$Cap = 8)

    $groups = foreach ($group in ($Warnings | Group-Object code |
            Sort-Object Count -Descending)) {
        $instances = @($group.Group | ForEach-Object {
            [ordered]@{ file = $_.file; line = $_.line; message = $_.message }
        })
        [ordered]@{
            code             = $group.Name
            count            = $instances.Count
            instances        = @($instances | Select-Object -First $Cap)
            omittedInstances = [Math]::Max(0, $instances.Count - $Cap)
        }
    }
    return ,@($groups)
}

function Read-TrxDirectory {
    # Returns { total, passed, failed, skipped, failures, assemblies } summed
    # over every TRX in the directory -- dotnet test writes one per project.
    param([string]$Directory)

    $totals = [ordered]@{ total = 0; passed = 0; failed = 0; skipped = 0 }
    $failures = [System.Collections.Generic.List[object]]::new()
    $assemblies = [System.Collections.Generic.List[object]]::new()

    foreach ($trx in Get-ChildItem -LiteralPath $Directory -Filter *.trx) {
        [xml]$run = Get-Content -LiteralPath $trx.FullName -Raw
        $ns = [System.Xml.XmlNamespaceManager]::new($run.NameTable)
        $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

        $counters = $run.SelectSingleNode('//t:ResultSummary/t:Counters', $ns)
        $passed = [int]$counters.GetAttribute('passed')
        $failed = [int]$counters.GetAttribute('failed')
        $executed = [int]$counters.GetAttribute('executed')
        $total = [int]$counters.GetAttribute('total')
        $skipped = $total - $executed

        $totals.total += $total
        $totals.passed += $passed
        $totals.failed += $failed
        $totals.skipped += $skipped
        $assemblies.Add([ordered]@{
            name    = Get-TrxAssemblyName -Run $run -Namespace $ns -FallBack $trx.Name
            total   = $total
            passed  = $passed
            failed  = $failed
            skipped = $skipped
        })

        $failedResults = $run.SelectNodes(
            '//t:UnitTestResult[@outcome="Failed"]', $ns)
        foreach ($result in $failedResults) {
            $failures.Add((Read-TrxFailure -Result $result -Namespace $ns))
        }
    }

    return [ordered]@{
        succeeded  = ($totals.failed -eq 0)
        total      = $totals.total
        passed     = $totals.passed
        failed     = $totals.failed
        skipped    = $totals.skipped
        failures   = @($failures)
        assemblies = @($assemblies)
    }
}

function Get-TrxAssemblyName {
    param([xml]$Run, [System.Xml.XmlNamespaceManager]$Namespace, [string]$FallBack)

    $method = $Run.SelectSingleNode('//t:TestDefinitions/t:UnitTest/t:TestMethod', $Namespace)
    if ($null -ne $method) {
        $codeBase = $method.GetAttribute('codeBase')
        if ($codeBase) { return [IO.Path]::GetFileNameWithoutExtension($codeBase) }
    }
    return $FallBack
}

function Read-TrxFailure {
    # Message verbatim and whole -- it is the payload. Stack capped to the
    # top frames; the deepest frame is where the assert fired.
    param([System.Xml.XmlNode]$Result, [System.Xml.XmlNamespaceManager]$Namespace)

    $message = $null
    $stack = @()
    $info = $Result.SelectSingleNode('.//t:ErrorInfo', $Namespace)
    if ($null -ne $info) {
        $messageNode = $info.SelectSingleNode('t:Message', $Namespace)
        if ($null -ne $messageNode) { $message = $messageNode.InnerText }
        $stackNode = $info.SelectSingleNode('t:StackTrace', $Namespace)
        if ($null -ne $stackNode) {
            $stack = @($stackNode.InnerText -split "`r?`n" |
                ForEach-Object Trim | Where-Object { $_ } |
                Select-Object -First 4)
        }
    }
    return [ordered]@{
        test    = $Result.GetAttribute('testName')
        message = $message
        stack   = $stack
    }
}

function Write-TextReport {
    param($Report)

    $verdict = $Report.succeeded ? 'PASS' : 'FAIL'
    Write-Output "$verdict  $($Report.target) ($($Report.configuration))"

    $build = $Report.build
    $buildWord = $build.succeeded ? 'succeeded' : 'FAILED'
    Write-Output ("build $buildWord in $($build.durationSeconds)s, " +
        "$($build.warningCount) warning(s)")
    foreach ($diagnosticError in $build.errors) {
        Write-Output ("  error {0}: {1} ({2}:{3})" -f $diagnosticError.code,
            $diagnosticError.message, $diagnosticError.file, $diagnosticError.line)
    }
    foreach ($warning in $build.warnings) {
        Write-Output "  warning $($warning.code) x$($warning.count)"
    }

    if ($null -eq $Report.tests) {
        Write-Output 'tests: not run'
        return
    }
    $tests = $Report.tests
    Write-Output ("tests: $($tests.passed)/$($tests.total) passed, " +
        "$($tests.failed) failed, $($tests.skipped) skipped")
    foreach ($failure in $tests.failures) {
        Write-Output "  FAIL $($failure.test)"
        Write-Output "       $($failure.message)"
        foreach ($frame in $failure.stack) { Write-Output "       $frame" }
    }
}

$resolvedTarget = Resolve-BuildTarget -Given $Target

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$buildLines = @(& dotnet build $resolvedTarget --configuration $Configuration `
    -nologo 2>&1 | ForEach-Object { "$_" })
$buildSucceeded = ($LASTEXITCODE -eq 0)
$stopwatch.Stop()

$diagnostics = Read-BuildOutput -Lines $buildLines
$buildErrors = @($diagnostics | Where-Object severity -eq 'error')
$buildWarnings = @($diagnostics | Where-Object severity -eq 'warning')

$build = [ordered]@{
    succeeded       = $buildSucceeded
    durationSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
    errors          = @($buildErrors |
        ForEach-Object { [ordered]@{ file = $_.file; line = $_.line;
            code = $_.code; message = $_.message } })
    warnings        = Group-WarningInstance -Warnings $buildWarnings
    warningCount    = $buildWarnings.Count
}

$tests = $null
if ($buildSucceeded -and -not $NoTests) {
    $resultsDirectory = Join-Path ([IO.Path]::GetTempPath()) `
        ("janet-trx-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $resultsDirectory | Out-Null
    try {
        $testArguments = @($resolvedTarget, '--no-build',
            '--configuration', $Configuration, '-nologo',
            '--logger', 'trx', '--results-directory', $resultsDirectory)
        if ($TestFilter) { $testArguments += @('--filter', $TestFilter) }
        & dotnet test @testArguments *> $null
        $tests = Read-TrxDirectory -Directory $resultsDirectory
    }
    finally {
        Remove-Item -LiteralPath $resultsDirectory -Recurse -Force `
            -ErrorAction SilentlyContinue
    }
}

$succeeded = $buildSucceeded -and
    ($NoTests -or ($null -ne $tests -and $tests.succeeded))

$report = [ordered]@{
    contract      = 1
    target        = $resolvedTarget
    configuration = $Configuration
    succeeded     = $succeeded
    build         = $build
    tests         = $tests
}

if ($Text) {
    Write-TextReport -Report $report
} elseif ($Pretty) {
    $report | ConvertTo-Json -Depth 8
} else {
    $report | ConvertTo-Json -Depth 8 -Compress
}

exit ($succeeded ? 0 : 1)
