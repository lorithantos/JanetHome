<#
.SYNOPSIS
    Verifies the standalone\ copies still match the pre-shim originals in git history.

.DESCRIPTION
    standalone\ holds the last self-contained version of each script that is now a shim
    over the janet CLI, so the repo stays usable by someone who does not want to build
    the .NET tools. A vendored copy that quietly diverges from what it claims to be is
    worse than no copy at all, so the claim is checkable rather than asserted.

    Two independent checks per file, because they fail differently:

    - The file on disk matches the SHA-256 recorded in standalone\origins.json. Catches
      an edit to the copy.
    - The recorded hash matches the blob at the recorded commit. Catches an edit to the
      RECORD -- a manifest that can be updated to match whatever the file now says
      verifies nothing.

    Both compare content normalised to LF, so the result does not depend on the
    checkout's core.autocrlf setting. In the repository the copies are byte-identical
    to their blobs.

    Git is required for the second check. Without it the first still runs and the
    envelope reports gitChecked:false -- a check that could not run says so rather
    than passing.

    Exit code 1 if any file drifted or could not be checked at all, 0 otherwise.

.PARAMETER Path
    The standalone directory. Defaults to standalone\ beside this script's repo root.

.PARAMETER Text
    Human-readable output instead of the JSON envelope.

.EXAMPLE
    .\Test-StandaloneScripts.ps1
    {"checked":10,"drifted":0,"gitChecked":true,"findings":[]}

.EXAMPLE
    .\Test-StandaloneScripts.ps1 -Text
#>
[CmdletBinding()]
param(
    [string]$Path = '',
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Path) { $Path = Join-Path $repoRoot 'standalone' }

$manifestPath = Join-Path $Path 'origins.json'
if (-not (Test-Path $manifestPath)) { throw "No origins.json in $Path -- nothing declares what these files are supposed to be." }

function Get-NormalisedHash {
    param([string]$Content)

    # LF-normalised, so a CRLF checkout and an LF one agree. -replace runs on the CR-LF
    # pair first; doing it the other way round would leave lone CRs behind.
    $normalised = $Content -replace "`r`n", "`n" -replace "`r", "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($normalised)

    return [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($bytes)).Replace('-', '').ToLowerInvariant()
}

$git = & (Join-Path $PSScriptRoot 'git.ps1') 2>$null
$gitChecked = -not [string]::IsNullOrWhiteSpace($git)

$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$findings = @()
$checked = 0

foreach ($entry in $manifest.PSObject.Properties) {
    $name = $entry.Name
    $record = $entry.Value
    $local = Join-Path $Path $name
    $checked++

    if (-not (Test-Path $local)) {
        $findings += [pscustomobject]@{ file = $name; issue = 'missing'; detail = 'Declared in origins.json but not on disk.' }
        continue
    }

    $actual = Get-NormalisedHash -Content (Get-Content $local -Raw -Encoding UTF8)
    if ($actual -ne $record.sha256) {
        $findings += [pscustomobject]@{
            file   = $name
            issue  = 'edited'
            detail = "On disk $($actual.Substring(0, 12)), origins.json says $($record.sha256.Substring(0, 12)). The copy is not what it claims to be."
        }
    }

    if (-not $gitChecked) { continue }

    $blob = & $git show "$($record.ref):$($record.source)" 2>$null
    if ($LASTEXITCODE -ne 0) {
        $findings += [pscustomobject]@{
            file   = $name
            issue  = 'unreachable'
            detail = "$($record.ref):$($record.source) is not in this clone -- shallow checkout, or the history was rewritten."
        }
        continue
    }

    $fromGit = Get-NormalisedHash -Content ((($blob) -join "`n") + "`n")
    if ($fromGit -ne $record.sha256) {
        $findings += [pscustomobject]@{
            file   = $name
            issue  = 'record-drifted'
            detail = "Blob at $($record.ref) hashes $($fromGit.Substring(0, 12)), origins.json says $($record.sha256.Substring(0, 12)). The record was changed, not the copy."
        }
    }
}

$result = [pscustomobject]@{
    checked     = $checked
    drifted     = @($findings).Count
    gitChecked  = $gitChecked
    findings    = @($findings)
}

if ($Text) {
    Write-Host "standalone: $checked file(s) checked" -ForegroundColor Cyan
    if (-not $gitChecked) { Write-Host '  git not found -- history check SKIPPED, only the on-disk hashes were verified.' -ForegroundColor Yellow }
    foreach ($finding in $findings) { Write-Host "  [$($finding.issue)] $($finding.file): $($finding.detail)" -ForegroundColor Red }
    if (@($findings).Count -eq 0) { Write-Host '  All copies match their recorded originals.' -ForegroundColor Green }
}
else {
    $result | ConvertTo-Json -Depth 4 -Compress
}

if (@($findings).Count -gt 0) { exit 1 }

# Stated rather than implied, so a caller reading $LASTEXITCODE is not reading a value
# left by the last git invocation above.
exit 0
