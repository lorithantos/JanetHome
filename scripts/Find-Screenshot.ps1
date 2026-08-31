<#
.SYNOPSIS
    Finds screenshots, wherever Windows and OneDrive have decided to put them.

.DESCRIPTION
    Answers "the user mentioned a screenshot; where is it" without guessing, which
    is the mistake this script exists to stop repeating. The obvious candidates --
    Pictures\Screenshots, Desktop, Downloads -- are all wrong often enough that
    trying them and giving up looks like the file does not exist.

    Four things make the location unguessable, and each one was hit for real:

      * IT IS A KNOWN FOLDER, NOT A PATH. The real location is recorded in the
        registry under a GUID, and reading it is the only way to be right on a
        machine that has moved it.
      * ONEDRIVE REDIRECTS IT. Pictures frequently lives under %OneDrive%, so a
        %USERPROFILE%-relative guess misses entirely.
      * ONEDRIVE SUFFIXES DUPLICATES. The folder on the machine this was written
        for is "Screenshots 1" -- with a space and a digit -- because OneDrive
        made a second one during a sync conflict. An exact-name match finds
        nothing.
      * THE NAME IS LOCALISED. "Schermafbeeldingen", "Captures d'ecran". Matching
        the English word is matching one locale.

    So: resolve the known folder from the registry, add the OneDrive and local
    Pictures roots, and search them by DEPTH rather than by folder name.

    Reports the roots it searched as well as what it found, because "nothing
    found" and "nothing found in the wrong places" look identical otherwise.

.PARAMETER Name
    Substring of the file name. A pasted "Screenshot 2026-08-31 163823.png" works,
    and so does just "163823".

.PARAMETER WithinHours
    Only files modified in the last N hours. Use it when the answer is "the one
    they just took".

.PARAMETER First
    Maximum results, newest first. Default 10.

.PARAMETER SearchDepth
    How far below each root to look. Default 2, which reaches
    Pictures\Screenshots 1\ without walking a whole photo library.

.PARAMETER Path
    Extra roots to search, in addition to the discovered ones.

.PARAMETER IncludeVideos
    Also search the Xbox Game Bar capture folder and match video extensions.

.PARAMETER Text
    Formatted output for a terminal. The default is JSON.

.PARAMETER Pretty
    Indent the JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Find-Screenshot.ps1" -Name 163823 -Text

.EXAMPLE
    & "$env:JanetBase\scripts\Find-Screenshot.ps1" -WithinHours 1 -First 3 -Text
    The one they just took.
#>
[CmdletBinding()]
param(
    [string]$Name,

    [double]$WithinHours = 0,

    [int]$First = 10,

    [int]$SearchDepth = 2,

    [string[]]$Path = @(),

    [switch]$IncludeVideos,

    [switch]$Text,

    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# KNOWNFOLDERID_Screenshots. The registry is the only place that knows where the
# user actually put it.
$screenshotsGuid = '{B7BEDE81-DF94-4682-A7D8-57A52620B86F}'
$shellFolderKeys = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders'
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders'
)

function Get-KnownFolder {
    # ASSIGN THE RESULT -- comma-wrapped so an empty result stays an array.
    param([string]$Guid)

    $found = @()
    foreach ($key in $shellFolderKeys) {
        if (-not (Test-Path $key)) { continue }

        $entry = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($null -eq $entry) { continue }
        if ($entry.PSObject.Properties.Name -notcontains $Guid) { continue }

        $value = [Environment]::ExpandEnvironmentVariables($entry.$Guid)
        if ($value -and (Test-Path $value)) { $found += $value }
    }

    return ,@($found)
}

function Get-SearchRoots {
    # ASSIGN THE RESULT -- comma-wrapped.
    $roots = @()
    $roots += Get-KnownFolder -Guid $screenshotsGuid

    $oneDrive = $env:OneDrive
    if (-not $oneDrive) { $oneDrive = $env:OneDriveConsumer }

    $candidates = @(
        $(if ($oneDrive) { Join-Path $oneDrive 'Pictures' })
        $(if ($oneDrive) { Join-Path $oneDrive 'Afbeeldingen' })
        (Join-Path $env:USERPROFILE 'Pictures')
        (Join-Path $env:USERPROFILE 'Desktop')
        (Join-Path $env:USERPROFILE 'Downloads')
    )

    if ($IncludeVideos) {
        $candidates += (Join-Path $env:USERPROFILE 'Videos')
        if ($oneDrive) { $candidates += (Join-Path $oneDrive 'Videos') }
    }

    $candidates += $Path

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { $roots += $candidate }
    }

    # Prune roots nested inside other roots, or every hit under both is reported
    # twice -- and they DO nest: the registry known folder resolves to
    # <OneDrive>\Pictures\Screenshots, which sits inside the Pictures root added
    # below it. Shortest first, so an ancestor is always considered before its
    # child and subsumes it.
    $unique = @()
    foreach ($root in ($roots | Select-Object -Unique | Sort-Object -Property Length)) {
        $resolved = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\')

        $nested = $false
        foreach ($kept in $unique) {
            if ($resolved -eq $kept -or
                $resolved.StartsWith($kept + '\', [StringComparison]::OrdinalIgnoreCase)) {
                $nested = $true
                break
            }
        }

        if (-not $nested) { $unique += $resolved }
    }

    return ,@($unique)
}

$extensions = if ($IncludeVideos) { '*.png', '*.jpg', '*.jpeg', '*.mp4', '*.mkv' } else { '*.png', '*.jpg', '*.jpeg' }
$searchRoots = Get-SearchRoots
$cutoff = if ($WithinHours -gt 0) { (Get-Date).AddHours(-$WithinHours) } else { [datetime]::MinValue }

$hits = @()
foreach ($root in $searchRoots) {
    $files = Get-ChildItem -LiteralPath $root -Include $extensions -Recurse -Depth $SearchDepth -File -ErrorAction SilentlyContinue
    foreach ($file in $files) {
        if ($Name -and $file.Name -notlike "*$Name*") { continue }
        if ($file.LastWriteTime -lt $cutoff) { continue }

        $hits += [PSCustomObject]@{
            path      = $file.FullName
            name      = $file.Name
            modified  = $file.LastWriteTime.ToString('s')
            sizeBytes = $file.Length
            root      = $root
        }
    }
}

# Deduplicated by path as well as by root: pruning nested roots should make this
# unnecessary, and it is cheap insurance against a symlink or a junction putting
# one file under two roots that do not look nested.
$ordered = @(
    $hits |
        Sort-Object -Property path -Unique |
        Sort-Object -Property modified -Descending |
        Select-Object -First $First)

$result = [ordered]@{
    ok            = $ordered.Count -gt 0
    returned      = $ordered.Count
    totalMatches  = $hits.Count
    truncated     = $hits.Count -gt $ordered.Count
    rootsSearched = $searchRoots
    files         = $ordered
}

if (-not $Text) {
    if ($Pretty) { [PSCustomObject]$result | ConvertTo-Json -Depth 4 }
    else { [PSCustomObject]$result | ConvertTo-Json -Depth 4 -Compress }
    if (-not $result.ok) { exit 1 }
    exit 0
}

if ($ordered.Count -eq 0) {
    Write-Host 'No screenshot matched.' -ForegroundColor Yellow
    Write-Host '  searched:'
    foreach ($root in $searchRoots) { Write-Host "    $root" -ForegroundColor DarkGray }
    Write-Host '  Widen with -SearchDepth, or add a root with -Path.' -ForegroundColor DarkGray
    exit 1
}

Write-Host "$($ordered.Count) of $($hits.Count) match, newest first:"
foreach ($hit in $ordered) {
    Write-Host "  $($hit.modified)  $([int]($hit.sizeBytes / 1KB)) KB  $($hit.path)"
}

exit 0
