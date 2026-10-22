<#
.SYNOPSIS
    Namespace-equals-folder lint for C# projects: every .cs file must declare the
    namespace its folder implies.

.DESCRIPTION
    For every .csproj under -Path (or the .csproj -Path names), each .cs file under
    the project directory is checked against

        expected = RootNamespace + '.' + (folder path relative to the project dir, dot-joined)

    RootNamespace is read from the csproj; when absent it defaults to the project file
    name with spaces made underscores, as the .NET SDK does. A file at the project root
    expects RootNamespace alone. The declared namespace is the first file-scoped
    ('namespace A.B;') or block ('namespace A.B {') declaration in the file. The
    comparison is case-sensitive, because C# namespaces are.

    The rule is the folder path EXACTLY -- Interfaces/ and Models/ are namespaces, and
    so is every other folder. The layout this checks (ImageSelectorV2, gamehub since
    2026-09-01) keeps implementations at the project root with Interfaces/ and Models/
    beneath, and lists each sub-namespace as a <Using Include> in the csproj so callers
    need no using lines. That absorbs the sub-namespace at the CALL SITE; it does not
    change what the declaration should say, so the lint does not consult the Using list.

    bin\ and obj\ are skipped. A .cs file belongs to the nearest .csproj above it, so a
    project nested inside another project's folder is checked against its own root.
    A file with no namespace declaration (top-level statements, a GlobalUsings.cs) is
    listed under noNamespace and is not a mismatch.

    Exit 1 when any mismatch exists, 0 when clean.

.PARAMETER Path
    A repo directory, a project directory, or a .csproj file.

.PARAMETER Text
    A human table instead of JSON.

.PARAMETER Pretty
    Indented JSON.

.OUTPUTS
    JSON: { path, rule, projectsScanned, filesScanned, returned, mismatches[], noNamespace[], projects[] }
    Each mismatch is { project, file, folder, declared, expected }; file is relative to
    the project directory. Each project is { project, rootNamespace, rootNamespaceSource, files }.

.EXAMPLE
    pwsh -File "$env:JanetBase\scripts\Test-NamespaceFolders.ps1" -Path C:\repos\ImageSelectorV2 -Text
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$skipSegment = '[\\/](bin|obj)([\\/]|$)'
$namespacePattern = '^\s*namespace\s+([A-Za-z_@][\w.]*)\s*(;|\{|$)'

function Get-ProjectFile {
    # The .csproj files -Path names: itself when it is one, else every one beneath it
    # outside bin\ and obj\.
    param([string]$Root)
    $item = Get-Item -LiteralPath $Root
    if (-not $item.PSIsContainer) {
        if ($item.Extension -ne '.csproj') { throw "Not a directory or a .csproj: $Root" }
        return @($item)
    }
    return @(Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch $skipSegment } |
        Sort-Object FullName)
}

function Get-RootNamespace {
    # RootNamespace from the csproj, or the SDK's default: the project name with
    # spaces made underscores.
    param([System.IO.FileInfo]$Project)
    [xml]$xml = [System.IO.File]::ReadAllText($Project.FullName)
    $node = $xml.SelectSingleNode('//*[local-name()="RootNamespace"]')
    if ($null -ne $node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
        return [PSCustomObject]@{ value = $node.InnerText.Trim(); source = 'csproj' }
    }
    return [PSCustomObject]@{ value = $Project.BaseName.Replace(' ', '_'); source = 'project name' }
}

function Get-DeclaredNamespace {
    # The first namespace declaration, file-scoped or block, or $null.
    param([string]$File)
    foreach ($line in [System.IO.File]::ReadLines($File)) {
        if ($line -match $namespacePattern) { return $Matches[1] }
    }
    return $null
}

function Get-OwningProjectDir {
    # The deepest project directory containing the file, so a nested project's files
    # are not also claimed by its parent.
    param([string]$File, [string[]]$ProjectDirs)
    $best = $null
    foreach ($dir in $ProjectDirs) {
        $prefix = $dir.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if (-not $File.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($null -eq $best -or $dir.Length -gt $best.Length) { $best = $dir }
    }
    return $best
}

function Get-ExpectedNamespace {
    # RootNamespace plus the file's folder relative to the project dir, dot-joined.
    param([string]$RootNamespace, [string]$RelativeFolder)
    if ([string]::IsNullOrEmpty($RelativeFolder)) { return $RootNamespace }
    $segments = $RelativeFolder -split '[\\/]' | Where-Object { $_ }
    return (@($RootNamespace) + @($segments)) -join '.'
}

function Test-ProjectFile {
    # Checks one .cs file and returns a row: { project, file, folder, declared, expected }.
    param([string]$File, [System.IO.FileInfo]$Project, [string]$RootNamespace)
    $relative = [System.IO.Path]::GetRelativePath($Project.DirectoryName, $File)
    $folder = [System.IO.Path]::GetDirectoryName($relative)
    [PSCustomObject]@{
        project  = $Project.Name
        file     = $relative
        folder   = $folder
        declared = Get-DeclaredNamespace -File $File
        expected = Get-ExpectedNamespace -RootNamespace $RootNamespace -RelativeFolder $folder
    }
}

if (-not (Test-Path -LiteralPath $Path)) { throw "Path not found: $Path" }

$projects = @(Get-ProjectFile -Root $Path)
$projectDirs = @($projects | ForEach-Object { $_.DirectoryName })

$rows = [System.Collections.Generic.List[object]]::new()
$projectSummaries = [System.Collections.Generic.List[object]]::new()

foreach ($project in $projects) {
    $root = Get-RootNamespace -Project $project
    $files = @(Get-ChildItem -LiteralPath $project.DirectoryName -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName.Substring($project.DirectoryName.Length) -notmatch $skipSegment } |
        Where-Object { (Get-OwningProjectDir -File $_.FullName -ProjectDirs $projectDirs) -eq $project.DirectoryName } |
        Sort-Object FullName)
    foreach ($file in $files) {
        $rows.Add((Test-ProjectFile -File $file.FullName -Project $project -RootNamespace $root.value))
    }
    $projectSummaries.Add([PSCustomObject]@{
            project             = $project.FullName
            rootNamespace       = $root.value
            rootNamespaceSource = $root.source
            files               = $files.Count
        })
}

$mismatches = @($rows | Where-Object { $null -ne $_.declared -and $_.declared -cne $_.expected })
$noNamespace = @($rows | Where-Object { $null -eq $_.declared } | ForEach-Object { [PSCustomObject]@{ project = $_.project; file = $_.file } })

$result = [PSCustomObject]@{
    path            = (Resolve-Path -LiteralPath $Path).Path
    rule            = 'declared namespace == RootNamespace + folder path relative to the project dir, dot-joined, case-sensitive'
    projectsScanned = $projects.Count
    filesScanned    = $rows.Count
    returned        = $mismatches.Count
    mismatches      = $mismatches
    noNamespace     = $noNamespace
    projects        = @($projectSummaries)
}

if ($Text) {
    Write-Host "$($mismatches.Count) mismatch(es) in $($rows.Count) file(s) across $($projects.Count) project(s) under $($result.path)"
    if ($mismatches.Count -gt 0) {
        $mismatches | Format-Table project, file, declared, expected -AutoSize | Out-String -Width 400 | Write-Host
    }
    if ($noNamespace.Count -gt 0) {
        Write-Host "$($noNamespace.Count) file(s) declare no namespace (not mismatches):"
        foreach ($n in $noNamespace) { Write-Host "  $($n.project)  $($n.file)" }
    }
}
else {
    $result | ConvertTo-Json -Depth 5 -Compress:(-not $Pretty)
}

if ($mismatches.Count -gt 0) { exit 1 }
exit 0
