<#
.SYNOPSIS
    Create or overwrite a text file without PowerShell here-string pain.

.DESCRIPTION
    Solves three problems with creating files from PowerShell:

    1. Escaping - here-strings break on $ (double-quoted) or '@ at column 0
       (single-quoted). Base64 mode eliminates all escaping issues.
    2. Encoding - writes UTF-8 without BOM by default, or UTF-16 LE with -Unicode.
    3. Directory creation - creates parent directories automatically.

    Three input modes:
      -Text       Raw string. Fine for short, simple content.
      -Base64     Base64-encoded UTF-8 bytes. Use for anything with special
                  characters, or content longer than a few hundred characters.
      -Clipboard  Write the current clipboard text to a file. No escaping,
                  no size limits, no encoding issues. Copy content in your
                  editor, run the command, done.

.EXAMPLE
    & "$env:JanetBase\.github\scripts\New-TextFile.ps1" -Path "hello.txt" -Text "Hello, World!"

.EXAMPLE
    & "$env:JanetBase\.github\scripts\New-TextFile.ps1" -Path "MyClass.cs" -Clipboard

.EXAMPLE
    & "$env:JanetBase\.github\scripts\New-TextFile.ps1" -Path "Copy.cs" -Base64 "dXNpbmcgU3lzdGVtOw0K..." -Force
#>
[CmdletBinding()]
[OutputType([void])]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Path,

    [Parameter(Mandatory, ParameterSetName = 'Text')]
    [string]$Text,

    [Parameter(Mandatory, ParameterSetName = 'Base64')]
    [string]$Base64,

    [Parameter(Mandatory, ParameterSetName = 'Clipboard')]
    [switch]$Clipboard,

    [switch]$Force,

    [switch]$Unicode
)

$fullPath = if ([System.IO.Path]::IsPathRooted($Path)) { $Path }
            else { Join-Path $PWD $Path }

if ((Test-Path $fullPath) -and -not $Force) {
    Write-Error "File already exists: $fullPath. Use -Force to overwrite."
    return
}

$dir = Split-Path $fullPath -Parent
if ($dir -and -not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

$encoding = if ($Unicode) { [System.Text.Encoding]::Unicode } else { [System.Text.UTF8Encoding]::new($false) }

switch ($PSCmdlet.ParameterSetName) {
    'Base64' {
        $clean = $Base64 -replace '\s', ''
        $bytes = [System.Convert]::FromBase64String($clean)
        [System.IO.File]::WriteAllBytes($fullPath, $bytes)
    }
    'Clipboard' {
        $clip = Get-Clipboard -Raw
        if ([string]::IsNullOrEmpty($clip)) {
            Write-Error 'Clipboard is empty.'
            return
        }
        [System.IO.File]::WriteAllText($fullPath, $clip, $encoding)
    }
    default {
        [System.IO.File]::WriteAllText($fullPath, $Text, $encoding)
    }
}

$info = [System.IO.FileInfo]::new($fullPath)
$lines = [System.IO.File]::ReadAllLines($fullPath).Count
Write-Host "Created $($info.Name): $lines lines, $($info.Length) bytes"