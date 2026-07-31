<#
.SYNOPSIS
    Pre-push audit: scans files for non-ASCII bytes and line ending issues.

.DESCRIPTION
    Checks one or more files for encoding problems that trigger AI reviewer
    comments. Reports non-ASCII byte offsets with hex values and line ending
    counts (CRLF vs bare LF). Exit code 1 if any issues found.

.PARAMETER Path
    One or more file paths. Accepts wildcards.

.PARAMETER ExpectCrlf
    Switch. If set, flags bare LF line endings as issues. Use for .cs files.

.EXAMPLE
    .\Test-FileEncoding.ps1 -Path .\AuthorizationSpec.cs -ExpectCrlf
    .\Test-FileEncoding.ps1 -Path .\docs\Coverage.md
    .\Test-FileEncoding.ps1 -Path .\*.cs -ExpectCrlf
#>
[OutputType([string])]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [switch]$ExpectCrlf
)

$hasIssues = $false
$files = $Path | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } | Select-Object -ExpandProperty FullName -Unique

if ($files.Count -eq 0) {
    Write-Error "No files matched: $($Path -join ', ')"
    exit 1
}

foreach ($file in $files) {
    $name = Split-Path $file -Leaf
    $bytes = [System.IO.File]::ReadAllBytes($file)

    # --- Non-ASCII scan ---
    $nonAscii = @()
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -gt 127) {
            $nonAscii += "  Offset $i : 0x$($bytes[$i].ToString('X2'))"
        }
    }

    # --- Line ending scan ---
    $lf = 0; $crlf = 0
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 0x0A) {
            if ($i -gt 0 -and $bytes[$i-1] -eq 0x0D) { $crlf++ } else { $lf++ }
        }
    }

    # --- Report ---
    Write-Output "--- $name ---"

    if ($nonAscii.Count -gt 0) {
        $hasIssues = $true
        Write-Output "  FAIL: $($nonAscii.Count) non-ASCII byte(s)"
        $nonAscii | ForEach-Object { Write-Output $_ }
    } else {
        Write-Output "  OK: 0 non-ASCII bytes"
    }

    Write-Output "  Line endings: CRLF=$crlf, bare LF=$lf"
    if ($ExpectCrlf -and $lf -gt 0) {
        $hasIssues = $true
        Write-Output "  FAIL: expected all CRLF but found $lf bare LF"
    }

    Write-Output ""
}

if ($hasIssues) {
    Write-Output "RESULT: Issues found. Fix before pushing."
    exit 1
} else {
    Write-Output "RESULT: All clean."
}
