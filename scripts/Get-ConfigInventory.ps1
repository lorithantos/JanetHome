<#
.SYNOPSIS
    Inventories configuration files (JSON, YAML, XML) under a directory.

.DESCRIPTION
    Scans a directory tree for config files, groups them by directory and
    extension, shows key counts and common structure patterns. Useful for
    assessing config migration scope (e.g., how many JSON job configs need
    converting to a new format).

.PARAMETER Path
    Root directory to scan. Defaults to current directory.

.PARAMETER OutFile
    Optional file path to write the markdown report to.

.PARAMETER Include
    File patterns to include. Defaults to *.json, *.yaml, *.yml, *.xml, *.config.

.PARAMETER Exclude
    Directory names to exclude. Defaults to obj, bin, .gdn, node_modules.

.PARAMETER SampleKeys
    If set, reads each JSON file and lists its top-level keys to detect structure patterns.

.PARAMETER ListFiles
    If set, emits a per-file table (original behavior). Default is directory-level summary only.

.PARAMETER MaxPatternFiles
    Maximum file names to list per JSON structure pattern before truncating. Default 5.

.EXAMPLE
    Get-ConfigInventory.ps1 -Path <repo-root>\SampleApp.Jobs\Jobs -SampleKeys
    Get-ConfigInventory.ps1 -Path <repo-root>\SampleApp.Web -SampleKeys -MaxPatternFiles 3
    Get-ConfigInventory.ps1 -Path <repo-root>\SampleApp.Web -ListFiles
#>
[CmdletBinding()]
param(
    [string]$Path = '.',
    [string]$OutFile,
    [string[]]$Include = @('*.json', '*.yaml', '*.yml', '*.xml', '*.config'),
    [string[]]$Exclude = @('obj', 'bin', '.gdn', 'node_modules', '.vs', 'packages'),
    [switch]$SampleKeys,
    [switch]$ListFiles,
    [int]$MaxPatternFiles = 5
)

$ErrorActionPreference = 'Stop'
$Path = Resolve-Path $Path
$dirName = Split-Path $Path -Leaf

$excludePattern = ($Exclude | ForEach-Object { [regex]::Escape($_) }) -join '|'
$excludeRegex = "[\\/]($excludePattern)[\\/]"

$files = foreach ($pattern in $Include) {
    Get-ChildItem -Path $Path -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch $excludeRegex }
}
$files = $files | Sort-Object FullName -Unique

if (-not $files) {
    Write-Warning "No config files found under $Path"
    return
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# Config Inventory: $dirName")
[void]$sb.AppendLine()
[void]$sb.AppendLine("**Scanned:** ``$Path``  ")
[void]$sb.AppendLine("**Date:** $(Get-Date -Format 'yyyy-MM-dd HH:mm')  ")
[void]$sb.AppendLine("**Files found:** $($files.Count)")
[void]$sb.AppendLine()

# Group by extension
$extGroups = $files | Group-Object Extension | Sort-Object Count -Descending
[void]$sb.AppendLine('## By Extension')
[void]$sb.AppendLine()
foreach ($g in $extGroups) {
    [void]$sb.AppendLine("- **$($g.Name)**: $($g.Count) file(s)")
}
[void]$sb.AppendLine()

# Group by directory
$dirGroups = $files | Group-Object { Split-Path $_.FullName -Parent | ForEach-Object { $_.Replace("$Path", '.') } } | Sort-Object Name
[void]$sb.AppendLine('## By Directory')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Directory | Count | Extensions |')
[void]$sb.AppendLine('|-----------|-------|------------|')
foreach ($g in $dirGroups) {
    $exts = ($g.Group | Select-Object -ExpandProperty Extension -Unique) -join ', '
    [void]$sb.AppendLine("| ``$($g.Name)`` | $($g.Count) | $exts |")
}
[void]$sb.AppendLine()

# File listing ï¿½ per-file or per-directory
if ($ListFiles) {
    [void]$sb.AppendLine('## Files')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| File | Size | Extension |')
    [void]$sb.AppendLine('|------|------|-----------|')
    foreach ($f in $files) {
        $relPath = $f.FullName.Replace("$Path\", '')
        $sizeKb = [math]::Round($f.Length / 1KB, 1)
        $size = if ($sizeKb -ge 1) { "${sizeKb} KB" } else { "$($f.Length) B" }
        [void]$sb.AppendLine("| ``$relPath`` | $size | $($f.Extension) |")
    }
    [void]$sb.AppendLine()
} else {
    [void]$sb.AppendLine('## Files by Directory')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| Directory | Files | Size Range | Sample Names |')
    [void]$sb.AppendLine('|-----------|-------|------------|--------------|')
    foreach ($g in $dirGroups) {
        $sizes = $g.Group | ForEach-Object { $_.Length } | Sort-Object
        $minKb = [math]::Round($sizes[0] / 1KB, 1)
        $maxKb = [math]::Round($sizes[-1] / 1KB, 1)
        $sizeRange = if ($sizes.Count -eq 1) {
            if ($minKb -ge 1) { "${minKb} KB" } else { "$($sizes[0]) B" }
        } else {
            $lo = if ($minKb -ge 1) { "${minKb} KB" } else { "$($sizes[0]) B" }
            $hi = if ($maxKb -ge 1) { "${maxKb} KB" } else { "$($sizes[-1]) B" }
            "${lo} ï¿½ ${hi}"
        }
        $sampleNames = ($g.Group | Select-Object -First 3 -ExpandProperty Name) -join ', '
        if ($g.Group.Count -gt 3) { $sampleNames += ", ï¿½ +$($g.Group.Count - 3)" }
        [void]$sb.AppendLine("| ``$($g.Name)`` | $($g.Count) | $sizeRange | $sampleNames |")
    }
    [void]$sb.AppendLine()
}

# JSON structure sampling
if ($SampleKeys) {
    $jsonFiles = $files | Where-Object { $_.Extension -eq '.json' -and $_.Name -ne 'launchSettings.json' }
    if ($jsonFiles) {
        [void]$sb.AppendLine('## JSON Structure Patterns')
        [void]$sb.AppendLine()

        $structures = @{}
        foreach ($jf in $jsonFiles) {
            try {
                $json = Get-Content $jf.FullName -Raw | ConvertFrom-Json
                $topKeys = ($json.PSObject.Properties.Name | Sort-Object) -join ', '
                if (-not $structures[$topKeys]) { $structures[$topKeys] = @() }
                $structures[$topKeys] += $jf.Name
            } catch {
                [void]$sb.AppendLine("- ``$($jf.Name)``: (parse error)")
            }
        }

        $structIdx = 0
        foreach ($entry in $structures.GetEnumerator() | Sort-Object { $_.Value.Count } -Descending) {
            $structIdx++
            [void]$sb.AppendLine("### Pattern $structIdx ($($entry.Value.Count) file(s))")
            [void]$sb.AppendLine()
            [void]$sb.AppendLine("**Top-level keys:** ``$($entry.Key)``")
            [void]$sb.AppendLine()
            [void]$sb.AppendLine("**Files:**")
            $showFiles = $entry.Value | Select-Object -First $MaxPatternFiles
            foreach ($fname in $showFiles) {
                [void]$sb.AppendLine("- ``$fname``")
            }
            $remaining = $entry.Value.Count - $MaxPatternFiles
            if ($remaining -gt 0) {
                [void]$sb.AppendLine("- ï¿½ and $remaining more")
            }
            [void]$sb.AppendLine()
        }
    }
}

$report = $sb.ToString()

if ($OutFile) {
    $report | Out-File -FilePath $OutFile -Encoding utf8 -NoNewline
    Write-Host "Config inventory written to: $OutFile"
} else {
    $report
}
