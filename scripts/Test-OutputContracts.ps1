<#
.SYNOPSIS
    Holds a tool's output format to the schema checked in beside it, and holds a schema
    change to being a deliberate one.

.DESCRIPTION
    Two checks, because format drift happens in two directions and only one of them is
    caught by validating output.

    SHAPE. Each contracts\*.schema.json is validated against a live envelope produced by
    the tool it describes. The schemas set additionalProperties:false, so a field added
    to the code turns this red until someone decides whether that was a format change.
    Deriving the schema from the code instead would make this check impossible: a schema
    the implementation writes agrees with the implementation by construction, which is
    the same reason a golden the implementation writes is not a golden.

    CHANGE. A schema is a FORMAT, not an engine. Changing it should be rare, and when it
    happens everything that describes the format has to move with it. So if a schema
    differs from the reference commit, this requires that the contract number inside it
    changed too, and that the script exposing the format changed in the same set. A
    schema edit with no script edit is either an engine change that leaked into the
    format, or a format change whose surface was left behind.

    JSON envelope by default; -Text to read at a terminal. Exit code 1 if any contract
    failed, 0 otherwise -- stated explicitly rather than inherited from the last command.

.PARAMETER Path
    The contracts directory. Defaults to contracts\ beside this script's repo root.

.PARAMETER Against
    Git ref to compare schemas with for the change check. Defaults to HEAD. Pass an
    empty string to skip the change check and validate shape only.

.EXAMPLE
    .\Test-OutputContracts.ps1
    {"checked":1,"failed":0,"live":true,"findings":[]}

.EXAMPLE
    .\Test-OutputContracts.ps1 -Text -Against HEAD~1
#>
[CmdletBinding()]
param(
    [string]$Path = '',
    [string]$Against = 'HEAD',
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Path) { $Path = Join-Path $repoRoot 'contracts' }

if (-not (Test-Path $Path)) {
    throw "No contracts directory at $Path. Nothing declares a format here."
}

. (Join-Path $PSScriptRoot 'JanetCli.Common.ps1')

# The command that produces a sample envelope for each contract. Held here rather than in
# the schema file because it is a test fixture, not part of the format: the format has to
# be readable by someone who never runs this.
$samplers = @{
    'assembly-api' = {
        param([string]$Janet, [string]$Root)

        $output = Join-Path $Root 'tests\Janet.Tests\bin\Debug\net10.0'
        $core = Join-Path $output 'Janet.Core.dll'
        $tests = Join-Path $output 'Janet.Tests.dll'

        # Returns an OBJECT carrying the list, not the list itself. A bare array return has
        # to choose between unrolling (which loses an empty result) and comma-wrapping
        # (which hands the caller a one-element array holding the array, so $_.json member
        # -enumerates to an Object[] that Test-Json refuses). Wrapping it sidesteps both.
        if (-not (Test-Path $core) -or -not (Test-Path $tests)) { return [pscustomobject]@{ samples = @() } }

        # More than one sample, because one envelope does not exercise one schema. The first
        # is broad and untruncated; the second is capped, so 'truncated' is seen true as well
        # as false, and it reaches the test assembly, which is the only one here declaring an
        # enum -- a 'kind' the schema lists and no Janet.Core sample would ever reach.
        $broad = @(& $Janet assembly --assembly $core --max-types 200 --compact) -join "`n"
        $kinds = @(& $Janet assembly --assembly $tests --type 'SurfaceProbe' --max-types 10 --compact) -join "`n"
        $capped = @(& $Janet assembly --assembly $tests --type 'SurfaceProbe' --max-types 2 --compact) -join "`n"

        return [pscustomobject]@{
            samples = @(
                [pscustomobject]@{ label = 'core, unfiltered'; json = $broad }
                [pscustomobject]@{ label = 'tests, every kind'; json = $kinds }
                [pscustomobject]@{ label = 'tests, capped'; json = $capped }
            )
        }
    }
}

$findings = @()
$checked = 0
$live = $true

# The REPO's build, not the installed tool. A gate that samples the global janet validates
# whatever was last packed and installed, so a change to the code in front of you passes
# until someone reinstalls -- which is the staleness note.janet-mcp-port already documents,
# arriving here as a gate that cannot fail. Falls back to the installed tool only when
# nothing is built, and the envelope says which one answered.
$janet = @(
    Join-Path $repoRoot 'src\Janet.Cli\bin\Debug\net10.0\janet.exe'
    Join-Path $repoRoot 'src\Janet.Cli\bin\Release\net10.0\janet.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

$source = 'repo build'

if (-not $janet) {
    $source = 'installed tool'
    try { $janet = Get-JanetCommand }
    catch { $live = $false }
}

$schemas = @(Get-ChildItem $Path -Filter '*.schema.json' -File | Sort-Object Name)

if ($schemas.Count -eq 0) {
    throw "No *.schema.json in $Path. An empty contracts directory reports nothing and looks like a pass."
}

# --- change check: a schema edit is a format change, and drags its surface with it ------

$changed = @()
if ($Against) {
    $git = & (Join-Path $PSScriptRoot 'git.ps1')
    $changed = @(& $git diff --name-only $Against -- 'contracts' 'scripts' | Where-Object { $_ })
}

foreach ($schemaFile in $schemas) {
    $checked++
    $name = $schemaFile.BaseName -replace '\.schema$', ''
    $schemaText = Get-Content $schemaFile.FullName -Raw
    $schema = $schemaText | ConvertFrom-Json

    if (-not ($schema.PSObject.Properties.Name -contains '$janet')) {
        $findings += [pscustomobject]@{ contract = $name; issue = 'undeclared'; detail = 'No $janet block: nothing says which script exposes this format.' }
        continue
    }

    $meta = $schema.'$janet'
    $relative = (Join-Path 'contracts' $schemaFile.Name) -replace '\\', '/'
    $scriptPath = $meta.script -replace '\\', '/'

    # A schema that did not exist at the reference commit is a NEW format, not a changed
    # one. Running the change rule on it would demand a bump from nothing and a script edit
    # that the new schema's own script may not need -- a gate that fires on correct work
    # gets disabled, which is worse than not having it.
    $previous = if ($Against) { (& $git show "${Against}:$relative" 2>$null) -join "`n" } else { '' }

    if ($Against -and $previous -and $changed -contains $relative) {
        $previousContract = ($previous | ConvertFrom-Json).'$janet'.contract

        if ($previousContract -eq $meta.contract) {
            $findings += [pscustomobject]@{
                contract = $name
                issue    = 'contract-not-bumped'
                detail   = "The schema changed but contract is still $($meta.contract). A format change bumps it; an engine change does not touch this file."
            }
        }

        if ($changed -notcontains $scriptPath) {
            $findings += [pscustomobject]@{
                contract = $name
                issue    = 'script-unchanged'
                detail   = "The schema changed but $($meta.script) did not. A format change has to move its surface too -- the help describing the envelope, and the node it points at ($($meta.node))."
            }
        }
    }

    # --- shape check: the live envelope against the checked-in format -------------------

    if (-not $live) { continue }

    if (-not $samplers.ContainsKey($name)) {
        $findings += [pscustomobject]@{ contract = $name; issue = 'no-sampler'; detail = "No sampler for '$name', so its shape was never checked. Add one rather than leaving the schema unverified." }
        continue
    }

    $samples = @((& $samplers[$name] $janet $repoRoot).samples)

    if ($samples.Count -eq 0) {
        $findings += [pscustomobject]@{ contract = $name; issue = 'no-sample'; detail = 'The sampler produced nothing -- build the solution first. Reported rather than passed: an unchecked schema is not a checked one.' }
        continue
    }

    foreach ($sample in $samples) {
        $errors = $null
        $valid = Test-Json -Json $sample.json -Schema $schemaText -ErrorVariable errors -ErrorAction SilentlyContinue

        if (-not $valid) {
            $findings += [pscustomobject]@{
                contract = $name
                issue    = 'shape'
                detail   = "[$($sample.label)] does not match the declared format: $(($errors | ForEach-Object { $_.ToString() }) -join '; ')"
            }
            continue
        }

        $emitted = ($sample.json | ConvertFrom-Json).contract
        if ($emitted -ne $meta.contract) {
            $findings += [pscustomobject]@{
                contract = $name
                issue    = 'contract-mismatch'
                detail   = "[$($sample.label)] stamps contract $emitted, the schema declares $($meta.contract)."
            }
        }
    }
}

$result = [pscustomobject]@{
    checked  = $checked
    failed   = @($findings).Count
    live     = $live

    # Which binary answered. A gate that sampled the installed tool instead of this build
    # would pass a change it never saw, so this is part of the verdict rather than trivia.
    sampledFrom = if ($live) { $source } else { 'nothing' }
    against  = $Against
    findings = @($findings)
}

if ($Text) {
    Write-Host "output contracts: $checked checked (sampled from the $($result.sampledFrom))" -ForegroundColor Cyan
    if (-not $live) { Write-Host '  janet is not on PATH -- SHAPE NOT CHECKED, only the change rule ran.' -ForegroundColor Yellow }
    foreach ($finding in $findings) { Write-Host "  [$($finding.issue)] $($finding.contract): $($finding.detail)" -ForegroundColor Red }
    if (@($findings).Count -eq 0) { Write-Host '  Every declared format matches what the code emits.' -ForegroundColor Green }
}
else {
    $result | ConvertTo-Json -Depth 5 -Compress
}

if (@($findings).Count -gt 0) { exit 1 }

# Stated rather than implied, so a caller reading $LASTEXITCODE is not reading a value left
# by the git invocation above.
exit 0
