<#
.SYNOPSIS
Replaces namespace references in csproj Using directives and .cs using statements.

.PARAMETER ConfigFile
JSON file with "mappings" (old->new namespace) and "paths" (directories to scan).

.PARAMETER Path
Root directory to scan. Defaults to current directory. Overridden by ConfigFile paths.

.PARAMETER Mappings
Hashtable of old namespace -> new namespace. Overridden by ConfigFile mappings.

.PARAMETER WhatIf
Show what would change without modifying files.

.PARAMETER TypeMappings
Hashtable of old type reference -> new type reference for body-level replacements.

.PARAMETER RemoveUsings
Array of full using statements to delete (e.g. 'using Foo = Bar.Baz;').

.PARAMETER Format
Run dotnet format IDE0005 after replacements to remove stale usings.
#>
param(
    [string]$ConfigFile,
    [hashtable]$Mappings,
    [string]$Path = '.',
    [switch]$WhatIf,
    [hashtable]$TypeMappings,
    [string[]]$RemoveUsings,
    [switch]$Format
)

$paths = @($Path)
if ($ConfigFile) {
    $config = Get-Content $ConfigFile | ConvertFrom-Json
    $Mappings = @{}
    foreach ($prop in $config.mappings.PSObject.Properties) { $Mappings[$prop.Name] = $prop.Value }
    if ($config.typeMappings) {
        $TypeMappings = @{}
        foreach ($prop in $config.typeMappings.PSObject.Properties) { $TypeMappings[$prop.Name] = $prop.Value }
    }
    if ($config.removeUsings) { $RemoveUsings = @($config.removeUsings) }
    if ($config.paths) { $paths = @($config.paths) }
    if ($config.format -eq $true) { $Format = [switch]::new($true) }
}

$csprojCount = 0
$csCount = 0
$typeRefCount = 0
$typeFileCount = 0
$removeCount = 0

foreach ($scanPath in $paths) {
    if (-not (Test-Path $scanPath)) { continue }

    # Fix csproj Using directives
    Get-ChildItem -Path $scanPath -Recurse -Filter '*.csproj' |
        Where-Object { $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*' -and $_.FullName -notlike '*_nupkg*' } |
        ForEach-Object {
            $content = Get-Content $_.FullName -Raw
            $original = $content
            foreach ($kv in $Mappings.GetEnumerator()) {
                $content = $content -replace [regex]::Escape("Include=`"$($kv.Key)`""), "Include=`"$($kv.Value)`""
                $content = $content -replace [regex]::Escape("Include=`"$($kv.Key)."), "Include=`"$($kv.Value)."
            }
            if ($content -ne $original) {
                $rel = [System.IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName).Replace('\', '/')
                if ($WhatIf) {
                    Write-Host "WOULD change: $rel"
                } else {
                    [System.IO.File]::WriteAllText($_.FullName, $content)
                    Write-Host "Changed: $rel"
                }
                $script:csprojCount++
            }
        }

    # Fix .cs using statements
    Get-ChildItem -Path $scanPath -Recurse -Filter '*.cs' |
        Where-Object { $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*' -and $_.FullName -notlike '*_nupkg*' } |
        ForEach-Object {
            $content = Get-Content $_.FullName -Raw
            $original = $content
            foreach ($kv in $Mappings.GetEnumerator()) {
                $content = $content -replace "using $([regex]::Escape($kv.Key));", "using $($kv.Value);"
                $content = $content -replace "using $([regex]::Escape($kv.Key))\.", "using $($kv.Value)."
                $content = $content -replace "using static $([regex]::Escape($kv.Key))\.", "using static $($kv.Value)."
            }
            if ($RemoveUsings) {
                foreach ($u in $RemoveUsings) {
                    $escaped = [regex]::Escape($u)
                    $before = $content
                    $content = $content -replace "(?m)^\s*$escaped\s*\r?\n", ''
                    if ($content -ne $before) { $script:removeCount++ }
                }
            }
            if ($content -ne $original) {
                $rel = [System.IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName).Replace('\', '/')
                if ($WhatIf) {
                    Write-Host "WOULD change: $rel"
                } else {
                    [System.IO.File]::WriteAllText($_.FullName, $content)
                    Write-Host "Changed: $rel"
                }
                $script:csCount++
            }
        }

    # Type mappings: word-boundary-aware replacements in .cs file bodies
    if ($TypeMappings -and $TypeMappings.Count -gt 0) {
        Get-ChildItem -Path $scanPath -Recurse -Filter '*.cs' |
            Where-Object { $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*' -and $_.FullName -notlike '*_nupkg*' } |
            ForEach-Object {
                $content = Get-Content $_.FullName -Raw
                $original = $content
                $fileRefs = 0
                foreach ($kv in $TypeMappings.GetEnumerator()) {
                    $pattern = "(?<!\w)$([regex]::Escape($kv.Key))(?!\w)"
                    $before = $content
                    $content = $content -replace $pattern, $kv.Value
                    if ($content -ne $before) {
                        $matches_ = [regex]::Matches($before, $pattern)
                        $fileRefs += $matches_.Count
                    }
                }
                if ($content -ne $original) {
                    $rel = [System.IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName).Replace('\', '/')
                    if ($WhatIf) {
                        Write-Host "WOULD change (type refs): $rel"
                    } else {
                        [System.IO.File]::WriteAllText($_.FullName, $content)
                        Write-Host "Changed (type refs): $rel"
                    }
                    $script:typeRefCount += $fileRefs
                    $script:typeFileCount++
                }
            }
    }
}

Write-Host "`n$csprojCount csproj files, $csCount .cs files $(if ($WhatIf) { 'would be ' })changed"
if ($removeCount -gt 0) {
    Write-Host "$removeCount using statement(s) $(if ($WhatIf) { 'would be ' })removed"
}
if ($typeFileCount -gt 0) {
    Write-Host "$typeRefCount type reference replacement(s) across $typeFileCount file(s) $(if ($WhatIf) { 'would be ' })changed"
}

if ($Format -and -not $WhatIf) {
    $slnFile = $null
    foreach ($scanPath in $paths) {
        if (-not (Test-Path $scanPath)) { continue }
        $slnx = Get-ChildItem -Path $scanPath -Filter '*.slnx' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($slnx) { $slnFile = $slnx.FullName; break }
        $sln = Get-ChildItem -Path $scanPath -Filter '*.sln' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($sln) { $slnFile = $sln.FullName; break }
    }
    if ($slnFile) {
        Write-Host "`nRunning dotnet format on $slnFile ..."
        dotnet format $slnFile --diagnostics IDE0005 --severity warn --verbosity quiet
        Write-Host "Formatted: removed unnecessary usings via IDE0005"
    } else {
        Write-Warning "No .slnx or .sln file found in scanned paths; skipping dotnet format."
    }
}
