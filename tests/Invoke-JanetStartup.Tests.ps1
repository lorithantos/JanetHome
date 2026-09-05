#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for the manifest 'args' field in scripts\Invoke-JanetStartup.ps1.

.DESCRIPTION
    Each test writes its own manifest into TestDrive and points its one 'run' entry at a
    stub script that echoes what it was bound with. Startup joins 'cmd' to the REPO root,
    not to the manifest's directory, so the stub is reached by a relative path that climbs
    out of the repo -- the tests must not write into the repo to run.

    Pester 5 syntax. Test-PreCommit.ps1 runs everything under tests\ named *.Tests.ps1 and
    refuses to run them on the 3.4.0 that ships with Windows, so this file is what makes
    that gate step do work: Install-Module Pester if it reports the version.

    The first file of its kind here (2026-09-04). Before it, the startup script had no
    tests, and the fact that array splatting binds positionally -- '-Area' arriving as the
    literal string bound to the first parameter -- was found by running it, not by a test.
#>

BeforeAll {
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:startup = Join-Path $script:repoRoot 'scripts\Invoke-JanetStartup.ps1'

    # The stub records how it was bound, which is the only thing these tests are about.
    # $Other is what a positional token lands in once -Area is bound by name.
    $script:stubSource = @'
param([string]$Area = '', [switch]$All, [string]$Other = '')
[pscustomobject]@{ area = $Area; all = [bool]$All; other = $Other } | ConvertTo-Json -Compress
'@

    function New-Probe {
        # Writes the stub under TestDrive and returns the path startup will join to the repo
        # root to reach it. Same-drive is required for that relative path to exist at all;
        # a TEMP on another drive is reported rather than producing a 'missing command'
        # problem that would read as a bug in the script under test.
        [CmdletBinding()]
        param()
        $stub = Join-Path $TestDrive 'Probe.ps1'
        [System.IO.File]::WriteAllText($stub, $script:stubSource, (New-Object System.Text.UTF8Encoding $false))
        if ([System.IO.Path]::GetPathRoot($stub) -ne [System.IO.Path]::GetPathRoot($script:repoRoot)) {
            throw "TestDrive ($stub) is not on the repo's drive; startup resolves 'cmd' relative to $($script:repoRoot)."
        }
        return [System.IO.Path]::GetRelativePath($script:repoRoot, $stub)
    }

    function New-Manifest {
        # One run entry, captured as 'probe'. $RunEntry is whatever the test wants to say
        # about it, including an 'args' of the wrong shape.
        [CmdletBinding()]
        param([Parameter(Mandatory)][hashtable]$RunEntry)
        $manifest = Join-Path $TestDrive ("manifest-" + [guid]::NewGuid().ToString('N') + '.json')
        $json = [ordered]@{ version = 1; onMissing = 'fail'; run = @($RunEntry) } | ConvertTo-Json -Depth 5
        [System.IO.File]::WriteAllText($manifest, $json, (New-Object System.Text.UTF8Encoding $false))
        return $manifest
    }

    function Invoke-Startup {
        # -OutFile '' so a test never rewrites the repo's .janet\last-brief.json.
        [CmdletBinding()]
        param([Parameter(Mandatory)][string]$ManifestPath, [switch]$SkipRun)
        $raw = & $script:startup -ManifestPath $ManifestPath -OutFile '' -SkipRun:$SkipRun
        return ($raw | ConvertFrom-Json)
    }
}

Describe "manifest 'args'" {
    It 'binds -Name value by name, a lone -Switch as a switch, and the rest positionally' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', 'JanetHome', '-All', 'leftover'); captureAs = 'probe' }

        $brief = Invoke-Startup $manifest

        $brief.problems | Should -BeNullOrEmpty
        $brief.run[0].status | Should -Be 'ok'
        $brief.run[0].args | Should -Be @('-Area', 'JanetHome', '-All', 'leftover')

        $bound = $brief.captured.probe | ConvertFrom-Json
        $bound.area | Should -Be 'JanetHome'
        $bound.all | Should -BeTrue
        $bound.other | Should -Be 'leftover'
    }

    It 'still runs an entry that has no args, and emits no args field for it' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; captureAs = 'probe' }

        $brief = Invoke-Startup $manifest

        $brief.problems | Should -BeNullOrEmpty
        $brief.run[0].status | Should -Be 'ok'
        $brief.run[0].PSObject.Properties.Name | Should -Not -Contain 'args'
        ($brief.captured.probe | ConvertFrom-Json).area | Should -Be ''
    }

    It 'treats an empty args array as no args' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @(); captureAs = 'probe' }

        $brief = Invoke-Startup $manifest

        $brief.problems | Should -BeNullOrEmpty
        $brief.run[0].PSObject.Properties.Name | Should -Not -Contain 'args'
    }

    It 'refuses args that is a bare string rather than an array' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = '-All'; captureAs = 'probe' }

        { Invoke-Startup $manifest } | Should -Throw -ExpectedMessage "*'args' for '$probe' must be an array of strings, not a String*"
    }

    It 'refuses an array holding anything that is not a string' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', 3); captureAs = 'probe' }

        { Invoke-Startup $manifest } | Should -Throw -ExpectedMessage "*'args' for '$probe' must be an array of strings; found *"
    }

    It 'refuses a named token the command does not declare, under -SkipRun, before anything runs' {
        # The misspelling case. Without the metadata check '-Aera' would have bound as the
        # literal string to the first positional parameter and nothing would have failed.
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Aera', 'JanetHome'); captureAs = 'probe' }

        { Invoke-Startup $manifest -SkipRun } | Should -Throw -ExpectedMessage "*'$probe' declares no parameter '-Aera'*"
    }

    It 'lints valid args under -SkipRun without executing the command' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', 'JanetHome'); captureAs = 'probe' }

        $brief = Invoke-Startup $manifest -SkipRun

        $brief.problems | Should -BeNullOrEmpty
        $brief.run[0].status | Should -Be 'skipped'
        $brief.run[0].args | Should -Be @('-Area', 'JanetHome')
        $brief.captured.PSObject.Properties.Name | Should -Not -Contain 'probe'
    }

    It 'resolves the path from cmd alone, so args never changes what is found' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = "$probe-missing"; args = @('-Area', 'JanetHome'); captureAs = 'probe' }

        { Invoke-Startup $manifest -SkipRun } | Should -Throw -ExpectedMessage "*missing command '$probe-missing'*"
    }
}
