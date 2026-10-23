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
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Test fixture builders: probes, manifests and project dirs under TestDrive, where -WhatIf has no meaning.')]
param()

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

    It 'binds a nested array whole to an array-typed parameter' {
        # Since 2026-10-22, for the razorgraph entry's -ServerArgument: five values, two of
        # them starting with '--', which a flat token list cannot delimit.
        $stub = Join-Path $TestDrive 'ArrayProbe.ps1'
        [System.IO.File]::WriteAllText($stub, @'
param([string]$Name = '', [string[]]$ServerArgument = @())
[pscustomobject]@{ name = $Name; serverArgument = @($ServerArgument) } | ConvertTo-Json -Compress
'@, (New-Object System.Text.UTF8Encoding $false))
        $probe = [System.IO.Path]::GetRelativePath($script:repoRoot, $stub)
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Name', 'X', '-ServerArgument', @('--http', '--port', '7718', 'janet mcp-token --print')); captureAs = 'probe' }

        $brief = Invoke-Startup $manifest

        $brief.problems | Should -BeNullOrEmpty
        $bound = $brief.captured.probe | ConvertFrom-Json
        $bound.name | Should -Be 'X'
        $bound.serverArgument | Should -Be @('--http', '--port', '7718', 'janet mcp-token --print')
    }

    It 'refuses a nested array after a parameter that takes one value' {
        $probe = New-Probe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', @('a', 'b')); captureAs = 'probe' }

        { Invoke-Startup $manifest -SkipRun } | Should -Throw -ExpectedMessage "*'-Area' for '$probe' takes one value, not an array*"
    }

    It 'refuses a nested array in a positional slot' {
        $probe = New-Probe
        # The unary comma: @(@('a','b')) would flatten to two strings before reaching JSON.
        $manifest = New-Manifest @{ cmd = $probe; args = @(, @('a', 'b')); captureAs = 'probe' }

        { Invoke-Startup $manifest -SkipRun } | Should -Throw -ExpectedMessage "*nested array at position 0*"
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

<#
    The razorgraph run entry (2026-10-22) replaced Ensure-RazorGraphServer.ps1, a forwarder
    that carried razorgraph's launch values as defaults. The manifest is now the only copy
    of them, and Invoke-GraphGuard.ps1 re-runs the entry through lib\RunArguments.ps1 when
    the server is down, so both the values and that re-run path are pinned here.
#>
Describe 'the razorgraph run entry and Invoke-ManifestRunEntry' {
    BeforeAll {
        . (Join-Path $script:repoRoot 'scripts\lib\RunArguments.ps1')
    }

    It "binds the real manifest's razorgraph entry to Ensure-McpServer.ps1 with the forwarder's values" {
        $manifest = Get-Content -LiteralPath (Join-Path $script:repoRoot 'startup-manifest.json') -Raw | ConvertFrom-Json
        $entry = @($manifest.run) | Where-Object { $_.captureAs -eq 'razorGraphServer' }
        $full = Join-Path $script:repoRoot $entry.cmd

        $resolved = Resolve-RunArguments $entry $entry.cmd $full $true ''

        $resolved.problems | Should -BeNullOrEmpty
        $entry.cmd | Should -Be 'scripts\Ensure-McpServer.ps1'
        $resolved.named['Name'] | Should -Be 'RazorGraph.Mcp'
        $resolved.named['Port'] | Should -Be '7718'
        $resolved.named['Base'] | Should -Be 'C:\repos\RazorGraphTool'
        $resolved.named['BinDir'] | Should -Be 'C:\repos\RazorGraphTool\.mcp-bin\current'
        $resolved.named['SourceDir'] | Should -Be 'C:\repos\RazorGraphTool\src\RazorGraph.Mcp'
        $resolved.named['ServerArgument'] | Should -Be @('--http', '--port', '7718', '--auth-token-command', 'janet mcp-token --print')
        $resolved.positional | Should -BeNullOrEmpty
    }

    It 'runs the entry named by captureAs with its args resolved' {
        $root = Join-Path $TestDrive 'runentry'
        New-Item -ItemType Directory -Path (Join-Path $root 'scripts') -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $root 'scripts\Echo.ps1'), @'
param([string]$Name = '', [string[]]$ServerArgument = @())
"$Name|$($ServerArgument -join ',')"
'@, (New-Object System.Text.UTF8Encoding $false))
        $json = [ordered]@{ run = @(
            @{ cmd = 'scripts\Echo.ps1'; args = @('-Name', 'other'); captureAs = 'other' },
            @{ cmd = 'scripts\Echo.ps1'; args = @('-Name', 'rg', '-ServerArgument', @('--http', '--port', '7718')); captureAs = 'razorGraphServer' }
        ) } | ConvertTo-Json -Depth 5
        [System.IO.File]::WriteAllText((Join-Path $root 'startup-manifest.json'), $json, (New-Object System.Text.UTF8Encoding $false))

        Invoke-ManifestRunEntry -RepoRoot $root -CaptureAs 'razorGraphServer' | Should -Be 'rg|--http,--port,7718'
        { Invoke-ManifestRunEntry -RepoRoot $root -CaptureAs 'absent' } | Should -Throw -ExpectedMessage "*no run entry with captureAs 'absent'*"
    }
}

<#
    Thread report contract 5 (2026-10-22): a selector that matches no item answers count 0
    with 'unmatchedArea' set, where a finished area answers count 0 with it null. Startup
    picks the selector from the project dir's name, so the brief has to say which one it got.
    The stub stands in for Get-ThreadReport.ps1 and echoes an envelope of that shape, so no
    test here reads or writes the machine's real thread list.
#>
Describe "a thread report whose area matched nothing" {
    BeforeAll {
        $script:reportStubSource = @'
param([string]$Area = '', [switch]$NoLead)
$flag = if ($Area -eq 'JanetHome') { $null } else { [ordered]@{ area = $Area; knownAreas = @('gamehub', 'JanetHome') } }
[ordered]@{ contract = 5; count = 0; active = $null; areas = @(); items = @(); notesLength = 0; error = $null; unmatchedArea = $flag } | ConvertTo-Json -Compress -Depth 4
'@
        function New-ReportProbe {
            [CmdletBinding()]
            param()
            $stub = Join-Path $TestDrive 'ReportProbe.ps1'
            [System.IO.File]::WriteAllText($stub, $script:reportStubSource, (New-Object System.Text.UTF8Encoding $false))
            return [System.IO.Path]::GetRelativePath($script:repoRoot, $stub)
        }
    }

    It "adds a notice naming the area and the areas it was matched against" {
        $probe = New-ReportProbe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', '{projectName}', '-NoLead'); captureAs = 'threadReport' }

        $brief = Invoke-Startup $manifest -ProjectDir (New-ProjectDir 'repos') 3>$null

        $brief.run[0].status | Should -Be 'ok'
        @($brief.notices).Count | Should -Be 1
        $brief.notices[0] | Should -BeLike "threads:*narrowed to area 'repos'*matches no thread item*gamehub, JanetHome*"
        # A fact about a capture, never a reason to refuse the session.
        $brief.problems | Should -BeNullOrEmpty
        $brief.enforcement | Should -BeNullOrEmpty
    }

    It 'adds no notice when the area matched, even with nothing open' {
        $probe = New-ReportProbe
        $manifest = New-Manifest @{ cmd = $probe; args = @('-Area', '{projectName}', '-NoLead'); captureAs = 'threadReport' }

        $brief = Invoke-Startup $manifest -ProjectDir (New-ProjectDir 'JanetHome') 3>$null

        $brief.notices | Should -BeNullOrEmpty
    }

    It 'adds no notice for a capture that is not a report envelope' {
        $manifest = New-Manifest @{ cmd = (New-Probe); args = @('-Area', 'JanetHome'); captureAs = 'probe' }

        $brief = Invoke-Startup $manifest 3>$null

        $brief.notices | Should -BeNullOrEmpty
    }
}

<#
    ENFORCED means a settings file DECLARES the hook, not that the script is on disk.
    Until 2026-10-21 the project-level arm was Test-Path on the script alone, so in
    every ordinary JanetHome session the brief said ENFORCED whether or not
    .claude\settings.json declared anything -- reproduced in a scratch project dir
    with the scripts present and no hooks block: all five hook-backed rules read
    ENFORCED and 'enforcement' was empty.

    Every case runs with $env:USERPROFILE pointed at a TestDrive directory, so the
    machine's real ~\.claude\settings.json can neither rescue nor sink a rule, and
    no real settings file is read for the user arm or written by anything here.
#>
Describe 'hook-backed rules' {
    BeforeAll {
        $script:guardRule = [ordered]@{ text = 'ENFORCED: fixture rule backed by a hook.'; enforcedBy = 'scripts\FixtureGuard.ps1' }

        function New-RuleManifest {
            # One probe run entry, so the manifest is the ordinary shape, plus the rules, and
            # the server-to-rule map when a test is about MCP servers (it lives in the
            # manifest since 2026-10-22, so a manifest without one watches nothing).
            [CmdletBinding()]
            param([Parameter(Mandatory)][System.Collections.IDictionary[]]$Rule, [System.Collections.IDictionary]$McpDependents)
            $probe = New-Probe
            $manifest = Join-Path $TestDrive ("manifest-" + [guid]::NewGuid().ToString('N') + '.json')
            $body = [ordered]@{ version = 1; onMissing = 'fail'; run = @(@{ cmd = $probe; captureAs = 'probe' }); rules = @($Rule) }
            if ($McpDependents) { $body['mcpDependents'] = $McpDependents }
            $json = $body | ConvertTo-Json -Depth 6
            [System.IO.File]::WriteAllText($manifest, $json, (New-Object System.Text.UTF8Encoding $false))
            return $manifest
        }

        function New-SettingsJson {
            # A settings file whose PreToolUse hook command names $Script, resolved the way
            # the real project-level commands resolve theirs.
            [CmdletBinding()]
            param([Parameter(Mandatory)][string]$Script)
            $command = "`$d = `$env:CLAUDE_PROJECT_DIR; `$s = Join-Path `$d 'scripts\$Script'; if (Test-Path `$s) { & `$s }"
            return ([ordered]@{
                    hooks = [ordered]@{
                        PreToolUse = @([ordered]@{ matcher = 'Write|Edit'; hooks = @([ordered]@{ type = 'command'; command = $command }) })
                    }
                } | ConvertTo-Json -Depth 8)
        }

        function New-HookProject {
            # A project dir carrying the backing script, and optionally a settings file
            # under .claude with the given text.
            [CmdletBinding()]
            param([Parameter(Mandatory)][string]$Name, [string]$SettingsText, [string]$SettingsFile = 'settings.json')
            $dir = New-ProjectDir $Name
            New-Item -ItemType Directory -Path (Join-Path $dir 'scripts'), (Join-Path $dir '.claude') -Force | Out-Null
            Set-Content -Path (Join-Path $dir 'scripts\FixtureGuard.ps1') -Value '# fixture hook' -Encoding UTF8
            if ($SettingsText) { Set-Content -Path (Join-Path $dir ".claude\$SettingsFile") -Value $SettingsText -Encoding UTF8 }
            return $dir
        }

        function Invoke-WithUserProfile {
            # Runs startup with USERPROFILE pointed at a fixture, holding the given user
            # settings text if any, and restores it however the run ends.
            [CmdletBinding()]
            param([Parameter(Mandatory)][string]$ManifestPath, [Parameter(Mandatory)][string]$ProjectDir, [string]$UserSettingsText, [string]$ClaudeJsonText)
            $profileDir = Join-Path $TestDrive ("profile-" + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path (Join-Path $profileDir '.claude') -Force | Out-Null
            if ($UserSettingsText) { Set-Content -Path (Join-Path $profileDir '.claude\settings.json') -Value $UserSettingsText -Encoding UTF8 }
            # ~\.claude.json, the harness's own per-project state, for the /mcp toggle cases.
            if ($ClaudeJsonText) { Set-Content -Path (Join-Path $profileDir '.claude.json') -Value $ClaudeJsonText -Encoding UTF8 }
            $prior = $env:USERPROFILE
            $env:USERPROFILE = $profileDir
            try { return Invoke-Startup $ManifestPath -SkipRun -ProjectDir $ProjectDir 3>$null }
            finally { $env:USERPROFILE = $prior }
        }
    }

    It 'keeps ENFORCED when the project settings declare a hook running the script' {
        $project = New-HookProject 'Declared' -SettingsText (New-SettingsJson 'FixtureGuard.ps1')
        $brief = Invoke-WithUserProfile (New-RuleManifest $script:guardRule) $project

        $brief.rules | Should -Be @('ENFORCED: fixture rule backed by a hook.')
        $brief.enforcement | Should -BeNullOrEmpty
        $brief.problems | Should -BeNullOrEmpty
    }

    It 'relabels ADVISORY and reports it when the script is on disk but no hook is declared' {
        # The case from the thread item: scripts in place, hooks block gone.
        $project = New-HookProject 'Undeclared' -SettingsText '{ "permissions": { "allow": [] } }'
        $brief = Invoke-WithUserProfile (New-RuleManifest $script:guardRule) $project

        $brief.rules | Should -Be @('ADVISORY (claims ENFORCED; hook not wired here): fixture rule backed by a hook.')
        @($brief.enforcement).Count | Should -Be 1
        $brief.enforcement[0] | Should -BeLike '*not wired*scripts\FixtureGuard.ps1*'
        $brief.problems | Should -BeNullOrEmpty -Because 'an unwired hook is reported, never fatal'
    }

    It 'does not count the script named outside the hooks section' {
        $project = New-HookProject 'Mentioned' -SettingsText '{ ".comment": ["FixtureGuard.ps1 used to be wired here"], "hooks": {} }'
        $brief = Invoke-WithUserProfile (New-RuleManifest $script:guardRule) $project

        $brief.rules[0] | Should -BeLike 'ADVISORY (claims ENFORCED*'
        @($brief.enforcement).Count | Should -Be 1
    }

    It 'counts a hook declared in the project settings.local.json' {
        $project = New-HookProject 'LocalDeclared' -SettingsText (New-SettingsJson 'FixtureGuard.ps1') -SettingsFile 'settings.local.json'
        $brief = Invoke-WithUserProfile (New-RuleManifest $script:guardRule) $project

        $brief.rules | Should -Be @('ENFORCED: fixture rule backed by a hook.')
        $brief.enforcement | Should -BeNullOrEmpty
    }

    It 'arms a rule from user-level settings when the project dir is another repo' {
        # Arm 2: the user hook runs this repo's script by absolute path, so the backing
        # script must be a real one in this repo; the project dir carries nothing.
        $rule = [ordered]@{ text = 'ENFORCED: fixture rule on a real script.'; enforcedBy = 'scripts\Invoke-EditGuard.ps1' }
        $elsewhere = New-ProjectDir 'OtherRepo'
        $brief = Invoke-WithUserProfile (New-RuleManifest $rule) $elsewhere -UserSettingsText (New-SettingsJson 'Invoke-EditGuard.ps1')

        $brief.rules | Should -Be @('ENFORCED: fixture rule on a real script.')
        $brief.enforcement | Should -BeNullOrEmpty
    }

    It 'relabels a rule the user-level settings do not declare, for another repo' {
        $rule = [ordered]@{ text = 'ENFORCED: fixture rule on a real script.'; enforcedBy = 'scripts\Invoke-EditGuard.ps1' }
        $elsewhere = New-ProjectDir 'OtherRepoUnwired'
        $brief = Invoke-WithUserProfile (New-RuleManifest $rule) $elsewhere -UserSettingsText '{ "hooks": {} }'

        $brief.rules[0] | Should -BeLike 'ADVISORY (claims ENFORCED*'
        @($brief.enforcement).Count | Should -Be 1
    }

    It 'does not let user-level settings arm a rule when the project dir is this repo' {
        # The user hooks skip this repo so the two levels never double-fire. The backing
        # script exists here but is no hook, so this repo's real .claude\settings.json --
        # read, never written -- declares nothing that runs it.
        $rule = [ordered]@{ text = 'ENFORCED: fixture rule on a script that is no hook.'; enforcedBy = 'scripts\Invoke-JanetStartup.ps1' }
        $brief = Invoke-WithUserProfile (New-RuleManifest $rule) $script:repoRoot -UserSettingsText (New-SettingsJson 'Invoke-JanetStartup.ps1')

        $brief.rules[0] | Should -BeLike 'ADVISORY (claims ENFORCED*'
        @($brief.enforcement).Count | Should -Be 1
    }

    <#
        A wired hook is not enough when the rule's promise runs through an MCP server the
        session does not load. On 2026-10-10 C:\repos\.claude\settings.local.json listed
        janet and razorgraph under disabledMcpjsonServers and the brief still printed the
        graph-first rule as ENFORCED. The project carries a fixture Invoke-GraphGuard.ps1
        and declares its hook, so the hook arm passes and only the MCP check can relabel.
    #>
    Context 'MCP servers disabled in settings' {
        BeforeAll {
            $script:graphRule = [ordered]@{ text = 'ENFORCED: fixture graph-first rule.'; enforcedBy = 'scripts\Invoke-GraphGuard.ps1' }
            # The manifest's server-to-rule map, as the real one is shaped for these rules.
            $script:mcpMap = [ordered]@{ janet = @(); razorgraph = @('scripts\Invoke-GraphGuard.ps1') }

            function New-ClaudeJson {
                # ~\.claude.json with one project entry: the project dir's path the way the
                # harness keys it (forward slashes), carrying the given per-project lists.
                [CmdletBinding()]
                param([Parameter(Mandatory)][string]$ProjectDir, [Parameter(Mandatory)][System.Collections.IDictionary]$Lists)
                $key = [System.IO.Path]::GetFullPath($ProjectDir) -replace '\\', '/'
                return ([ordered]@{ oauthAccount = [ordered]@{ note = 'fixture' }; projects = [ordered]@{ $key = $Lists } } | ConvertTo-Json -Depth 6)
            }

            function New-GraphProject {
                # A project dir whose settings.json wires the graph guard, and whose
                # settings.local.json holds the given text when there is one.
                [CmdletBinding()]
                param([Parameter(Mandatory)][string]$Name, [string]$LocalSettingsText)
                $dir = New-ProjectDir $Name
                New-Item -ItemType Directory -Path (Join-Path $dir 'scripts'), (Join-Path $dir '.claude') -Force | Out-Null
                Set-Content -Path (Join-Path $dir 'scripts\Invoke-GraphGuard.ps1') -Value '# fixture hook' -Encoding UTF8
                Set-Content -Path (Join-Path $dir '.claude\settings.json') -Value (New-SettingsJson 'Invoke-GraphGuard.ps1') -Encoding UTF8
                if ($LocalSettingsText) { Set-Content -Path (Join-Path $dir '.claude\settings.local.json') -Value $LocalSettingsText -Encoding UTF8 }
                return $dir
            }
        }

        It 'relabels the graph-first rule and reports it when razorgraph is disabled' {
            $project = New-GraphProject 'RazorDisabled' -LocalSettingsText '{ "disabledMcpjsonServers": ["razorgraph"] }'
            $brief = Invoke-WithUserProfile (New-RuleManifest $script:graphRule -McpDependents $script:mcpMap) $project

            $brief.rules | Should -Be @('ADVISORY (claims ENFORCED; MCP server razorgraph disabled here): fixture graph-first rule.')
            @($brief.enforcement).Count | Should -Be 1
            $brief.enforcement[0] | Should -BeLike "*'razorgraph'*disabledMcpjsonServers*settings.local.json*scripts\Invoke-GraphGuard.ps1*"
            $brief.problems | Should -BeNullOrEmpty -Because 'a disabled server is reported, never fatal'
        }

        It 'keeps ENFORCED and reports nothing when no server it needs is disabled' {
            $project = New-GraphProject 'OtherDisabled' -LocalSettingsText '{ "disabledMcpjsonServers": ["some-other-server"] }'
            $brief = Invoke-WithUserProfile (New-RuleManifest $script:graphRule -McpDependents $script:mcpMap) $project

            $brief.rules | Should -Be @('ENFORCED: fixture graph-first rule.')
            $brief.enforcement | Should -BeNullOrEmpty
        }

        It 'reports janet disabled at user level without relabelling a rule that does not need it' {
            $project = New-GraphProject 'JanetDisabled'
            $brief = Invoke-WithUserProfile (New-RuleManifest $script:graphRule -McpDependents $script:mcpMap) $project -UserSettingsText '{ "disabledMcpjsonServers": ["janet"] }'

            $brief.rules | Should -Be @('ENFORCED: fixture graph-first rule.')
            @($brief.enforcement).Count | Should -Be 1
            $brief.enforcement[0] | Should -BeLike "*'janet'*No rule is relabelled*"
        }

        It 'relabels when razorgraph is toggled off in /mcp, which ~\.claude.json records per project' {
            $project = New-GraphProject 'RazorToggled'
            $claudeJson = New-ClaudeJson $project ([ordered]@{ disabledMcpServers = @('razorgraph') })
            $brief = Invoke-WithUserProfile (New-RuleManifest $script:graphRule -McpDependents $script:mcpMap) $project -ClaudeJsonText $claudeJson

            $brief.rules | Should -Be @('ADVISORY (claims ENFORCED; MCP server razorgraph disabled here): fixture graph-first rule.')
            @($brief.enforcement).Count | Should -Be 1
            $brief.enforcement[0] | Should -BeLike "*'razorgraph'*disabledMcpServers for this project in *.claude.json*"
            # The file holds credentials; nothing from it but the path may reach the brief.
            ($brief | ConvertTo-Json -Depth 8) | Should -Not -BeLike '*fixture*oauth*'
            ($brief | ConvertTo-Json -Depth 8) | Should -Not -BeLike '*oauthAccount*'
        }

        It 'relabels when a declined .mcp.json approval is recorded in ~\.claude.json' {
            $project = New-GraphProject 'RazorDeclined'
            $claudeJson = New-ClaudeJson $project ([ordered]@{ disabledMcpjsonServers = @('razorgraph'); enabledMcpjsonServers = @('janet') })
            $brief = Invoke-WithUserProfile (New-RuleManifest $script:graphRule -McpDependents $script:mcpMap) $project -ClaudeJsonText $claudeJson

            $brief.rules[0] | Should -BeLike 'ADVISORY (claims ENFORCED; MCP server razorgraph disabled here)*'
            $brief.enforcement[0] | Should -BeLike "*disabledMcpjsonServers for this project in *.claude.json*"
        }

        It "ignores another project's entry in ~\.claude.json" {
            $project = New-GraphProject 'RazorElsewhere'
            $claudeJson = New-ClaudeJson (Join-Path $TestDrive 'SomeOtherProject') ([ordered]@{ disabledMcpServers = @('razorgraph') })
            $brief = Invoke-WithUserProfile (New-RuleManifest $script:graphRule -McpDependents $script:mcpMap) $project -ClaudeJsonText $claudeJson

            $brief.rules | Should -Be @('ENFORCED: fixture graph-first rule.')
            $brief.enforcement | Should -BeNullOrEmpty
        }

        It "reads the server-to-rule map from the manifest: without it nothing is relabelled" {
            $project = New-GraphProject 'RazorUnmapped' -LocalSettingsText '{ "disabledMcpjsonServers": ["razorgraph"] }'
            $brief = Invoke-WithUserProfile (New-RuleManifest $script:graphRule) $project

            $brief.rules | Should -Be @('ENFORCED: fixture graph-first rule.')
        }

        It "refuses a map entry that no rule's enforcedBy carries" {
            $project = New-GraphProject 'RazorDrift'
            $map = [ordered]@{ razorgraph = @('scripts\Invoke-GraphGuard.ps1', 'scripts\Renamed-Guard.ps1') }

            { Invoke-WithUserProfile (New-RuleManifest $script:graphRule -McpDependents $map) $project } |
                Should -Throw -ExpectedMessage "*mcpDependents: 'razorgraph' names 'Renamed-Guard.ps1', which no rule's enforcedBy carries*"
        }
    }
}
