<#
.SYNOPSIS
    Encode a file's content as a Base64 string for use with New-TextFile.ps1 -Base64.

.PARAMETER Path
    Path to the file to encode.

.PARAMETER LineWidth
    Max characters per line in the output. 0 = no wrapping. Default 120.

.OUTPUTS
    [string] Base64 representation of the file.

.EXAMPLE
    & "$env:JanetBase\.github\scripts\ConvertTo-Base64File.ps1" -Path "MyClass.cs" | Set-Clipboard
#>
[CmdletBinding()]
[OutputType([void])]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Path,

    [int]$LineWidth = 120
)

$fullPath = if ([System.IO.Path]::IsPathRooted($Path)) { $Path }
            else { Join-Path $PWD $Path }

if (-not (Test-Path $fullPath)) {
    Write-Error "File not found: $fullPath"
    return
}

$bytes = [System.IO.File]::ReadAllBytes($fullPath)
$b64 = [System.Convert]::ToBase64String($bytes)

if ($LineWidth -gt 0) {
    $sb = [System.Text.StringBuilder]::new($b64.Length + ($b64.Length / $LineWidth) * 2)
    for ($i = 0; $i -lt $b64.Length; $i += $LineWidth) {
        $chunk = [Math]::Min($LineWidth, $b64.Length - $i)
        [void]$sb.AppendLine($b64.Substring($i, $chunk))
    }
    return $sb.ToString().TrimEnd()
}

return $b64