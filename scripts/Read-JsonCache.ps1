<#
.SYNOPSIS
    Read cached JSON files as PSCustomObjects, with optional schema discovery.

.DESCRIPTION
    Deserializes one or more JSON files from disk and emits PSCustomObjects.
    In -Schema mode, walks the JSON structure and returns key names, types,
    and counts -- just enough for an agent to know what to ask for without
    reading the full content.

    Works with any JSON file: work items, PRs, sprint status, etc.

.PARAMETER Path
    One or more paths to JSON files.

.PARAMETER Schema
    Instead of returning data, return the structure: key names, value types,
    and element counts for arrays.

.OUTPUTS
    PSCustomObject - deserialized JSON data, or schema description.

.EXAMPLE
    .\Read-JsonCache.ps1 "$env:TEMP\wi-15837556\wi-15837556.json"

.EXAMPLE
    .\Read-JsonCache.ps1 "$env:TEMP\wi-15837556\wi-15837556.json" -Schema

.EXAMPLE
    Get-ChildItem "$env:TEMP\wi-*\*.json" | .\Read-JsonCache.ps1
#>
[OutputType([PSCustomObject])]
param(
    [Parameter(Mandatory, Position = 0, ValueFromPipeline, ValueFromPipelineByPropertyName)]
    [Alias('FullName')]
    [string[]]$Path,

    [switch]$Schema
)

begin {
    function Get-SchemaNode($obj, $indent) {
        $prefix = '  ' * $indent
        if ($obj -is [System.Collections.IList]) {
            $count = $obj.Count
            if ($count -gt 0) {
                Write-Output "${prefix}array[$count]:"
                Get-SchemaNode $obj[0] ($indent + 1)
            } else {
                Write-Output "${prefix}array[0]"
            }
        }
        elseif ($obj -is [PSCustomObject]) {
            foreach ($prop in $obj.PSObject.Properties) {
                $val = $prop.Value
                if ($val -is [PSCustomObject]) {
                    Write-Output "${prefix}$($prop.Name) (object):"
                    Get-SchemaNode $val ($indent + 1)
                }
                elseif ($val -is [System.Collections.IList]) {
                    $count = $val.Count
                    if ($count -gt 0 -and $val[0] -is [PSCustomObject]) {
                        Write-Output "${prefix}$($prop.Name) (array[$count] of object):"
                        Get-SchemaNode $val[0] ($indent + 1)
                    } else {
                        $elemType = if ($count -gt 0) { $val[0].GetType().Name } else { '?' }
                        Write-Output "${prefix}$($prop.Name) (array[$count] of $elemType)"
                    }
                }
                else {
                    $typeName = if ($null -eq $val) { 'null' } else { $val.GetType().Name }
                    $preview = if ($val -is [string] -and $val.Length -gt 60) { $val.Substring(0, 60) + '...' } elseif ($null -ne $val) { "$val" } else { 'null' }
                    Write-Output "${prefix}$($prop.Name) ($typeName): $preview"
                }
            }
        }
        else {
            $typeName = if ($null -eq $obj) { 'null' } else { $obj.GetType().Name }
            Write-Output "${prefix}($typeName): $obj"
        }
    }
}

process {
    foreach ($p in $Path) {
        if (-not (Test-Path $p)) {
            Write-Error "File not found: $p"
            continue
        }

        $json = Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json

        if ($Schema) {
            Write-Output "=== $(Split-Path $p -Leaf) ==="
            Get-SchemaNode $json 0
            Write-Output ''
        }
        else {
            $json  # emit to pipeline
        }
    }
}
