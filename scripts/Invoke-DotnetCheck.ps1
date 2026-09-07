# JANET-SHIM
<#
.SYNOPSIS
    Builds and tests a .NET target and reports the result as structured JSON instead of
    console scrollback.

.DESCRIPTION
    A shim. The implementation moved to Janet.Core and is reached through the `janet` CLI;
    this script forwards to it so every existing caller keeps working.

    dotnet's own output buries the payload: one real warning drowns in fifteen restore
    warnings, and a failed test's assert message takes three re-runs with different console
    filters to extract. This runs the build and the tests once and emits what a session
    actually needs, parsed with a JSON reader rather than grepped out of text.

    Errors are verbatim and never truncated. Warnings are deduplicated and grouped by code
    -- collapsed, never silently dropped, and the envelope reports what it omitted. Test
    failures carry their payload up front and are read from TRX files rather than scraped.
    The exit code means exactly one thing: 0 when the build succeeded and every test passed.

    THE CONTRACT IS NOW 7. Contract 4 added the 'status' discriminator, because the MCP tool
    can answer "running" with a handle when a rebuild outlasts the client's call timeout;
    this script only ever produces "complete" -- every invocation is a fresh process, so
    there is nobody to poll -- but the field is present. Contract 5 makes 'tests' carry the
    runner's verdict rather than recompute one from the TRX files it managed to parse:
    'runnerExitCode' is dotnet test's own exit code and forces succeeded false when non-zero,
    'abort' carries the runner's abort banner (the exception that killed a crashed test host,
    or null), and each assembly entry carries 'status' -- "aborted" marks a partial TRX whose
    counters are what the host lived to write, not a result. Before this, a crashed test host
    summed to 423/423 passing with exit 0 (notes\test-count-blind-spot.md). Contract 6 gives
    'graph' two fields: 'via' names the convention that answered ("script" for a .graph
    directory with scripts\graph.ps1, "razorgraph" for a graph held by the RazorGraph MCP
    server the repository's .mcp.json declares) and 'graphId' names the server-side graph.
    Under razorgraph a successful build rebuilds a graph that source has outrun, in place,
    so the graph tools stay current across the check loop.

    Contract 7 corrects contract 5 and extends it. The correction: 5 decided "aborted" from
    four independent signs OR'd together, and three of them fire on healthy runs. xUnit files
    its per-test "[FAIL]" line as a run-level RunInfo Error, so EVERY failing suite came back
    aborted with a [FAIL] line as its crash banner; and an assembly a --filter matched nothing
    in writes a TRX with no definitions and no counters, so a narrow filter over six
    assemblies came back as five crashes and a failed run. Abort now needs positive evidence
    -- the runner closing the file out as Aborted, never closing it out at all, or a RunInfo
    Error whose text actually reads as an abort banner -- and an assembly that ran nothing is
    'empty', which does NOT fail the run. The extension: 'tests' carries 'durationSeconds'
    (wall clock) and 'testTimeSeconds' (every test's own duration summed, which exceeds wall
    clock when collections run in parallel), a 'slowest' list of the costliest test classes,
    and 'resultsDirectory'; each assembly adds the same two times plus 'resultsFile'. The TRX
    files now OUTLIVE the run -- a bounded number of recent result directories is kept -- so a
    session can open one instead of re-running a suite to see what the envelope did not carry.
    The declared format is contracts\dotnet-check.schema.json.

    Baselines written under contract 3 are still read. The baseline file's format did not
    change when the envelope's did, and stamping both from one number would have discarded
    every baseline on disk and quietly lost the first comparison after the upgrade.

.PARAMETER Target
    A .sln/.slnx/.csproj file, or a directory containing exactly one. Defaults to the
    current directory.

.PARAMETER New
    Diff warnings against the previous -New baseline for this target and configuration.
    Forces a complete census (--no-incremental and --force), because a diff against an
    incremental build reports every later full build as all-new.

.PARAMETER Full
    Rebuild everything without the baseline machinery. Worth reaching for whenever the SHAPE
    of the build changed -- a project added or removed, a reference swapped, a target
    framework moved -- because an incremental run that skipped a project entirely and one
    that had nothing to say about it produce the identical green. Not hypothetical:
    DriveSurvey.App was absent from its .slnx while it was being written, and every check
    that session passed without once compiling it.

.PARAMETER NoGraph
    Skip the code-graph refresh. Only relevant in repositories carrying a convention: a
    .graph directory with scripts\graph.ps1, or a razorgraph server declared in .mcp.json.
#>
[CmdletBinding()]
param(
    [string]$Target = '.',
    [string]$Configuration = 'Debug',
    [switch]$NoTests,
    [string]$TestFilter = '',
    [switch]$New,
    [switch]$Full,
    [switch]$NoGraph,
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

$janet = Get-JanetCommand

$arguments = @('check', '--target', $Target, '--configuration', $Configuration)
if ($TestFilter) { $arguments += @('--test-filter', $TestFilter) }
if ($NoTests) { $arguments += '--no-tests' }
if ($New) { $arguments += '--new' }
if ($Full) { $arguments += '--full' }
if ($NoGraph) { $arguments += '--no-graph' }
if ($Text) { $arguments += '--text' }
if ($Pretty) { $arguments += '--pretty' }

& $janet @arguments

# Forwarded deliberately: this script's exit code is a contract of its own -- 0 iff the build
# succeeded and every test passed -- and callers gate on it.
exit $LASTEXITCODE
