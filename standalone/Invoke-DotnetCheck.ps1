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

    -New additionally diffs the warning census against the previous -New run
    of the same target and configuration, so a freshly introduced warning is
    one named field instead of a needle in the count. It forces a
    non-incremental build: an incremental no-op legitimately emits zero
    warnings, and a diff against an incomplete census would report every
    later full build as all-new. The baseline is a rebuildable cache under
    %LOCALAPPDATA%\Janet\dotnet-check, keyed by target + configuration; only
    a successful build overwrites it.

    In a repository that carries a code graph, a successful build also
    refreshes it when source has outrun it -- because the check loop is
    exactly what would otherwise bypass the repo's own build script and leave
    an agent querying a stale graph. Staleness is always REPORTED even where
    it cannot be fixed, since a confidently stale graph is worse than none.

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

.PARAMETER New
    Diff warnings against the previous -New baseline for this target and
    configuration. Forces --no-incremental and --force (slower, but a
    complete census of both compiler and restore warnings -- restore
    warnings replay only when restore actually runs). The diff is keyed on
    file|code|message -- a warning that merely moved lines is not new; the
    same message twice in one file merges to one key.

.PARAMETER Full
    Rebuild everything: --no-incremental and --force, without the baseline
    machinery -New carries. Use it when the question is "does the whole thing
    still build" rather than "what did I just change".

    Worth reaching for whenever the shape of the build changed -- a project
    added to or removed from the solution, a reference swapped from a project
    to a package, a target framework moved. An incremental build reports only
    on what MSBuild decided to touch, so a run that skipped a project entirely
    and a run that had nothing to say about it produce the identical green.
    That is not hypothetical: DriveSurvey.App was absent from its .slnx while
    it was being written, and every check that session passed without once
    compiling it.

.PARAMETER NoGraph
    Skip the code-graph refresh. Only relevant in repositories that carry the
    graph convention (a .graph directory and scripts\graph.ps1); elsewhere
    there is nothing to skip.

.PARAMETER Text
    Human-readable output instead of JSON.

.PARAMETER Pretty
    Indent the JSON. Debugging by eye only.

.OUTPUTS
    JSON: { contract, target, configuration, succeeded,
            build:  { succeeded, durationSeconds, errors[], warnings[],
                      warningCount, newWarnings, resolvedWarningCount,
                      baseline },
            tests:  null | { succeeded, total, passed, failed, skipped,
                             failures[], assemblies[] } }
    errors[]:      { file, line, code, message } -- every instance, verbatim.
    warnings[]:    { code, count, instances[], omittedInstances } -- instances
                   deduplicated; a positive omittedInstances is the honest
                   truncation marker.
    newWarnings:   null when no comparison happened (-New absent, or no prior
                   baseline); else every warning absent from the baseline,
                   verbatim and uncapped -- new warnings are error-like.
    resolvedWarningCount: baseline warnings gone this run; same nullability.
    baseline:      null unless -New; else { path, comparedTo, saved } where
                   comparedTo is the prior baseline's timestamp or null on
                   the first run, and saved says this run's census was
                   written back (only a successful build is).
    failures[]:    { test, message, stack[] } -- message verbatim and whole.
    tests is null when -NoTests was passed or the build failed; which one is
    visible from build.succeeded.
    graph:         null when the repository has no graph convention at all
                   (most of them) -- "not applicable", the same reading as a
                   null tests. Otherwise { path, builtAt, newestSourceAt,
                   status, refreshed, canRefresh } where status is current |
                   stale | absent | failed. 'stale' with canRefresh false
                   means the repo has a graph but no way to rebuild it here;
                   trust it accordingly.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-DotnetCheck.ps1" -Target D:\Repos\RetirementCore
    Build + full test run as compact JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-DotnetCheck.ps1" -Target .\App.sln -NoTests -Text
    Build only, human-readable.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-DotnetCheck.ps1" -TestFilter GridSortMemoryTests -Pretty
    One test class, indented JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-DotnetCheck.ps1" -Target D:\Repos\RetirementCore -New -NoTests
    Full-census build; newWarnings lists exactly what this session introduced.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-DotnetCheck.ps1" -Target D:\Repos\DriveSurvey -Full
    Rebuild everything and run the suite. The check to run after changing which
    projects the solution contains, or what they reference.
#>
[CmdletBinding()]
param(
    [string]$Target = '.',
    [string]$Configuration = 'Debug',
    [switch]$NoTests,
    [string]$TestFilter,
    [switch]$New,
    [switch]$Full,
    [switch]$NoGraph,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ContractVersion = 3

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
# The separator is ' : ' with a mandatory space before the colon -- that is
# what distinguishes it from the drive colon in an absolute Windows path.
$script:BareDiagnostic = '^(?<file>.+?)\s+:\s+' +
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
            # WPF's compile-time temp project carries a fresh random infix
            # every build (App_k0iqikfl_wpftmp.csproj); left alone it would
            # defeat dedup now and read as new+resolved on every -New diff.
            # Its diagnostics are duplicates of the source project's, so
            # stripping the infix folds them into it.
            $entry = [pscustomobject]@{
                file     = $Matches.file.Trim() -replace '_[a-z0-9]+_wpftmp(?=\.)', ''
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

function Get-BaselinePath {
    # One baseline per (target, configuration): Release and Debug censuses
    # differ legitimately and must not diff against each other.
    param([string]$ResolvedTarget, [string]$BuildConfiguration)

    $keySource = "$ResolvedTarget|$BuildConfiguration".ToLowerInvariant()
    $digest = [BitConverter]::ToString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.Encoding]::UTF8.GetBytes($keySource)))
    $stem = [IO.Path]::GetFileNameWithoutExtension($ResolvedTarget)
    $directory = Join-Path $env:LOCALAPPDATA 'Janet\dotnet-check'
    return Join-Path $directory `
        ("{0}-{1}.json" -f $stem, $digest.Replace('-', '').Substring(0, 12))
}

function Read-WarningBaseline {
    # Returns the previous census, or $null when absent, unreadable, or
    # stamped with a different contract -- a stale format is the same as no
    # baseline, never a wrong comparison.
    param([string]$Path, [int]$Contract)

    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $baseline = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch { return $null }

    $fields = $baseline.PSObject.Properties.Name
    if ($fields -notcontains 'contract' -or $baseline.contract -ne $Contract -or
        $fields -notcontains 'warnings') {
        return $null
    }
    return $baseline
}

function Get-WarningKey {
    # Line is deliberately excluded: an edit that merely moves a warning must
    # not resurrect it as new. Cost: the same message twice in one file
    # merges to one key.
    param($Warning)
    return '{0}|{1}|{2}' -f $Warning.file.ToLowerInvariant(),
        $Warning.code, $Warning.message
}

function Compare-WarningBaseline {
    # Returns { newWarnings, resolvedWarningCount } against the prior census.
    param([object[]]$Current, $Baseline)

    $priorKeys = @{}
    foreach ($warning in @($Baseline.warnings)) {
        $priorKeys[(Get-WarningKey $warning)] = $true
    }
    $currentKeys = @{}
    $fresh = foreach ($warning in $Current) {
        $key = Get-WarningKey $warning
        $currentKeys[$key] = $true
        if (-not $priorKeys.ContainsKey($key)) {
            [ordered]@{ file = $warning.file; line = $warning.line;
                code = $warning.code; message = $warning.message }
        }
    }
    $resolved = @($priorKeys.Keys | Where-Object { -not $currentKeys.ContainsKey($_) })

    return [ordered]@{
        newWarnings          = @($fresh)
        resolvedWarningCount = $resolved.Count
    }
}

function Save-WarningBaseline {
    param([string]$Path, [int]$Contract, [string]$ResolvedTarget,
        [string]$BuildConfiguration, [object[]]$Warnings)

    $directory = Split-Path $Path -Parent
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [ordered]@{
        contract      = $Contract
        target        = $ResolvedTarget
        configuration = $BuildConfiguration
        savedAt       = (Get-Date).ToString('o')
        warnings      = @($Warnings | ForEach-Object {
            [ordered]@{ file = $_.file; line = $_.line;
                code = $_.code; message = $_.message } })
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Find-RepositoryRoot {
    # The graph belongs to a repository, not to a project file: walk up from
    # the build target until a .git directory appears.
    param([string]$StartPath)

    $current = Split-Path -Parent $StartPath

    while ($current) {
        if (Test-Path -LiteralPath (Join-Path $current '.git')) { return $current }

        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { return $null }
        $current = $parent
    }

    return $null
}

function Get-GraphState {
    <#
    Reports where the repository's code graph stands relative to its source.
    Returns $null when the repository has no graph convention at all -- most
    repositories -- so the field reads as "not applicable" rather than
    "missing", the same way tests does.

    Convention, deliberately owned by the REPOSITORY and merely honoured
    here: a .graph directory holds the generated graph, and scripts\graph.ps1
    regenerates it. Keeping the solution path, output path, and analyzer
    resolution in the repo's own script is why this stays repo-agnostic.
    #>
    param([string]$RepositoryRoot)

    # NOT '$null -eq': a [string] parameter converts $null to empty string on
    # binding, so the null check never fires and Join-Path throws on ''. The
    # caller genuinely passes null whenever the target sits outside a repo.
    if ([string]::IsNullOrEmpty($RepositoryRoot)) { return $null }

    $graphDirectory = Join-Path $RepositoryRoot '.graph'
    $refreshScript = Join-Path $RepositoryRoot 'scripts\graph.ps1'
    $hasDirectory = Test-Path -LiteralPath $graphDirectory
    $hasScript = Test-Path -LiteralPath $refreshScript

    if (-not $hasDirectory -and -not $hasScript) { return $null }

    $graphFile = $null

    if ($hasDirectory) {
        $graphFile = Get-ChildItem -LiteralPath $graphDirectory -Filter *.json -File |
            Sort-Object Length -Descending |
            Select-Object -First 1
    }

    $newest = Get-NewestSourceWrite -RepositoryRoot $RepositoryRoot

    $state = [ordered]@{
        path           = $null -ne $graphFile ? $graphFile.FullName : $null
        builtAt        = $null -ne $graphFile ? $graphFile.LastWriteTimeUtc.ToString('o') : $null
        newestSourceAt = $null -ne $newest ? $newest.ToString('o') : $null
        status         = 'absent'
        refreshed      = $false
        canRefresh     = $hasScript
    }

    if ($null -ne $graphFile) {
        $outrun = $null -ne $newest -and $newest -gt $graphFile.LastWriteTimeUtc
        $state.status = $outrun ? 'stale' : 'current'
    }

    return $state
}

function Get-NewestSourceWrite {
    # Newest write across the sources a code graph would analyze. Build output
    # is excluded: obj\ regenerates on every build and would make every graph
    # look stale the moment it was written.
    param([string]$RepositoryRoot)

    $newest = $null

    $files = Get-ChildItem -LiteralPath $RepositoryRoot -File -Recurse `
        -Include *.cs, *.xaml, *.razor, *.cshtml -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git|\.graph)\\' }

    foreach ($file in $files) {
        if ($null -eq $newest -or $file.LastWriteTimeUtc -gt $newest) {
            $newest = $file.LastWriteTimeUtc
        }
    }

    return $newest
}

function Update-CodeGraph {
    <#
    Delegates to the repository's own graph script and reports what happened.
    Never throws and never fails the check: a graph is a convenience, and the
    repo script already treats a missing analyzer as a skip. Mirrors that
    posture rather than second-guessing it.
    #>
    param([string]$RepositoryRoot, $State)

    $refreshScript = Join-Path $RepositoryRoot 'scripts\graph.ps1'

    try {
        & $refreshScript -Quiet *> $null
    }
    catch {
        $State.status = 'failed'
        return
    }

    $graphFile = Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot '.graph') `
        -Filter *.json -File -ErrorAction SilentlyContinue |
        Sort-Object Length -Descending |
        Select-Object -First 1

    if ($null -eq $graphFile) {
        $State.status = 'failed'
        return
    }

    $State.path = $graphFile.FullName
    $State.builtAt = $graphFile.LastWriteTimeUtc.ToString('o')
    $State.status = 'current'
    $State.refreshed = $true
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
    if ($null -ne $build.newWarnings) {
        Write-Output ("new warnings: $(@($build.newWarnings).Count), " +
            "resolved: $($build.resolvedWarningCount)")
        foreach ($fresh in $build.newWarnings) {
            Write-Output ("  NEW {0}: {1} ({2}:{3})" -f $fresh.code,
                $fresh.message, $fresh.file, $fresh.line)
        }
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

$buildArguments = @($resolvedTarget, '--configuration', $Configuration, '-nologo')
# A diff is only meaningful against a complete census. --no-incremental
# recompiles everything (CSxxxx come back); --force re-runs restore (NUxxxx
# are replayed only by a real restore, and a baseline missing them would
# report every ancient restore warning as new on the next real one).
#
# -Full asks for the same rebuild without the baseline machinery, because
# "recompile everything" and "diff the warning census" are separate wants and
# -New was the only way to get the first. An incremental build reports on the
# projects MSBuild decided to touch, so a run that skipped everything and a run
# that genuinely had nothing to say are the same green -- which is how a project
# missing from the solution manifest stayed invisible across a whole session.
if ($New -or $Full) { $buildArguments += @('--no-incremental', '--force') }

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$buildLines = @(& dotnet build @buildArguments 2>&1 | ForEach-Object { "$_" })
$buildSucceeded = ($LASTEXITCODE -eq 0)
$stopwatch.Stop()

$diagnostics = Read-BuildOutput -Lines $buildLines
$buildErrors = @($diagnostics | Where-Object severity -eq 'error')
$buildWarnings = @($diagnostics | Where-Object severity -eq 'warning')

$newWarnings = $null
$resolvedWarningCount = $null
$baselineReport = $null
if ($New) {
    $baselinePath = Get-BaselinePath -ResolvedTarget $resolvedTarget `
        -BuildConfiguration $Configuration
    $priorBaseline = Read-WarningBaseline -Path $baselinePath `
        -Contract $script:ContractVersion
    if ($null -ne $priorBaseline) {
        $diff = Compare-WarningBaseline -Current $buildWarnings -Baseline $priorBaseline
        $newWarnings = $diff.newWarnings
        $resolvedWarningCount = $diff.resolvedWarningCount
    }
    if ($buildSucceeded) {
        Save-WarningBaseline -Path $baselinePath -Contract $script:ContractVersion `
            -ResolvedTarget $resolvedTarget -BuildConfiguration $Configuration `
            -Warnings $buildWarnings
    }
    $baselineReport = [ordered]@{
        path       = $baselinePath
        comparedTo = ($null -ne $priorBaseline) ? $priorBaseline.savedAt : $null
        saved      = $buildSucceeded
    }
}

$build = [ordered]@{
    succeeded            = $buildSucceeded
    durationSeconds      = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
    errors               = @($buildErrors |
        ForEach-Object { [ordered]@{ file = $_.file; line = $_.line;
            code = $_.code; message = $_.message } })
    warnings             = Group-WarningInstance -Warnings $buildWarnings
    warningCount         = $buildWarnings.Count
    newWarnings          = $newWarnings
    resolvedWarningCount = $resolvedWarningCount
    baseline             = $baselineReport
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

# The graph describes source structure, so a failed build is the one state
# worth refusing to graph: the analyzer would either fail too or record a
# tree that never compiled. Stale-only refresh keeps a tight edit loop from
# paying for a graph nothing changed.
$graph = Get-GraphState -RepositoryRoot (Find-RepositoryRoot -StartPath $resolvedTarget)

$refreshWanted = $null -ne $graph -and
    -not $NoGraph -and
    $buildSucceeded -and
    $graph.canRefresh -and
    $graph.status -ne 'current'

if ($refreshWanted) {
    Update-CodeGraph `
        -RepositoryRoot (Find-RepositoryRoot -StartPath $resolvedTarget) -State $graph
}

$succeeded = $buildSucceeded -and
    ($NoTests -or ($null -ne $tests -and $tests.succeeded))

$report = [ordered]@{
    contract      = $script:ContractVersion
    target        = $resolvedTarget
    configuration = $Configuration
    succeeded     = $succeeded
    build         = $build
    tests         = $tests
    graph         = $graph
}

if ($Text) {
    Write-TextReport -Report $report
} elseif ($Pretty) {
    $report | ConvertTo-Json -Depth 8
} else {
    $report | ConvertTo-Json -Depth 8 -Compress
}

exit ($succeeded ? 0 : 1)
