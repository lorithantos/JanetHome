<#
.SYNOPSIS
    Pre-push audit: checks each file against the encoding its type is held to.

.DESCRIPTION
    Two policies, chosen by extension, because code and prose are not the same
    kind of file.

    ASCII (everything except .md) -- code and data. A non-ASCII byte here is
    almost always an accident: a smart quote pasted into a string literal, an
    em dash in a comment, an encoding that survived a copy from a web page.
    research.json in particular must stay ASCII, because the writer's whole
    escaping story is built on it and a stray byte changes the file's bytes
    without changing its meaning.

    UTF-8 (.md) -- prose. Markdown is written for people, and an em dash is
    the right character rather than a defect. Held to valid UTF-8 with no BOM:
    a BOM breaks front-matter parsers and shows up as a phantom diff on the
    first line.

    Line endings are reported for both and only enforced under -ExpectCrlf.
    Exit code 1 if any file fails its policy, 0 otherwise -- stated explicitly
    so a caller reading $LASTEXITCODE is not reading a value left by whatever
    ran before.

.PARAMETER Path
    One or more file paths. Accepts wildcards.

.PARAMETER ExpectCrlf
    Switch. If set, flags bare LF line endings as issues. Use for .cs files.

.PARAMETER Strict
    Switch. Applies the ASCII policy to every file, markdown included. For
    auditing prose that is supposed to have stayed plain.

.EXAMPLE
    .\Test-FileEncoding.ps1 -Path .\AuthorizationSpec.cs -ExpectCrlf
    .\Test-FileEncoding.ps1 -Path .\docs\Coverage.md
    .\Test-FileEncoding.ps1 -Path .\*.md -Strict
#>
[OutputType([string])]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [switch]$ExpectCrlf,
    [switch]$Strict
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

    $isProse = (-not $Strict) -and ([System.IO.Path]::GetExtension($file) -eq '.md')

    # --- Non-ASCII scan ---
    $nonAscii = @()
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -gt 127) {
            $nonAscii += "  Offset $i : 0x$($bytes[$i].ToString('X2'))"
        }
    }

    # --- UTF-8 validity, for the files allowed to hold more than ASCII ---
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $utf8Error = $null
    if ($isProse -and $nonAscii.Count -gt 0) {
        try {
            # Throws rather than substituting U+FFFD, so mojibake is a failure and not a
            # silent replacement that looks fine until someone reads it.
            $null = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        } catch {
            $utf8Error = $_.Exception.InnerException ? $_.Exception.InnerException.Message : $_.Exception.Message
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

    if ($isProse) {
        if ($bom) {
            $hasIssues = $true
            Write-Output '  FAIL: UTF-8 byte order mark. Save without one.'
        } elseif ($utf8Error) {
            $hasIssues = $true
            Write-Output "  FAIL: not valid UTF-8 -- $utf8Error"
        } else {
            Write-Output "  OK: UTF-8, $($nonAscii.Count) non-ASCII byte(s)"
        }
    } elseif ($nonAscii.Count -gt 0) {
        $hasIssues = $true
        Write-Output "  FAIL: $($nonAscii.Count) non-ASCII byte(s)"
        $nonAscii | ForEach-Object { Write-Output $_ }
    } else {
        Write-Output '  OK: 0 non-ASCII bytes'
    }

    Write-Output "  Line endings: CRLF=$crlf, bare LF=$lf"
    if ($ExpectCrlf -and $lf -gt 0) {
        $hasIssues = $true
        Write-Output "  FAIL: expected all CRLF but found $lf bare LF"
    }

    Write-Output ""
}

if ($hasIssues) {
    Write-Output 'RESULT: Issues found. Fix before pushing.'
    exit 1
}

Write-Output 'RESULT: All clean.'

# Stated rather than implied. Falling off the end leaves $LASTEXITCODE holding whatever
# the previous command set, so a caller checking it reads a stale answer and calls it ours.
exit 0
