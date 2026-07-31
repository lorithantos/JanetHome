<#
.SYNOPSIS
    Reports types that share a short name across two C# codebases.
.DESCRIPTION
    Scans both trees for public/internal type declarations and reports collisions
    by short name, flagging whether the namespaces also match. Useful when
    migrating or consolidating two libraries that grew independently.
#>
param(
    [Parameter(Mandatory)][string]$LocalPath,
    [Parameter(Mandatory)][string]$OtherPath,
    [string]$OutFile
)

function Get-TypeNames($path) {
    $types = @{}
    Get-ChildItem -Path $path -Recurse -Filter '*.cs' |
        Where-Object { $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*' -and $_.FullName -notlike '*_nupkg*' -and $_.FullName -notlike '*Test*' } |
        ForEach-Object {
            $ns = Select-String -Path $_.FullName -Pattern '^namespace\s+(.+?)[\s;{]' | ForEach-Object { $_.Matches[0].Groups[1].Value }
            $typeNames = Select-String -Path $_.FullName -Pattern '^\s*(?:public|internal)\s+(?:static\s+)?(?:abstract\s+)?(?:sealed\s+)?(?:class|interface|enum|struct|record)\s+(\w+)' | ForEach-Object { $_.Matches[0].Groups[1].Value }
            foreach ($t in $typeNames) {
                $key = if ($ns) { "$ns.$t" } else { $t }
                $rel = [System.IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName).Replace('\', '/')
                $types[$key] = @{ file = $rel; namespace = $ns; typeName = $t }
            }
        }
    return $types
}

$local = Get-TypeNames $LocalPath
$other = Get-TypeNames $OtherPath

# Find types with same short name in both
$overlap = @()
foreach ($lk in $local.Keys) {
    $lType = $local[$lk].typeName
    $gMatches = $other.Keys | Where-Object { $other[$_].typeName -eq $lType }
    foreach ($gk in $gMatches) {
        $overlap += @{
            typeName = $lType
            localFQN = $lk
            localFile = $local[$lk].file
            otherFQN = $gk
            otherFile = $other[$gk].file
            sameNamespace = ($local[$lk].namespace -eq $other[$gk].namespace)
        }
    }
}

$result = @{
    localTypeCount = $local.Count
    otherTypeCount = $other.Count
    overlapCount = $overlap.Count
    overlaps = $overlap | Sort-Object { $_.typeName }
}

if ($OutFile) {
    $result | ConvertTo-Json -Depth 4 -Compress:$false | Set-Content $OutFile -Encoding utf8
    Write-Host "Written to $OutFile ($($overlap.Count) overlapping types)"
} else {
    $result | ConvertTo-Json -Depth 4
}
