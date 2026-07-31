param(
    [string]$Path = '.',
    [string]$OutFile
)

$result = @{}

# Scan csprojs for <Using Include="..."> directives
Get-ChildItem -Path $Path -Recurse -Filter '*.csproj' |
    Where-Object { $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*' -and $_.FullName -notlike '*_nupkg*' } |
    ForEach-Object {
        $rel = [System.IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName).Replace('\', '/')
        $usings = Select-String -Path $_.FullName -Pattern '<Using Include="([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value }
        if ($usings) { $result[$rel] = @{ csprojUsings = @($usings) } }
    }

# Scan .cs files for explicit using statements
Get-ChildItem -Path $Path -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*' -and $_.FullName -notlike '*_nupkg*' } |
    ForEach-Object {
        $rel = [System.IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName).Replace('\', '/')
        $usings = Select-String -Path $_.FullName -Pattern '^using\s+([^;]+);' | ForEach-Object { $_.Matches[0].Groups[1].Value }
        if ($usings) {
            if (-not $result.ContainsKey($rel)) { $result[$rel] = @{} }
            $result[$rel]['csUsings'] = @($usings)
        }
    }

if ($OutFile) {
    $result | ConvertTo-Json -Depth 4 -Compress:$false | Set-Content $OutFile -Encoding utf8
    Write-Host "Written to $OutFile ($($result.Count) files)"
} else {
    $result | ConvertTo-Json -Depth 4
}
