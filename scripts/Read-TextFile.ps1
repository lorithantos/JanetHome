<#
.SYNOPSIS
    Read a text file with automatic encoding detection.

.DESCRIPTION
    Detects file encoding from BOM (UTF-16 LE, UTF-8 BOM, or UTF-8 no BOM),
    reads the content using the correct encoding, and returns an object with
    the content, detected encoding name, the System.Text.Encoding instance,
    and a Warning property (null when clean, string when anomalous).

    Detects fake UTF-16 LE BOM (FF FE header over ASCII/UTF-8 body) by
    checking for null byte interleaving. True UTF-16 LE has nulls every
    other byte; a fake BOM over ASCII has none.

    Use the returned Encoding property with New-TextFile.ps1 or
    [System.IO.File]::WriteAllText to write back in the same encoding.

.EXAMPLE
    $file = & "$env:JanetBase\.github\scripts\Read-TextFile.ps1" -Path "reply.md"
    $file.Content    # the text
    $file.Encoding   # [System.Text.Encoding] instance
    $file.EncodingName  # "UTF-16 LE", "UTF-8 BOM", or "UTF-8"

.EXAMPLE
    # Round-trip edit preserving encoding
    $file = & "$env:JanetBase\.github\scripts\Read-TextFile.ps1" -Path "reply.md"
    $modified = $file.Content.Replace("old", "new")
    [System.IO.File]::WriteAllText($file.Path, $modified, $file.Encoding)
#>
[CmdletBinding()]
[OutputType([PSCustomObject])]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Path
)

$fullPath = if ([System.IO.Path]::IsPathRooted($Path)) { $Path }
            else { Join-Path $PWD $Path }

if (-not (Test-Path $fullPath)) {
    Write-Error "File not found: $fullPath"
    return
}

$bytes = [System.IO.File]::ReadAllBytes($fullPath)

# Detect encoding from BOM
$warning = $null

if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
    # Check for fake BOM: UTF-16 LE header over ASCII/UTF-8 body
    $sampleEnd = [Math]::Min(102, $bytes.Length)
    $sample = $bytes[2..($sampleEnd - 1)]
    $nullCount = ($sample | Where-Object { $_ -eq 0 }).Count
    if ($nullCount -eq 0 -and $sample.Count -gt 10) {
        $warning = 'UTF-16 LE BOM detected but body contains no null bytes; likely ASCII/UTF-8 with spurious BOM'
    }
    $encoding = [System.Text.Encoding]::Unicode
    $encodingName = 'UTF-16 LE'
}
elseif ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
    $encoding = [System.Text.UTF8Encoding]::new($true)
    $encodingName = 'UTF-8 BOM'
}
else {
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $encodingName = 'UTF-8'
}

$content = [System.IO.File]::ReadAllText($fullPath, $encoding)

[PSCustomObject]@{
    Path         = $fullPath
    Content      = $content
    Encoding     = $encoding
    EncodingName = $encodingName
    Warning      = $warning
}