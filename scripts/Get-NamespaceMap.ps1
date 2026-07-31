param(
    [string]$Path = '.',
    [string]$OutFile
)

$map = @{}
Get-ChildItem -Path $Path -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*' -and $_.FullName -notlike '*_nupkg*' } |
    ForEach-Object {
        $ns = Select-String -Path $_.FullName -Pattern '^namespace\s+(.+?)[\s;{]' | ForEach-Object { $_.Matches[0].Groups[1].Value }
        if ($ns) {
            $rel = [System.IO.Path]::GetRelativePath((Get-Location).Path, $_.FullName).Replace('\', '/')
            if (-not $map.ContainsKey($ns)) { $map[$ns] = @() }
            $map[$ns] += $rel
        }
    }

$sorted = @{}
$map.Keys | Sort-Object | ForEach-Object { $sorted[$_] = @($map[$_] | Sort-Object) }

if ($OutFile) {
    $sorted | ConvertTo-Json -Depth 3 -Compress:$false | Set-Content $OutFile -Encoding utf8
    Write-Host "Written to $OutFile ($($sorted.Count) namespaces, $(($map.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum) files)"
} else {
    $sorted | ConvertTo-Json -Depth 3
}
