#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\Test-NamespaceFolders.ps1 -- the declared namespace of
    every .cs file must be RootNamespace plus its folder path.

.DESCRIPTION
    Each test writes a small fixture tree of .csproj and .cs files into a fresh temp
    directory and runs the real script over it with pwsh -File, reading the JSON
    envelope and the exit code. The fixtures cover a matching file at the root and
    in Interfaces/, a mismatched file in Models/ (the ImageSelectorV2 straggler's
    shape), a RootNamespace override, a block namespace, bin\ and obj\ skipping, a
    nested project, and a file with no namespace.
#>

BeforeAll {
    Set-StrictMode -Version Latest
    $script:subject = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Test-NamespaceFolders.ps1'

    function New-Fixture {
        # A fresh temp directory with the given files written into it; keys are
        # paths relative to the fixture root.
        param([hashtable]$Files)
        $root = Join-Path ([System.IO.Path]::GetTempPath()) ('ns-folders-' + [guid]::NewGuid().ToString('N'))
        foreach ($relative in $Files.Keys) {
            $full = Join-Path $root $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $full -Parent) | Out-Null
            [System.IO.File]::WriteAllText($full, $Files[$relative])
        }
        return $root
    }

    function Invoke-Lint {
        # Runs the real script and returns its envelope with the exit code.
        param([string]$Path)
        $raw = pwsh -NoProfile -File $script:subject -Path $Path | Out-String
        $code = $LASTEXITCODE
        [PSCustomObject]@{ Envelope = $raw | ConvertFrom-Json; ExitCode = $code }
    }

    $script:plainProject = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    $script:fixtures = [System.Collections.Generic.List[string]]::new()
}

AfterAll {
    foreach ($f in $script:fixtures) { Remove-Item -LiteralPath $f -Recurse -Force -ErrorAction SilentlyContinue }
}

Describe 'Test-NamespaceFolders' {
    It 'passes when every file declares RootNamespace plus its folder' {
        $root = New-Fixture @{
            'App\App.csproj'                = $script:plainProject
            'App\Service.cs'                = "namespace App;`npublic class Service { }"
            'App\Interfaces\IService.cs'    = "namespace App.Interfaces;`npublic interface IService { }"
            'App\Models\Deep\Thing.cs'      = "namespace App.Models.Deep;`npublic class Thing { }"
        }
        $script:fixtures.Add($root)
        $r = Invoke-Lint -Path $root
        $r.ExitCode | Should -Be 0
        $r.Envelope.returned | Should -Be 0
        $r.Envelope.filesScanned | Should -Be 3
    }

    It 'reports a file whose namespace ignores its folder, and exits 1' {
        $root = New-Fixture @{
            'App\App.csproj'                 = $script:plainProject
            'App\Models\CatalogViewModels.cs' = "namespace App.Helpers;`npublic class CatalogPageResult { }"
        }
        $script:fixtures.Add($root)
        $r = Invoke-Lint -Path $root
        $r.ExitCode | Should -Be 1
        $r.Envelope.returned | Should -Be 1
        $m = $r.Envelope.mismatches[0]
        $m.declared | Should -Be 'App.Helpers'
        $m.expected | Should -Be 'App.Models'
        $m.file | Should -Be 'Models\CatalogViewModels.cs'
    }

    It 'expects the RootNamespace from the csproj when it overrides the project name' {
        $override = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RootNamespace>Company.Product</RootNamespace></PropertyGroup></Project>'
        $root = New-Fixture @{
            'App\App.csproj'             = $override
            'App\Interfaces\IService.cs' = "namespace Company.Product.Interfaces;`npublic interface IService { }"
            'App\Root.cs'                = "namespace App;`npublic class Root { }"
        }
        $script:fixtures.Add($root)
        $r = Invoke-Lint -Path $root
        $r.Envelope.projects[0].rootNamespace | Should -Be 'Company.Product'
        $r.Envelope.projects[0].rootNamespaceSource | Should -Be 'csproj'
        $r.Envelope.returned | Should -Be 1
        $r.Envelope.mismatches[0].file | Should -Be 'Root.cs'
        $r.Envelope.mismatches[0].expected | Should -Be 'Company.Product'
    }

    It 'reads a block namespace and compares case-sensitively' {
        $root = New-Fixture @{
            'App\App.csproj'          = $script:plainProject
            'App\Models\Block.cs'     = "using System;`nnamespace App.Models`n{`n    public class Block { }`n}"
            'App\Models\WrongCase.cs' = "namespace App.models;`npublic class WrongCase { }"
        }
        $script:fixtures.Add($root)
        $r = Invoke-Lint -Path $root
        $r.Envelope.returned | Should -Be 1
        $r.Envelope.mismatches[0].file | Should -Be 'Models\WrongCase.cs'
    }

    It 'skips bin and obj, and lists a file with no namespace without failing' {
        $root = New-Fixture @{
            'App\App.csproj'               = $script:plainProject
            'App\Program.cs'               = "System.Console.WriteLine(1);"
            'App\obj\Debug\Generated.g.cs' = "namespace Nowhere;"
            'App\bin\Debug\Copied.cs'      = "namespace Nowhere;"
        }
        $script:fixtures.Add($root)
        $r = Invoke-Lint -Path $root
        $r.ExitCode | Should -Be 0
        $r.Envelope.filesScanned | Should -Be 1
        @($r.Envelope.noNamespace).Count | Should -Be 1
        $r.Envelope.noNamespace[0].file | Should -Be 'Program.cs'
    }

    It 'checks a nested project against its own root, not its parent' {
        $root = New-Fixture @{
            'App\App.csproj'                    = $script:plainProject
            'App\Inner\Inner.csproj'            = $script:plainProject
            'App\Inner\Models\Thing.cs'         = "namespace Inner.Models;`npublic class Thing { }"
        }
        $script:fixtures.Add($root)
        $r = Invoke-Lint -Path $root
        $r.ExitCode | Should -Be 0
        $r.Envelope.projectsScanned | Should -Be 2
        $r.Envelope.filesScanned | Should -Be 1
    }

    It 'accepts a .csproj path as well as a directory' {
        $root = New-Fixture @{
            'App\App.csproj'       = $script:plainProject
            'App\Models\Thing.cs'  = "namespace App.Models;`npublic class Thing { }"
            'Other\Other.csproj'   = $script:plainProject
            'Other\Bad.cs'         = "namespace Wrong;"
        }
        $script:fixtures.Add($root)
        $r = Invoke-Lint -Path (Join-Path $root 'App\App.csproj')
        $r.ExitCode | Should -Be 0
        $r.Envelope.projectsScanned | Should -Be 1
    }
}
