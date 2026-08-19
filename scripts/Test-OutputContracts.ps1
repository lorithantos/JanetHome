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
    'az-token' = {
        param([string]$Janet, [string]$Root)

        # $Root is unused: this format is produced from a sign-in, not from anything in the
        # repo. Kept so every sampler has one shape.
        $null = $Root

        # THE ONLY SAMPLER HERE THAT DEPENDS ON THE MACHINE. Every other one builds what it
        # needs; this one needs a live 'az login', which a build agent will not have. So it
        # SKIPS rather than fails: a contract nobody could check must not read as a contract
        # that failed, and it must not read as one that passed either.
        $probe = @(& $Janet az token --scope arm 2>&1)
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{
                samples = @()
                skipped = "no Azure CLI sign-in on this machine ($($probe -join ' '))"
            }
        }

        # Two samples because the interesting half of this format is a key that is ABSENT by
        # default. Sampling only the metadata shape would never exercise the arm that carries
        # a token, and sampling only --raw would never prove the omission is legal.
        $metadata = $probe -join "`n"
        $literal = @(& $Janet az token --scope 'https://vault.azure.net/.default') -join "`n"

        return [pscustomobject]@{
            samples = @(
                [pscustomobject]@{ label = 'arm alias, metadata only'; json = $metadata }
                [pscustomobject]@{ label = 'literal scope, no alias'; json = $literal }
            )
        }
    }

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

    'thread-report' = {
        param([string]$Janet, [string]$Root)

        # Seeded from a fixture rather than the machine's real list: the live list is session
        # state, so sampling it would make this gate's result depend on what the last session
        # happened to be doing.
        $seed = Join-Path ([System.IO.Path]::GetTempPath()) ("janet-contract-thread-" + [guid]::NewGuid().ToString('N') + '.json')
        $empty = Join-Path ([System.IO.Path]::GetTempPath()) ("janet-contract-thread-empty-" + [guid]::NewGuid().ToString('N') + '.json')

        # One item with notes and refs, one bare, one completed: notesLead is exercised both
        # non-empty and empty, notesLength both above and at zero, and the done status is only
        # reachable through --all.
        $items = @'
[
  { "topic": "cache eviction", "status": "active", "refs": ["note.cache"], "next": "query the telemetry table", "notes": "\n\nRuled out the obvious: it isn't the TTL.\nSecond line, not carried." },
  { "topic": "cache warming", "status": "parked", "refs": [], "next": "", "notes": "" },
  { "topic": "finished thing", "status": "done", "refs": [], "next": "", "notes": "closed out" }
]
'@
        [System.IO.File]::WriteAllText($seed, $items, (New-Object System.Text.UTF8Encoding $false))
        [System.IO.File]::WriteAllText($empty, '[]', (New-Object System.Text.UTF8Encoding $false))

        try {
            $live = @(& $Janet thread report --path $seed) -join "`n"
            $all = @(& $Janet thread report --path $seed --all) -join "`n"
            $none = @(& $Janet thread report --path $empty) -join "`n"
        }
        finally {
            # A sampler that leaves temp files behind runs on every commit.
            foreach ($p in @($seed, $empty)) {
                if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue }
            }
        }

        return [pscustomobject]@{
            samples = @(
                [pscustomobject]@{ label = 'live items'; json = $live }
                [pscustomobject]@{ label = 'including completed'; json = $all }
                [pscustomobject]@{ label = 'empty list'; json = $none }
            )
        }
    }

    'dotnet-check' = {
        param([string]$Janet, [string]$Root)

        # The smallest target in the repo, and no tests or graph refresh: this runs on every
        # commit, and a gate that costs a full solution build is a gate people stop running.
        $project = Join-Path $Root 'src\Janet.Core\Janet.Core.csproj'
        if (-not (Test-Path $project)) { return [pscustomobject]@{ samples = @() } }

        $complete = @(& $Janet check --target $project --no-tests --no-graph) -join "`n"

        # ONLY THE 'complete' ARM IS SAMPLED. The running arm needs a build that outlasts the
        # grace period, and the CLI never produces one -- every invocation is a fresh process,
        # so there is nobody to poll and returning a handle would be useless. It is covered by
        # a unit test instead, and this comment is here so the gap is stated rather than
        # discovered.
        return [pscustomobject]@{
            samples = @([pscustomobject]@{ label = 'complete, build only'; json = $complete })
        }
    }
}

function Get-SchemaBody {
    <#
    .SYNOPSIS
        The schema with its $janet metadata block removed, canonicalised so that reformatting
        is not mistaken for a format change.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Text)

    $parsed = $Text | ConvertFrom-Json -AsHashtable
    $parsed.Remove('$janet')

    return ($parsed | ConvertTo-Json -Depth 30 -Compress)
}

$findings = @()
$pendingContracts = @()
$skippedContracts = @()
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
    # src\ is in the pathspec because a format no longer has to come from a script. The
    # C#-native contracts name their surface in src\, and a diff that could not see it would
    # report every one of them as "the schema changed but its surface did not".
    $changed = @(& $git diff --name-only $Against -- 'contracts' 'scripts' 'src' | Where-Object { $_ })
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

    # The files a format change has to drag with it. 'script' is the original spelling, and
    # every contract that began as PowerShell still uses it; 'surface' is the general form,
    # for a format born in C# with no script to name. Both are read, so a contract with two
    # front ends can list them, and neither is required -- a schema that names nothing is
    # caught below rather than crashing here, which is what it used to do.
    $surface = @(
        if ($meta.PSObject.Properties.Name -contains 'script') { $meta.script }
        if ($meta.PSObject.Properties.Name -contains 'surface') { $meta.surface }
    ) | ForEach-Object { $_ -replace '\\', '/' }

    if (@($surface).Count -eq 0) {
        $findings += [pscustomobject]@{
            contract = $name
            issue    = 'undeclared'
            detail   = 'The $janet block names neither a script nor a surface, so nothing says which code is allowed to change this format.'
        }
        continue
    }

    # A schema that did not exist at the reference commit is a NEW format, not a changed
    # one. Running the change rule on it would demand a bump from nothing and a script edit
    # that the new schema's own script may not need -- a gate that fires on correct work
    # gets disabled, which is worse than not having it.
    $previous = if ($Against) { (& $git show "${Against}:$relative" 2>$null) -join "`n" } else { '' }

    # Compared with $janet REMOVED from both sides. That block is metadata about the schema --
    # which script produces it, whether the code exists yet -- and editing it is not a format
    # change. Comparing whole files made clearing implemented:false demand a contract bump for
    # a change that altered no format at all, which is a gate firing on correct work.
    $formatChanged = $false
    if ($Against -and $previous -and $changed -contains $relative) {
        $formatChanged = (Get-SchemaBody $previous) -cne (Get-SchemaBody $schemaText)
    }

    if ($formatChanged) {
        $previousContract = ($previous | ConvertFrom-Json).'$janet'.contract

        if ($previousContract -eq $meta.contract) {
            $findings += [pscustomobject]@{
                contract = $name
                issue    = 'contract-not-bumped'
                detail   = "The schema changed but contract is still $($meta.contract). A format change bumps it; an engine change does not touch this file."
            }
        }

        # ANY of the declared surface moving satisfies this, not all of it. A format with a
        # CLI and a server front end is usually changed in one of them plus the serializer,
        # and demanding every listed path move would fire on correct work.
        $moved = @($surface | Where-Object { $changed -contains $_ })

        if ($moved.Count -eq 0) {
            $findings += [pscustomobject]@{
                contract = $name
                issue    = 'surface-unchanged'
                detail   = "The schema changed but none of $($surface -join ', ') did. A format change has to move its surface too -- the help describing the envelope, and the node it points at ($($meta.node))."
            }
        }
    }

    # --- shape check: the live envelope against the checked-in format -------------------

    if (-not $live) { continue }

    # A format may be agreed before the code that satisfies it -- writing the schema first
    # is what stops it becoming a description of whatever got built. Such a schema is
    # PENDING: reported every run, never counted as a pass, and self-clearing, because the
    # moment its script becomes a shim the port has shipped and the flag is a lie.
    $pending = ($meta.PSObject.Properties.Name -contains 'implemented') -and (-not $meta.implemented)

    if ($pending) {
        # The self-clearing trick reads the shim banner, so it only means anything for a
        # contract that names a SCRIPT. A C#-native pending format has no equivalent tell and
        # stays pending until someone clears the flag by hand, which is the honest outcome:
        # better a flag that must be cleared deliberately than one that clears itself wrongly.
        $scriptFile = if ($meta.PSObject.Properties.Name -contains 'script') {
            Join-Path $repoRoot $meta.script
        }

        $shipped = $scriptFile -and (Test-Path $scriptFile) -and
            ((Get-Content $scriptFile -TotalCount 1) -match 'JANET-SHIM')

        if ($shipped) {
            $findings += [pscustomobject]@{
                contract = $name
                issue    = 'pending-but-shipped'
                detail   = "Declared implemented:false, but $($meta.script) is already a shim -- the port landed and the flag was left behind. Clear it and add a sampler."
            }
        }
        else {
            $pendingContracts += $name
        }

        continue
    }

    if (-not $samplers.ContainsKey($name)) {
        $findings += [pscustomobject]@{ contract = $name; issue = 'no-sampler'; detail = "No sampler for '$name', so its shape was never checked. Add one rather than leaving the schema unverified." }
        continue
    }

    $sampled = & $samplers[$name] $janet $repoRoot

    # A sampler may report that it COULD NOT check, as distinct from checking and finding
    # nothing. The difference matters: one is an environment this run could not exercise, the
    # other is a format the code failed to produce. Collapsing them would let a machine with
    # no Azure login look identical to a broken serializer.
    $skip = if ($sampled.PSObject.Properties.Name -contains 'skipped') { $sampled.skipped }

    if ($skip) {
        $skippedContracts += [pscustomobject]@{ contract = $name; reason = $skip }
        continue
    }

    $samples = @($sampled.samples)

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

    # Named, not merely subtracted from the count. A pending format is one nobody has
    # verified, and a run reporting "0 failed" over a directory of them would be telling
    # the truth in the least useful way available.
    pending  = @($pendingContracts)

    # Named for the same reason pending is: a format this run could not exercise is not a
    # format this run approved, and a bare "0 failed" over one would be true and misleading.
    skipped  = @($skippedContracts)
    against  = $Against
    findings = @($findings)
}

if ($Text) {
    Write-Host "output contracts: $checked checked (sampled from the $($result.sampledFrom))" -ForegroundColor Cyan
    if (-not $live) { Write-Host '  janet is not on PATH -- SHAPE NOT CHECKED, only the change rule ran.' -ForegroundColor Yellow }
    foreach ($name in $pendingContracts) { Write-Host "  [pending] ${name}: format agreed, code not written. Not verified by anything." -ForegroundColor Yellow }
    foreach ($item in $skippedContracts) { Write-Host "  [skipped] $($item.contract): $($item.reason). Shape NOT checked this run." -ForegroundColor Yellow }
    foreach ($finding in $findings) { Write-Host "  [$($finding.issue)] $($finding.contract): $($finding.detail)" -ForegroundColor Red }
    if (@($findings).Count -eq 0) {
        $verified = $checked - @($pendingContracts).Count - @($skippedContracts).Count
        Write-Host "  $verified format(s) match what the code emits." -ForegroundColor Green
    }
}
else {
    $result | ConvertTo-Json -Depth 5 -Compress
}

if (@($findings).Count -gt 0) { exit 1 }

# Stated rather than implied, so a caller reading $LASTEXITCODE is not reading a value left
# by the git invocation above.
exit 0
