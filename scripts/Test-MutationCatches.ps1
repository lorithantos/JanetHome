<#
.SYNOPSIS
    Proves a test suite would actually catch the defects it claims to, by breaking
    the code on purpose and requiring the suite to go red.

.DESCRIPTION
    A green suite is not evidence. It is only evidence about the defects it can
    distinguish from correct behaviour, and nothing in a test run reports which
    ones those are. This repo already knows that -- pattern.reviewer-principles
    says tests must be able to fail, note.golden-tests records forty green results
    standing for no verification, and both Test-OutputContracts and
    Test-StandaloneScripts carry a caveat saying they were verified by mutation.
    All of that verification was done by hand, once, and is now a claim in a
    comment. This runs it.

    Given a JSON plan of mutations, for each one it:

      1. Applies the mutation to a source file.
      2. REFUSES A MUTATION THAT CHANGES NOTHING. A find/replace whose result is
         byte-identical to the original tests nothing and passes, which reads
         exactly like a suite that caught nothing. This is not hypothetical: a
         do { X } while (false) wrapper around an awaited call, written as a
         mutation on 2026-08-14, was semantically identical to X and its green
         result was mistaken for evidence.
      3. Runs the suite and requires it to FAIL, recording which tests failed --
         so the plan says not merely that something caught it but what.
      4. Restores the file from the bytes read before the edit, and verifies the
         restore by hash.

    A baseline run must be green before anything is touched: failures cannot be
    attributed to a mutation in a suite that was already red. A final run must be
    green again, which is what proves every restore landed.

    THIS SCRIPT EDITS SOURCE FILES IN PLACE. It restores from an in-memory copy in
    a finally block and verifies by SHA-256, and if a restore ever fails it writes
    the original beside the file as .mutation-original and says so loudly rather
    than continuing. Run it on a clean working tree so git is a second way back.

    Exit code 1 if any mutation was not caught, was a no-op, could not be applied,
    or could not be restored. 0 only when every mutation was caught and every file
    is back to its original bytes.

.PARAMETER Plan
    Path to the JSON plan. Shape:

        {
          "target": "D:\\Repos\\Janet.Shared\\Janet.Shared.slnx",
          "configuration": "Release",
          "mutations": [
            {
              "name": "drop the key filter",
              "path": "src/Janet.Coalescing/ChangeCoalescer.cs",
              "find":    "if (Array.IndexOf(registration.Keys, posting.Key) >= 0)",
              "replace": "if (true)",
              "filter": "ChangeCoalescerTests"
            }
          ]
        }

    A relative 'path' resolves against the TARGET's directory -- the repo being
    mutated -- not the plan's, so a plan can live anywhere. Set "root" at the top
    level to override. 'filter'
    is optional and narrows the run for that mutation only; omit it to run the
    whole suite. 'occurrences' may be "all" to allow a find that matches more than
    once -- the default refuses an ambiguous match rather than guessing which.

.PARAMETER Target
    Overrides the plan's target, for running the same plan against another
    checkout.

.PARAMETER Configuration
    Overrides the plan's configuration. Defaults to Release.

.PARAMETER Text
    Human-readable output instead of the JSON envelope.

.EXAMPLE
    .\Test-MutationCatches.ps1 -Plan .\mutations\coalescer.json -Text

.EXAMPLE
    .\Test-MutationCatches.ps1 -Plan .\mutations\coalescer.json
    {"checked":2,"caught":2,"findings":[]}
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Plan,
    [string]$Target = '',
    [string]$Configuration = '',
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Prop {
    # Optional JSON fields are ABSENT on a PSCustomObject, not null, and reading
    # one under StrictMode throws before any validation can report the real
    # problem (house rules 2).
    param($Object, [string]$Name, $Default = $null)

    if ($null -eq $Object) { return $Default }
    if ($Object.PSObject.Properties.Name -notcontains $Name) { return $Default }
    if ($null -eq $Object.$Name) { return $Default }

    return $Object.$Name
}

function Get-Sha256 {
    param([byte[]]$Bytes)

    return [System.BitConverter]::ToString(
        [System.Security.Cryptography.SHA256]::HashData($Bytes)).Replace('-', '').ToLowerInvariant()
}

<#
    Runs the suite and returns what happened. Uses Invoke-DotnetCheck rather than
    scraping dotnet's console output: it already reports build and test results as
    a structured envelope whose exit code means exactly one thing.
#>
function Invoke-Suite {
    param([string]$SuiteTarget, [string]$SuiteConfiguration, [string]$Filter)

    $checkScript = Join-Path $PSScriptRoot 'Invoke-DotnetCheck.ps1'
    $arguments = @{ Target = $SuiteTarget; Configuration = $SuiteConfiguration; NoGraph = $true }
    if ($Filter) { $arguments['TestFilter'] = $Filter }

    $raw = & $checkScript @arguments 2>&1 | Out-String
    $envelope = $null
    try { $envelope = $raw | ConvertFrom-Json } catch { $envelope = $null }

    if ($null -eq $envelope -or (Get-Prop $envelope 'status') -ne 'complete') {
        return [pscustomobject]@{ Ran = $false; Green = $false; Failed = @(); Detail = $raw.Trim() }
    }

    $tests = Get-Prop $envelope 'tests'
    $failures = @(Get-Prop $tests 'failures' @())

    $names = @($failures | ForEach-Object {
        $name = Get-Prop $_ 'test'
        if (-not $name) { $name = Get-Prop $_ 'name' }
        if (-not $name) { $name = Get-Prop $_ 'fullName' }
        if ($name) { $name }
    })

    return [pscustomobject]@{
        Ran    = $true
        Green  = [bool](Get-Prop $envelope 'succeeded' $false)
        Failed = $names
        Detail = ''
    }
}

# ---- the plan ---------------------------------------------------------------

if (-not (Test-Path $Plan)) { throw "No mutation plan at $Plan." }

$document = Get-Content $Plan -Raw -Encoding UTF8 | ConvertFrom-Json

if (-not $Target) { $Target = [string](Get-Prop $document 'target' '') }
if (-not $Configuration) { $Configuration = [string](Get-Prop $document 'configuration' 'Release') }

if (-not $Target) { throw "The plan names no target and none was supplied." }
if (-not (Test-Path $Target)) { throw "Target $Target does not exist." }

$mutations = @(Get-Prop $document 'mutations' @())
if ($mutations.Count -eq 0) { throw "The plan lists no mutations; there is nothing to prove." }

# Relative paths belong to the repo being mutated, not to wherever the plan is
# kept -- a plan written into a scratch directory named nothing at all until this
# defaulted to the target instead.
$planRoot = [string](Get-Prop $document 'root' '')
if (-not $planRoot) { $planRoot = Split-Path (Resolve-Path $Target) -Parent }
if (-not (Test-Path $planRoot)) { throw "Root $planRoot does not exist." }

$findings = @()
$results = @()

# ---- baseline ---------------------------------------------------------------

$baseline = Invoke-Suite -SuiteTarget $Target -SuiteConfiguration $Configuration -Filter ''

if (-not $baseline.Ran) {
    $findings += [pscustomobject]@{
        mutation = '(baseline)'
        kind     = 'suite-unavailable'
        detail   = "The suite could not be run at all, so nothing here is a check that passed. $($baseline.Detail)"
    }
}
elseif (-not $baseline.Green) {
    $findings += [pscustomobject]@{
        mutation = '(baseline)'
        kind     = 'baseline-red'
        detail   = 'The suite is already failing, so a failure after a mutation proves nothing about the mutation.'
    }
}

# ---- mutate, run, restore ---------------------------------------------------

if ($findings.Count -eq 0) {
    foreach ($mutation in $mutations) {
        $name = [string](Get-Prop $mutation 'name' '(unnamed)')
        $relative = [string](Get-Prop $mutation 'path' '')
        $find = [string](Get-Prop $mutation 'find' '')
        $replace = [string](Get-Prop $mutation 'replace' '')
        $filter = [string](Get-Prop $mutation 'filter' '')
        $occurrences = [string](Get-Prop $mutation 'occurrences' 'one')

        $file = if ([System.IO.Path]::IsPathRooted($relative)) { $relative } else { Join-Path $planRoot $relative }

        if (-not $relative -or -not (Test-Path $file)) {
            $findings += [pscustomobject]@{ mutation = $name; kind = 'file-missing'; detail = "No file at $file." }
            continue
        }

        $originalBytes = [System.IO.File]::ReadAllBytes($file)
        $originalHash = Get-Sha256 $originalBytes
        $original = [System.IO.File]::ReadAllText($file)

        $matches = ([regex]::Matches($original, [regex]::Escape($find))).Count

        if (-not $find -or $matches -eq 0) {
            $findings += [pscustomobject]@{ mutation = $name; kind = 'not-found'; detail = "The text to replace does not appear in $relative." }
            continue
        }

        if ($matches -gt 1 -and $occurrences -ne 'all') {
            $findings += [pscustomobject]@{
                mutation = $name
                kind     = 'ambiguous'
                detail   = "'find' matches $matches times in $relative. Narrow it, or set occurrences to 'all' to mean it."
            }
            continue
        }

        $mutated = if ($occurrences -eq 'all') {
            $original.Replace($find, $replace)
        }
        else {
            $at = $original.IndexOf($find, [System.StringComparison]::Ordinal)
            $original.Substring(0, $at) + $replace + $original.Substring($at + $find.Length)
        }

        # THE CHECK THAT MAKES THE REST WORTH ANYTHING. A mutation that leaves the
        # file identical runs the suite against unchanged code, and its green
        # result is indistinguishable from a suite that caught nothing.
        if ($mutated -eq $original) {
            $findings += [pscustomobject]@{
                mutation = $name
                kind     = 'no-op'
                detail   = 'The mutation leaves the file byte-identical, so the run that follows would prove nothing.'
            }
            continue
        }

        $outcome = $null
        $restored = $false

        try {
            [System.IO.File]::WriteAllText($file, $mutated, (New-Object System.Text.UTF8Encoding $false))
            $outcome = Invoke-Suite -SuiteTarget $Target -SuiteConfiguration $Configuration -Filter $filter
        }
        finally {
            [System.IO.File]::WriteAllBytes($file, $originalBytes)
            $restored = (Get-Sha256 ([System.IO.File]::ReadAllBytes($file))) -eq $originalHash
        }

        if (-not $restored) {
            $rescue = "$file.mutation-original"
            [System.IO.File]::WriteAllBytes($rescue, $originalBytes)
            $findings += [pscustomobject]@{
                mutation = $name
                kind     = 'restore-failed'
                detail   = "$relative was NOT restored. The original bytes are at $rescue. Stopping; do not trust anything below."
            }
            break
        }

        if (-not $outcome.Ran) {
            $findings += [pscustomobject]@{ mutation = $name; kind = 'suite-unavailable'; detail = $outcome.Detail }
            continue
        }

        if ($outcome.Green) {
            $findings += [pscustomobject]@{
                mutation = $name
                kind     = 'not-caught'
                detail   = "The suite stayed green with $relative broken. Nothing tests this behaviour."
            }
            continue
        }

        $results += [pscustomobject]@{
            mutation = $name
            file     = $relative
            caughtBy = @($outcome.Failed)
        }
    }
}

# ---- everything back? -------------------------------------------------------

$final = $null
if ($findings.Count -eq 0) {
    $final = Invoke-Suite -SuiteTarget $Target -SuiteConfiguration $Configuration -Filter ''

    if (-not $final.Green) {
        $findings += [pscustomobject]@{
            mutation = '(final)'
            kind     = 'not-restored'
            detail   = 'The suite is red after every mutation was reverted, so something did not go back. Check the working tree before trusting this run.'
        }
    }
}

$envelopeOut = [pscustomobject]@{
    target        = $Target
    configuration = $Configuration
    checked       = $mutations.Count
    caught        = $results.Count
    caughtDetail  = $results
    findings      = $findings
}

if ($Text) {
    Write-Output "$($envelopeOut.caught) of $($envelopeOut.checked) mutations were caught. Target: $Target ($Configuration)."
    Write-Output ''

    foreach ($result in $results) {
        $by = if ($result.caughtBy.Count -gt 0) { $result.caughtBy -join ', ' } else { '(the suite went red; no test names reported)' }
        Write-Output "  CAUGHT   $($result.mutation)  [$($result.file)]"
        Write-Output "           by: $by"
    }

    foreach ($finding in $findings) {
        Write-Output "  PROBLEM  $($finding.mutation)  ($($finding.kind))"
        Write-Output "           $($finding.detail)"
    }

    if ($findings.Count -eq 0) { Write-Output '' ; Write-Output 'Every mutation was caught and every file is back to its original bytes.' }
}
else {
    Write-Output ($envelopeOut | ConvertTo-Json -Depth 6 -Compress)
}

exit ($findings.Count -eq 0 ? 0 : 1)
