<#
.SYNOPSIS
    Replaces non-ASCII characters in source files with ASCII equivalents.

.DESCRIPTION
    Two-pass approach that handles both valid UTF-8 multi-byte characters
    AND raw Windows-1252 bytes (the 0x80-0x9F range that .NET's UTF-8
    decoder silently turns into '?'):

    Pass 1 (byte-level): Scans raw bytes for Windows-1252 code points in
    the 0x80-0x9F range and replaces them with ASCII byte sequences.
    This prevents the UTF-8 decoder from destroying them.

    Pass 2 (character-level): Decodes the cleaned bytes as UTF-8, then
    replaces remaining Unicode characters (math symbols, Greek letters,
    smart quotes, etc.) with ASCII equivalents.

    Pair with Test-FileEncoding.ps1 to audit files before pushing.

.PARAMETER Path
    One or more file paths. Accepts wildcards and pipeline input.

.PARAMETER WhatIf
    Reports what would change without modifying any files.

.EXAMPLE
    .\Fix-FileEncoding.ps1 -Path .\BackpressureMonitor.cs
    .\Fix-FileEncoding.ps1 -Path .\*.cs -WhatIf
    Get-ChildItem *.cs -Recurse | .\Fix-FileEncoding.ps1
#>
[OutputType([string])]
param(
    [Parameter(Mandatory, Position = 0, ValueFromPipeline, ValueFromPipelineByPropertyName)]
    [Alias('FullName')]
    [string[]]$Path,

    [switch]$WhatIf
)

begin {
    # --- Pass 1: Windows-1252 byte-level replacements (0x80-0x9F range) ---
    # These single bytes are NOT valid UTF-8 and would be destroyed by ReadAllText.
    # Keys are single bytes; values are ASCII byte arrays.
    $byteReplacements = @{
        [byte]0x91 = [byte[]][char[]]"'"
        [byte]0x92 = [byte[]][char[]]"'"
        [byte]0x93 = [byte[]][char[]]'"'
        [byte]0x94 = [byte[]][char[]]'"'
        [byte]0x95 = [byte[]][char[]]'*'
        [byte]0x96 = [byte[]][char[]]'-'
        [byte]0x97 = [byte[]][char[]]'--'
        [byte]0x85 = [byte[]][char[]]'...'
        [byte]0x99 = [byte[]][char[]]'(tm)'
    }

    # --- Pass 2: Unicode character-level replacements ---
    $charReplacements = [ordered]@{
        [char]0xFFFD = '?'
        [char]0x2014 = '--'
        [char]0x2013 = '-'
        [char]0x201C = '"'
        [char]0x201D = '"'
        [char]0x2018 = "'"
        [char]0x2019 = "'"
        [char]0x2026 = '...'
        [char]0x2022 = '*'
        [char]0x00D7 = '*'
        [char]0x00F7 = '/'
        [char]0x00B2 = '^2'
        [char]0x00B3 = '^3'
        [char]0x03C3 = 'sigma'
        [char]0x03B1 = 'alpha'
        [char]0x03B2 = 'beta'
        [char]0x03BC = 'mu'
        [char]0x03C0 = 'pi'
        [char]0x2248 = '~='
        [char]0x2260 = '!='
        [char]0x2264 = '<='
        [char]0x2265 = '>='
        [char]0x221E = 'Inf'
        [char]0x00B1 = '+/-'
        [char]0x00B0 = 'deg'
        [char]0x2192 = '->'
        [char]0x2190 = '<-'
        [char]0x21D2 = '=>'
        [char]0x25B6 = '*'
        [char]0x2500 = '-'
        [char]0x2502 = '|'
        [char]0x2550 = '='
        [char]0x2588 = '#'
        [char]0x2591 = '.'
        [char]0x2592 = '+'
        [char]0x2593 = '#'
        [char]0x00A0 = ' '
    }

    function Invoke-BytePass {
[OutputType([string])]
        param([byte[]]$Bytes)

        $result = [System.Collections.Generic.List[byte]]::new($Bytes.Length)
        $changes = 0

        foreach ($b in $Bytes) {
            if ($byteReplacements.ContainsKey($b)) {
                $result.AddRange([byte[]]$byteReplacements[$b])
                $changes++
            } else {
                $result.Add($b)
            }
        }

        return @{ Bytes = $result.ToArray(); Changes = $changes }
    }
}

process {
    foreach ($file in $Path) {
        foreach ($resolved in (Resolve-Path $file -ErrorAction SilentlyContinue)) {
            $filePath = $resolved.Path
            $name     = Split-Path $filePath -Leaf

            # Pass 1: byte-level scan for Windows-1252 specials
            $rawBytes = [System.IO.File]::ReadAllBytes($filePath)
            $byteResult = Invoke-BytePass -Bytes $rawBytes
            $byteChanges = $byteResult.Changes

            # Decode cleaned bytes as UTF-8
            $content  = [System.Text.Encoding]::UTF8.GetString($byteResult.Bytes)
            $afterBytePass = $content

            # Pass 2: character-level replacements for valid Unicode
            foreach ($entry in $charReplacements.GetEnumerator()) {
                $content = $content.Replace([string]$entry.Key, $entry.Value)
            }

            $charChanges = 0
            if ($content -ne $afterBytePass) {
                $minLen = [Math]::Min($afterBytePass.Length, $content.Length)
                for ($i = 0; $i -lt $minLen; $i++) {
                    if ($afterBytePass[$i] -ne $content[$i]) { $charChanges++ }
                }
                $charChanges += [Math]::Abs($afterBytePass.Length - $content.Length)
            }

            $totalChanges = $byteChanges + $charChanges

            if ($totalChanges -eq 0) {
                Write-Output "  $name  OK"
                continue
            }

            $detail = @()
            if ($byteChanges -gt 0) { $detail += "$byteChanges Win-1252" }
            if ($charChanges -gt 0) { $detail += "$charChanges Unicode" }

            if ($WhatIf) {
                Write-Output "  $name  WOULD FIX $totalChanges char(s) ($($detail -join ', '))"
            } else {
                $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
                [System.IO.File]::WriteAllText($filePath, $content, $utf8NoBom)

                $afterBytes = [System.IO.File]::ReadAllBytes($filePath)
                $remaining  = ($afterBytes | Where-Object { $_ -gt 127 }).Count

                Write-Output "  $name  FIXED $totalChanges ($($detail -join ', ')) [$remaining non-ASCII bytes remaining]"
            }
        }
    }
}
