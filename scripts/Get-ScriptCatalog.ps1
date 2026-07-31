<#
.SYNOPSIS
    Lists all reusable scripts with their synopsis and parameter names.

.DESCRIPTION
    Scans the script directory and outputs a compact catalog of every .ps1 file:
    name, synopsis, and parameter list.  Designed to be called once at the start
    of a session so the caller knows what tools are available without reading
    each file.

.PARAMETER ScriptDir
    Directory to scan.  Defaults to the directory this script lives in, which
    makes it correct regardless of repo layout -- the original hardcoded
    $env:JanetBase\.github\scripts, which is wrong for any repo that keeps its
    scripts elsewhere.

.PARAMETER Text
    Formatted output instead of JSON. Output is JSON by default: this is the fallback
    when research.json is stale or a lookup misses, and its reader is the session model.

.PARAMETER Pretty
    Indent the JSON.

.OUTPUTS
    JSON: { count, scripts[] } where each is { name, synopsis, params[] }.

.EXAMPLE
    & "$env:JanetBase\scripts\Get-ScriptCatalog.ps1"

.EXAMPLE
    & "$env:JanetBase\scripts\Get-ScriptCatalog.ps1" -Text
#>
[CmdletBinding()]
param(
    [string]$ScriptDir = $PSScriptRoot,
    [switch]$Text,
    [switch]$Pretty
)

$scriptDir = $ScriptDir
$catalog = @()

foreach ($file in (Get-ChildItem "$scriptDir\*.ps1" | Where-Object Name -ne 'Get-ScriptCatalog.ps1' | Sort-Object Name)) {
    $help = Get-Help $file.FullName -ErrorAction SilentlyContinue

    # Get-Help returns a bare string for a script with no comment-based help, so
    # every property access below has to be probed first. Reading .parameters
    # blind throws under Set-StrictMode -- which the startup executor sets.
    $hasProp = { param($o, $n) $null -ne $o -and $o.PSObject.Properties.Name -contains $n }

    $synopsis = if ((& $hasProp $help 'Synopsis')) { $help.Synopsis } else { $null }
    # With no .SYNOPSIS block, Get-Help synthesizes the syntax line -- either the
    # bare filename or 'Name.ps1 [[-Param] <type>] ...'. Both are noise here.
    if (-not $synopsis -or $synopsis -match "^\s*$([regex]::Escape($file.Name))(\s|$)") {
        $synopsis = '(no synopsis)'
    }
    $synopsis = ($synopsis -split '\r?\n' | Where-Object { $_.Trim() }) -join ' '

    # Params stay an array in the structured form; only the text view joins them.
    $paramList = @()
    if ((& $hasProp $help 'parameters') -and (& $hasProp $help.parameters 'parameter')) {
        $paramList = @($help.parameters.parameter | ForEach-Object { $_.name })
    }

    $catalog += [PSCustomObject]@{
        name     = $file.Name
        synopsis = $synopsis
        params   = $paramList
    }
}

if (-not $Text) {
    $result = [PSCustomObject]@{ count = $catalog.Count; scripts = @($catalog) }
    if ($Pretty) { $result | ConvertTo-Json -Depth 4 }
    else { $result | ConvertTo-Json -Depth 4 -Compress }
    return
}

foreach ($entry in $catalog) {
    $p = if ($entry.params.Count -gt 0) { $entry.params -join ', ' } else { '(none)' }
    Write-Host "$($entry.name)"
    Write-Host "  $($entry.synopsis)"
    Write-Host "  Params: $p"
    Write-Host ''
}