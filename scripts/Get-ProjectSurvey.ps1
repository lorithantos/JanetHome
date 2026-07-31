<#
.SYNOPSIS
    Scans a repo for .csproj files and produces a markdown summary of each project.

.DESCRIPTION
    Extracts target framework, package references, project references, output type,
    root namespace, and assembly name from every .csproj under the given path.
    Outputs a markdown report (tables + details) to stdout or a file.

.PARAMETER Path
    Root directory to scan. Defaults to current directory.

.PARAMETER OutFile
    Optional file path to write the markdown report to.

.PARAMETER Exclude
    Directory names to exclude (e.g., obj, bin, .gdn). Defaults to obj, bin, .gdn, node_modules.

.PARAMETER MaxDepth
    Maximum directory depth to recurse. 0 = unlimited (default).

.EXAMPLE
    Get-ProjectSurvey.ps1 -Path <repo-root>\SampleApp.Jobs
    Get-ProjectSurvey.ps1 -Path <repo-root>\SampleApp.Web -OutFile $env:TEMP\survey.md
#>
[CmdletBinding()]
param(
    [string]$Path = '.',
    [string]$OutFile,
    [string[]]$Exclude = @('obj', 'bin', '.gdn', 'node_modules', '.vs', 'packages'),
    [int]$MaxDepth = 0
)

$ErrorActionPreference = 'Stop'
$Path = Resolve-Path $Path
$repoName = Split-Path $Path -Leaf

# Build exclusion regex from directory names
$excludePattern = ($Exclude | ForEach-Object { [regex]::Escape($_) }) -join '|'
$excludeRegex = "[\\/]($excludePattern)[\\/]"

# Find all csproj files
$csprojFiles = Get-ChildItem -Path $Path -Filter '*.csproj' -Recurse -File |
    Where-Object { $_.FullName -notmatch $excludeRegex }

if (-not $csprojFiles) {
    Write-Warning "No .csproj files found under $Path"
    return
}

$projects = foreach ($csproj in $csprojFiles) {
    $relPath = $csproj.FullName.Replace("$Path\", '')
    $xml = [xml](Get-Content $csproj.FullName -Raw)

    # Target framework(s)
    $tfm = $xml.Project.PropertyGroup.TargetFramework |
        Where-Object { $_ } | Select-Object -First 1
    $tfms = $xml.Project.PropertyGroup.TargetFrameworks |
        Where-Object { $_ } | Select-Object -First 1
    $framework = if ($tfms) { $tfms } elseif ($tfm) { $tfm } else { '(unknown)' }

    # Output type
    $outputType = $xml.Project.PropertyGroup.OutputType |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $outputType) { $outputType = 'Library' }

    # Root namespace
    $rootNs = $xml.Project.PropertyGroup.RootNamespace |
        Where-Object { $_ } | Select-Object -First 1

    # Assembly name
    $asmName = $xml.Project.PropertyGroup.AssemblyName |
        Where-Object { $_ } | Select-Object -First 1

    # SDK
    $sdk = $xml.Project.Sdk
    if (-not $sdk) {
        $sdkImport = $xml.Project.Import | Where-Object { $_.Sdk } | Select-Object -First 1
        if ($sdkImport) { $sdk = $sdkImport.Sdk }
    }

    # Package references
    $pkgRefs = $xml.Project.ItemGroup.PackageReference | Where-Object { $_.Include } | ForEach-Object {
        $ver = if ($_.Version) { $_.Version }
               elseif ($_.VersionOverride) { "$($_.VersionOverride) (override)" }
               else { '(central)' }
        [PSCustomObject]@{ Name = $_.Include; Version = $ver }
    }

    # Project references
    $projRefs = $xml.Project.ItemGroup.ProjectReference | Where-Object { $_.Include } | ForEach-Object {
        $_.Include -replace '\\', '/'
    }

    # Implicit usings / nullable
    $implicitUsings = $xml.Project.PropertyGroup.ImplicitUsings |
        Where-Object { $_ } | Select-Object -First 1
    $nullable = $xml.Project.PropertyGroup.Nullable |
        Where-Object { $_ } | Select-Object -First 1

    [PSCustomObject]@{
        RelPath         = $relPath
        Name            = $csproj.BaseName
        SDK             = $sdk
        Framework       = $framework
        OutputType      = $outputType
        RootNamespace   = $rootNs
        AssemblyName    = $asmName
        ImplicitUsings  = $implicitUsings
        Nullable        = $nullable
        PackageRefs     = $pkgRefs
        ProjectRefs     = $projRefs
        PackageCount    = ($pkgRefs | Measure-Object).Count
        ProjectRefCount = ($projRefs | Measure-Object).Count
    }
}

# Build markdown report
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# Project Survey: $repoName")
[void]$sb.AppendLine()
[void]$sb.AppendLine("**Scanned:** ``$Path``  ")
[void]$sb.AppendLine("**Date:** $(Get-Date -Format 'yyyy-MM-dd HH:mm')  ")
[void]$sb.AppendLine("**Projects found:** $($projects.Count)")
[void]$sb.AppendLine()

# Summary table
[void]$sb.AppendLine('## Summary')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Project | TFM | Packages | ProjRefs | Namespace |')
[void]$sb.AppendLine('|---------|-----|----------|----------|-----------|')
foreach ($p in $projects) {
    $ns = if ($p.RootNamespace) { "``$($p.RootNamespace)``" } else { '-' }
    [void]$sb.AppendLine("| ``$($p.Name)`` | ``$($p.Framework)`` | $($p.PackageCount) | $($p.ProjectRefCount) | $ns |")
}
[void]$sb.AppendLine()

# TFM distribution
$tfmGroups = $projects | Group-Object Framework | Sort-Object Count -Descending
[void]$sb.AppendLine('## Target Frameworks')
[void]$sb.AppendLine()
foreach ($g in $tfmGroups) {
    [void]$sb.AppendLine("- **$($g.Name)**: $($g.Count) project(s)")
}
[void]$sb.AppendLine()

# Package reference index
$allPkgs = $projects | ForEach-Object {
    $projName = $_.Name
    $_.PackageRefs | ForEach-Object { [PSCustomObject]@{ Package = $_.Name; Version = $_.Version; Project = $projName } }
}
$pkgGroups = $allPkgs | Group-Object Package | Sort-Object Count -Descending
[void]$sb.AppendLine('## Package References')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| Package | Used By | Version(s) |')
[void]$sb.AppendLine('|---------|---------|------------|')
foreach ($g in $pkgGroups) {
    $versions = ($g.Group | Select-Object -ExpandProperty Version -Unique) -join ', '
    $usedBy = ($g.Group | Select-Object -ExpandProperty Project -Unique) -join ', '
    [void]$sb.AppendLine("| ``$($g.Name)`` | $usedBy | $versions |")
}
[void]$sb.AppendLine()

# Per-project details
[void]$sb.AppendLine('## Project Details')
[void]$sb.AppendLine()
foreach ($p in $projects) {
    [void]$sb.AppendLine("### $($p.Name)")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("- **Path:** ``$($p.RelPath)``")
    [void]$sb.AppendLine("- **SDK:** $($p.SDK)")
    [void]$sb.AppendLine("- **TFM:** ``$($p.Framework)``")
    [void]$sb.AppendLine("- **Output:** $($p.OutputType)")
    if ($p.RootNamespace) { [void]$sb.AppendLine("- **Namespace:** ``$($p.RootNamespace)``") }
    if ($p.AssemblyName) { [void]$sb.AppendLine("- **Assembly:** ``$($p.AssemblyName)``") }
    if ($p.ImplicitUsings) { [void]$sb.AppendLine("- **Implicit usings:** $($p.ImplicitUsings)") }
    if ($p.Nullable) { [void]$sb.AppendLine("- **Nullable:** $($p.Nullable)") }
    [void]$sb.AppendLine()

    if ($p.PackageRefs) {
        [void]$sb.AppendLine('**Packages:**')
        [void]$sb.AppendLine()
        foreach ($pkg in $p.PackageRefs) {
            [void]$sb.AppendLine("- ``$($pkg.Name)`` $($pkg.Version)")
        }
        [void]$sb.AppendLine()
    }

    if ($p.ProjectRefs) {
        [void]$sb.AppendLine('**Project References:**')
        [void]$sb.AppendLine()
        foreach ($ref in $p.ProjectRefs) {
            [void]$sb.AppendLine("- ``$ref``")
        }
        [void]$sb.AppendLine()
    }
}

$report = $sb.ToString()

if ($OutFile) {
    $report | Out-File -FilePath $OutFile -Encoding utf8 -NoNewline
    Write-Host "Survey written to: $OutFile"
} else {
    $report
}
