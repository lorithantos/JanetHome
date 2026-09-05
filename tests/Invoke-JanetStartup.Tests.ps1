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
    $script:startJanet = Join-Path $script:repoRoot 'scripts\Start-Janet.ps1'

    # The stub records how it was bound, which is the only thing these tests are about.
    # $Other is what a positional token lands in once -Area is bound by name.  -NoLead is
    # declared LAST rather than beside -All: implicit positions follow declaration order, and
    # the entry that hands the stub a leftover positional depends on it still landing in
    # $Other.  It exists so the token tests can use the real manifest's own arg shape.
    $script:stubSource = @'
param([string]$Area = '', [switch]$All, [string]$Other = '', [switch]$NoLead)
[pscustomobject]@{ area = $Area; all = [bool]$All; other = $Other; noLead = [bool]$NoLead } | ConvertTo-Json -Compress
'@

    # A shim over a CLI that failed, which is the shape startup could not see until
    # 2026-09-04: nothing throws, the message goes to stderr and the exit code carries the
    # failure. -ErrorAction Continue is load-bearing -- startup runs with $ErrorActionPreference
    # 'Stop' and a child script inherits it, so without it the stub would terminate and be
    # caught, which is the path that already worked and not the one under test.
    $script:failingStubSource = @'
param([string]$Area = '')
Write-Error "no item is filed under an area matching '$Area'" -ErrorAction Continue
exit 3
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

    function New-FailingProbe {
        # Same contract as New-Probe, different stub. A separate file rather than a switch on
        # the healthy one, because the exit-code tests need BOTH in a single manifest.
        [CmdletBinding()]
        param()
        $stub = Join-Path $TestDrive 'FailingProbe.ps1'
        [System.IO.File]::WriteAllText($stub, $script:failingStubSource, (New-Object System.Text.UTF8Encoding $false))
        return [System.IO.Path]::GetRelativePath($script:repoRoot, $stub)
    }

    function New-Manifest {
        # The run entries, in order, each saying whatever the test wants about it -- including
        # an 'args' of the wrong shape. Typed as an array so a test can file two entries and
        # assert what the SECOND one reports; a single hashtable still binds, so every caller
        # written before that stays as it was.
        [CmdletBinding()]
        param([Parameter(Mandatory)][hashtable[]]$RunEntry)
        $manifest = Join-Path $TestDrive ("manifest-" + [guid]::NewGuid().ToString('N') + '.json')
        $json = [ordered]@{ version = 1; onMissing = 'fail'; run = @($RunEntry) } | ConvertTo-Json -Depth 5
        [System.IO.File]::WriteAllText($manifest, $json, (New-Object System.Text.UTF8Encoding $false))
        return $manifest
    }

    function New-ProjectDir {
        # A directory whose LEAF is the entire point: {projectName} is Split-Path -Leaf of it.
        # Made real on disk rather than passed as a bare string, because startup also joins
        # hook paths to the project dir -- a test that only works because these manifests
        # carry no rules would pass for a reason it never states.
        [CmdletBinding()]
        param([Parameter(Mandatory)][string]$Name)
        $dir = Join-Path $TestDrive $Name
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        return $dir
    }

    function Invoke-Startup {
        # -OutFile '' so a test never rewrites the repo's .janet\last-brief.json.
        # -ProjectDir is forwarded only when the test names one: leaving it unbound is what
        # exercises the $env:CLAUDE_PROJECT_DIR and current-directory fallbacks, and passing
        # an empty string would look the same to startup but not to a reader.
        [CmdletBinding()]
        param([Parameter(Mandatory)][string]$ManifestPath, [switch]$SkipRun, [string]$ProjectDir)
        $extra = @{}
        if ($PSBoundParameters.ContainsKey('ProjectDir')) { $extra['ProjectDir'] = $ProjectDir }
        $raw = & $script:startup -ManifestPath $ManifestPath -OutFile '' -SkipRun:$SkipRun @extra
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

Describe "the '{projectName}' args token" {
    It 'substitutes the leaf name of -ProjectDir before the token is bound' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', '{projectName}'); captureAs = 'probe' }
        $projectDir = New-ProjectDir 'RazorGraphTool'

        $brief = Invoke-Startup $manifest -ProjectDir $projectDir

        $brief.problems | Should -BeNullOrEmpty
        $brief.run[0].status | Should -Be 'ok'
        ($brief.captured.probe | ConvertFrom-Json).area | Should -Be 'RazorGraphTool'
    }

    It 'resolves the token from $env:CLAUDE_PROJECT_DIR when no -ProjectDir is passed' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', '{projectName}'); captureAs = 'probe' }
        # Startup runs in THIS process, so the variable is restored in a finally -- a leaked
        # CLAUDE_PROJECT_DIR would silently satisfy the current-directory test below.
        $prior = $env:CLAUDE_PROJECT_DIR
        $env:CLAUDE_PROJECT_DIR = New-ProjectDir 'EnvProject'
        try { $brief = Invoke-Startup $manifest }
        finally { $env:CLAUDE_PROJECT_DIR = $prior }

        $brief.problems | Should -BeNullOrEmpty
        ($brief.captured.probe | ConvertFrom-Json).area | Should -Be 'EnvProject'
    }

    It 'falls back to the current directory when neither -ProjectDir nor the variable is set' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', '{projectName}'); captureAs = 'probe' }
        # Moving the location is safe here: startup joins 'cmd' to the repo root, never to
        # the caller's location, so the probe is still reached from anywhere on the drive.
        $prior = $env:CLAUDE_PROJECT_DIR
        $env:CLAUDE_PROJECT_DIR = ''
        Push-Location (New-ProjectDir 'CwdProject')
        try { $brief = Invoke-Startup $manifest }
        finally {
            Pop-Location
            $env:CLAUDE_PROJECT_DIR = $prior
        }

        $brief.problems | Should -BeNullOrEmpty
        ($brief.captured.probe | ConvertFrom-Json).area | Should -Be 'CwdProject'
    }

    It 'refuses an unrecognised token under -SkipRun, before anything runs' {
        # The quiet failure this exists to prevent: '{repoName}' passed through as a literal
        # would bind to -Area, match nothing filed, and return the empty list of a finished
        # backlog. It is a problem, so onMissing 'fail' makes it a hard stop.
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', '{repoName}'); captureAs = 'probe' }
        $projectDir = New-ProjectDir 'RazorGraphTool'

        { Invoke-Startup $manifest -SkipRun -ProjectDir $projectDir } |
            Should -Throw -ExpectedMessage "*unresolved token '{repoName}'*"
    }

    It 'reports the RESOLVED args in the brief, which is where the narrowing is stated' {
        # The manifest's own shape. The brief's run entry is the only place a reader learns
        # which area the thread report was narrowed to, so a token surviving into it would
        # leave the captured items attributed to nothing.
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', '{projectName}', '-NoLead'); captureAs = 'probe' }
        $projectDir = New-ProjectDir 'RazorGraphTool'

        $brief = Invoke-Startup $manifest -ProjectDir $projectDir

        $brief.problems | Should -BeNullOrEmpty
        $brief.run[0].args | Should -Be @('-Area', 'RazorGraphTool', '-NoLead')
        $brief.run[0].args | Should -Not -Contain '{projectName}'
        ($brief.captured.probe | ConvertFrom-Json).noLead | Should -BeTrue
    }
}

Describe 'Start-Janet.ps1 -DryRun' {
    It 'stages a brief narrowed to the leaf of -Path' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', '{projectName}', '-NoLead'); captureAs = 'probe' }
        $projectDir = New-ProjectDir 'RazorGraphTool'
        $promptFile = Join-Path $TestDrive 'startup-prompt.md'

        # -ClaudePath is the stub: the launcher only Test-Paths it and -DryRun never invokes
        # it, so demanding a real claude install would make this a test about the machine.
        $anyLeafFile = Join-Path $TestDrive 'Probe.ps1'

        # The launcher has no -OutFile passthrough, so startup writes the repo's real
        # .janet\last-brief.json. Snapshot and restore it: leaving the repo's last brief
        # describing a probe would be a side effect on the next session, not a test.
        $lastBrief = Join-Path $script:repoRoot '.janet\last-brief.json'
        $saved = if (Test-Path $lastBrief -PathType Leaf) { [System.IO.File]::ReadAllBytes($lastBrief) } else { $null }

        # A decoy in the environment. -Path has to WIN, not merely be present: startup runs
        # before claude does, so its fallbacks answer for the launching shell rather than for
        # the session, and a launcher that dropped -Path would still look right without this.
        $prior = $env:CLAUDE_PROJECT_DIR
        $env:CLAUDE_PROJECT_DIR = New-ProjectDir 'DecoyProject'
        try {
            & $script:startJanet -DryRun -Path $projectDir -ManifestPath $manifest `
                -PromptFile $promptFile -ClaudePath $anyLeafFile 6>&1 | Out-Null
        }
        finally {
            $env:CLAUDE_PROJECT_DIR = $prior
            if ($null -ne $saved) { [System.IO.File]::WriteAllBytes($lastBrief, $saved) }
        }

        # The staged file, not the console output: it is what claude is actually pointed at,
        # and the brief is embedded in it verbatim.
        $staged = Get-Content $promptFile -Raw -Encoding UTF8
        $staged | Should -BeLike '*--- BRIEF ---*'

        $embedded = ($staged -split '--- BRIEF ---', 2)[1].Trim() | ConvertFrom-Json
        $embedded.problems | Should -BeNullOrEmpty
        $embedded.run[0].args | Should -Be @('-Area', 'RazorGraphTool', '-NoLead')
        ($embedded.captured.probe | ConvertFrom-Json).area | Should -Be 'RazorGraphTool'
    }
}

Describe 'a run entry that fails without throwing' {
    It "reports status 'error' and captures what the command wrote to stderr" {
        # The failure startup was blind to until 2026-09-04. Both halves matter: the status
        # was 'ok' AND the capture was the empty string, and an empty string is the worst
        # possible report of a failure because it is what a clean result looks like. Asserting
        # the status alone would pass on a fix that still captured nothing.
        $failing = New-FailingProbe
        $manifest = New-Manifest @{ cmd = $failing; args = @('-Area', 'NoSuchArea'); captureAs = 'probe' }

        $brief = Invoke-Startup $manifest

        $brief.run[0].status | Should -Be 'error'
        $brief.captured.probe | Should -Not -BeNullOrEmpty
        $brief.captured.probe | Should -BeLike "*no item is filed under an area matching 'NoSuchArea'*"

        # Deliberately NOT a problem: a failed run entry stays non-fatal, so Start-Janet still
        # launches. Routing it to 'problems' would make one bad script refuse the session.
        $brief.problems | Should -BeNullOrEmpty
    }

    It "still reports 'ok' for a healthy entry that follows a failing one" {
        # $LASTEXITCODE is process-wide and a PowerShell script that ends without 'exit' leaves
        # it exactly as it found it. So without the reset before each entry, the 3 left behind
        # by the entry above is still standing when the healthy one is judged, and a working
        # command is labelled error -- the same lie as the one this Describe is about, pointed
        # the other way.
        $failing = New-FailingProbe
        $probe = New-Probe
        $manifest = New-Manifest @(
            @{ cmd = $failing; args = @('-Area', 'NoSuchArea'); captureAs = 'failing' },
            @{ cmd = $probe; args = @('-Area', 'RazorGraphTool'); captureAs = 'probe' }
        )

        $brief = Invoke-Startup $manifest

        $brief.run[0].status | Should -Be 'error'
        $brief.run[1].status | Should -Be 'ok'
        ($brief.captured.probe | ConvertFrom-Json).area | Should -Be 'RazorGraphTool'
    }
}
