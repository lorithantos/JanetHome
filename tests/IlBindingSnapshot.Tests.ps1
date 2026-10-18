#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\Get-IlBindingSnapshot.ps1 and
    scripts\Compare-IlBindingSnapshot.ps1 -- the pair that answers "did my refactor
    change what anything binds to".

.DESCRIPTION
    Builds a throwaway class library in a temp folder with a PLANTED REBIND: the
    caller has 'using A;' and calls "x".Hi(), which binds to A.AExt::Hi(object).
    Built with WITHB defined, another file adds 'global using B;', and the same
    source line now binds to B.BExt::Hi(string) -- a better match -- with a green
    build and no warning. The same switch adds 'global using Widget = B.Widget;',
    which moves typeof(Widget) from A.Widget to B.Widget: a TYPE rebind that the
    call-target view cannot see and the Full view must.

    The scripts run as child pwsh processes, because the exit code is part of the
    contract: a caller gates on it.

    Needs the .NET SDK on PATH. Three small builds, about ten seconds in all.
#>

BeforeAll {
    Set-StrictMode -Version Latest
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:snapshotScript = Join-Path $script:repoRoot 'scripts\Get-IlBindingSnapshot.ps1'
    $script:compareScript = Join-Path $script:repoRoot 'scripts\Compare-IlBindingSnapshot.ps1'
    $script:work = Join-Path ([System.IO.Path]::GetTempPath()) ("il-binding-tests-" + [guid]::NewGuid().ToString('N'))
    $project = Join-Path $script:work 'demo'
    New-Item -ItemType Directory -Path $project -Force | Out-Null

    Set-Content -LiteralPath (Join-Path $project 'Demo.csproj') -Value '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup></Project>'
    Set-Content -LiteralPath (Join-Path $project 'Ext.cs') -Value @'
namespace A { public static class AExt { public static string Hi(this object o) => "A"; } public class Widget { } }
namespace B { public static class BExt { public static string Hi(this string s) => "B"; } public class Widget { } }
'@
    Set-Content -LiteralPath (Join-Path $project 'Caller.cs') -Value @'
using A;
namespace C { public static class Caller { public static string Call() => "x".Hi(); public static System.Type Kind() => typeof(Widget); } }
'@
    Set-Content -LiteralPath (Join-Path $project 'Rebind.cs') -Value @'
#if WITHB
global using B;
global using Widget = B.Widget;
#endif
'@

    function Invoke-DemoBuild {
        param([string]$OutDir, [string[]]$Extra = @())
        $output = & dotnet build (Join-Path $project 'Demo.csproj') -c Release -o $OutDir -nologo @Extra 2>&1
        if ($LASTEXITCODE -ne 0) { throw "demo build failed: $($output -join [Environment]::NewLine)" }
    }

    function Invoke-Script {
        # Runs a script in a child pwsh and returns its exit code and parsed JSON output.
        param([string]$ScriptPath, [string[]]$Arguments)
        $raw = & pwsh -NoProfile -NonInteractive -File $ScriptPath @Arguments
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Json = ($raw -join "`n" | ConvertFrom-Json) }
    }

    function Get-DemoSnapshot {
        param([string]$Name, [string]$Folder, [string]$Detail)
        $file = Join-Path $script:work "$Name.json"
        $run = Invoke-Script -ScriptPath $script:snapshotScript -Arguments @('-Path', $Folder, '-Include', 'Demo.dll', '-Detail', $Detail, '-OutFile', $file)
        if ($run.ExitCode -ne 0) { throw "snapshot $Name failed: $($run.Json.error)" }
        $file
    }

    function Compare-Snapshot {
        param([string]$BeforeFile, [string]$AfterFile)
        Invoke-Script -ScriptPath $script:compareScript -Arguments @('-Before', $BeforeFile, '-After', $AfterFile)
    }

    # Order matters: the 'again' build follows the WITHB build, so it is a genuine
    # recompile of the original source, not an incremental copy of the first output.
    Invoke-DemoBuild -OutDir (Join-Path $script:work 'out-before')
    Invoke-DemoBuild -OutDir (Join-Path $script:work 'out-after') -Extra @('-p:DefineConstants=WITHB')
    Invoke-DemoBuild -OutDir (Join-Path $script:work 'out-again')

    $script:beforeCalls = Get-DemoSnapshot -Name 'before-calls' -Folder (Join-Path $script:work 'out-before') -Detail 'CallTargets'
    $script:afterCalls = Get-DemoSnapshot -Name 'after-calls' -Folder (Join-Path $script:work 'out-after') -Detail 'CallTargets'
    $script:againCalls = Get-DemoSnapshot -Name 'again-calls' -Folder (Join-Path $script:work 'out-again') -Detail 'CallTargets'
    $script:beforeFull = Get-DemoSnapshot -Name 'before-full' -Folder (Join-Path $script:work 'out-before') -Detail 'Full'
    $script:afterFull = Get-DemoSnapshot -Name 'after-full' -Folder (Join-Path $script:work 'out-after') -Detail 'Full'
    $script:projectFile = Join-Path $project 'Demo.csproj'

    # The sequence diff, lifted out of the script's AST so an edit to it is what runs.
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:compareScript, [ref]$null, [ref]$null)
    $sequence = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Compare-Sequence'
        }, $true)
    . ([scriptblock]::Create($sequence.Extent.Text))
}

AfterAll {
    if (Test-Path -LiteralPath $script:work) { Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue }
}

Describe 'Compare-IlBindingSnapshot on the planted rebind' {
    It 'reports exactly the one rebound call target, with its method, and exits 1' {
        $run = Compare-Snapshot -BeforeFile $script:beforeCalls -AfterFile $script:afterCalls
        $run.ExitCode | Should -Be 1
        $run.Json.changed | Should -BeTrue
        $run.Json.summary.changedCallSites | Should -Be 1
        $run.Json.summary.methodsChanged | Should -Be 1
        $run.Json.summary.methodsAdded | Should -Be 0
        $run.Json.summary.methodsRemoved | Should -Be 0

        $method = @($run.Json.changes.methods.changed)[0]
        $method.assembly | Should -Be 'Demo'
        $method.method | Should -Be 'C.Caller::Call():String'
        $change = @($method.calls)
        $change.Count | Should -Be 1
        $change[0].kind | Should -Be 'changed'
        $change[0].before | Should -Be 'A.AExt::Hi(Object):String'
        $change[0].after | Should -Be 'B.BExt::Hi(String):String'
    }

    It 'compares a clean recompile of the same source as unchanged, and exits 0' {
        $run = Compare-Snapshot -BeforeFile $script:beforeCalls -AfterFile $script:againCalls
        $run.ExitCode | Should -Be 0
        $run.Json.changed | Should -BeFalse
        $run.Json.summary.changedCallSites | Should -Be 0
    }

    It 'sees the typeof rebind only at Full detail' {
        $run = Compare-Snapshot -BeforeFile $script:beforeFull -AfterFile $script:afterFull
        $run.ExitCode | Should -Be 1
        $kind = @($run.Json.changes.methods.changed | Where-Object { $_.method -eq 'C.Caller::Kind():System.Type' })
        $kind.Count | Should -Be 1
        @($kind[0].PSObject.Properties.Name) | Should -Not -Contain 'calls'
        @($kind[0].operands)[0].before | Should -Be 'ldtoken A.Widget'
        @($kind[0].operands)[0].after | Should -Be 'ldtoken B.Widget'
        $run.Json.summary.changedBodies | Should -Be 2
    }

    It 'refuses snapshots taken at different detail with exit 2' {
        $run = Compare-Snapshot -BeforeFile $script:beforeCalls -AfterFile $script:afterFull
        $run.ExitCode | Should -Be 2
        $run.Json.ok | Should -BeFalse
        $run.Json.error | Should -Match 'different detail'
    }
}

Describe 'Get-IlBindingSnapshot inputs' {
    It 'fails loudly when a folder holds no assemblies' {
        $empty = Join-Path $script:work 'empty'
        New-Item -ItemType Directory -Path $empty -Force | Out-Null
        $run = Invoke-Script -ScriptPath $script:snapshotScript -Arguments @('-Path', $empty, '-OutFile', (Join-Path $script:work 'empty.json'))
        $run.ExitCode | Should -Be 1
        $run.Json.error | Should -Match 'No assemblies to snapshot'
    }

    It 'builds a project and snapshots its TargetPath, matching the folder snapshot' {
        $file = Join-Path $script:work 'project-calls.json'
        $run = Invoke-Script -ScriptPath $script:snapshotScript -Arguments @('-Solution', $script:projectFile, '-Configuration', 'Release', '-OutFile', $file)
        $run.ExitCode | Should -Be 0
        $run.Json.assemblies | Should -Be 1
        (Compare-Snapshot -BeforeFile $script:beforeCalls -AfterFile $file).ExitCode | Should -Be 0
    }
}

Describe 'Compare-Sequence' {
    It 'pairs a replaced item as one change at its site' {
        $changes = Compare-Sequence -Left @('a', 'b', 'c') -Right @('a', 'x', 'c')
        $changes.Count | Should -Be 1
        $changes[0].kind | Should -Be 'changed'
        $changes[0].beforeSite | Should -Be 1
        $changes[0].after | Should -Be 'x'
    }

    It 'reports an insertion as added, without disturbing the sites after it' {
        $changes = Compare-Sequence -Left @('a', 'b', 'c') -Right @('a', 'b', 'n', 'c')
        $changes.Count | Should -Be 1
        $changes[0].kind | Should -Be 'added'
        $changes[0].afterSite | Should -Be 2
    }

    It 'reports a removal as removed' {
        $changes = Compare-Sequence -Left @('a', 'b', 'c') -Right @('a', 'c')
        $changes.Count | Should -Be 1
        $changes[0].kind | Should -Be 'removed'
        $changes[0].before | Should -Be 'b'
    }

    It 'returns nothing for equal sequences' {
        (Compare-Sequence -Left @('a', 'b') -Right @('a', 'b')).Count | Should -Be 0
    }
}
