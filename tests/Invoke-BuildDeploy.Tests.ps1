#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for the verify probe in scripts\Invoke-BuildDeploy.ps1 -- a probe
    that gets no HTTP response is warming, not a failed deploy.

.DESCRIPTION
    The script is one top-level pipeline that deploys when it runs, so it cannot
    be dot-sourced. These tests lift the verify loop's own try/catch out of the
    script's AST, together with Get-Prop, and run that text under StrictMode
    against real requests: a refused connection, and a local HttpListener that
    answers with the status named in the path. They run the script's text, not a
    copy of it, so an edit to the catch is what they test.

    The load-bearing case is 'refused'. Until 2026-10-17 the catch read
    $_.Exception.Response bare, and an exception with no Response (a timeout, or
    a connection refused while the site restarts) threw "The property 'Response'
    cannot be found on this object" out of the catch. That ended the loop, and a
    deploy that had landed was reported as DEPLOYMENT FAILED. It happened to the
    Neelam test deploys of 2026-10-03 and 2026-10-09.
#>

BeforeAll {
    Set-StrictMode -Version Latest
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $scriptPath = Join-Path $script:repoRoot 'scripts\Invoke-BuildDeploy.ps1'
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$null)

    $getProp = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-Prop'
        }, $true)
    . ([scriptblock]::Create($getProp.Extent.Text))

    # The innermost try whose body makes the probe request.
    $tries = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.TryStatementAst] -and
            $node.Body.Extent.Text -match 'Invoke-WebRequest -Uri \$verifyUrl'
        }, $true)
    $script:probe = [scriptblock]::Create(($tries | Sort-Object { $_.Extent.Text.Length } | Select-Object -First 1).Extent.Text)

    $script:port = Get-Random -Minimum 20000 -Maximum 40000
    $script:listener = [System.Net.HttpListener]::new()
    $script:listener.Prefixes.Add("http://127.0.0.1:$script:port/")
    $script:listener.Start()
    $script:server = [powershell]::Create()
    [void]$script:server.AddScript({
            param($listening)
            while ($listening.IsListening) {
                try { $context = $listening.GetContext() } catch { break }
                $context.Response.StatusCode = [int]$context.Request.Url.AbsolutePath.Trim('/')
                $context.Response.Close()
            }
        }).AddArgument($script:listener)
    $script:serverHandle = $script:server.BeginInvoke()

    function Invoke-Probe {
        # Runs the lifted try/catch in this scope with the loop's variables set,
        # and returns what it wrote plus whether it marked the site healthy.
        param([string]$Url)
        $verifyUrl = $Url
        $attempt = 1
        $attempts = 12
        $warming = @(503)
        $healthy = $false
        $output = . $script:probe 6>&1 | Out-String
        [pscustomobject]@{ Output = $output.Trim(); Healthy = $healthy }
    }
}

AfterAll {
    $script:listener.Stop()
    $script:listener.Close()
    [void]$script:server.EndInvoke($script:serverHandle)
    $script:server.Dispose()
}

Describe 'Invoke-BuildDeploy verify probe' {
    It 'treats a refused connection as no response yet, without throwing' {
        $result = Invoke-Probe -Url 'http://127.0.0.1:1/'
        $result.Output | Should -Match 'no response yet'
        $result.Healthy | Should -BeFalse
    }

    It 'reports a warming status as warming up' {
        $result = Invoke-Probe -Url "http://127.0.0.1:$script:port/503"
        $result.Output | Should -Match 'warming up \(503\)'
        $result.Healthy | Should -BeFalse
    }

    It 'reports any other status by number' {
        $result = Invoke-Probe -Url "http://127.0.0.1:$script:port/500"
        $result.Output | Should -Match 'HTTP 500'
        $result.Healthy | Should -BeFalse
    }

    It 'marks a 200 healthy' {
        $result = Invoke-Probe -Url "http://127.0.0.1:$script:port/200"
        $result.Output | Should -Match 'Healthy \(HTTP 200\)'
        $result.Healthy | Should -BeTrue
    }
}

<#
    The clean-tree gate. These run the whole script as a child process against a
    throwaway git repository under %TEMP%, and NOTHING IN THEM CAN DEPLOY: an
    az.cmd stub that only exits 1 sits first on PATH, the manifest names an app
    and group that do not exist, and the build and package stages are
    'cmd /c exit 7', so a run that got past validation would fail at Build
    before any push. Every case but one uses -Validate; the one that does not is
    the proof that the deploy path refuses too, and it asserts no stage started.
#>
Describe 'Invoke-BuildDeploy clean-tree gate' {
    BeforeAll {
        $script:deployScript = Join-Path $script:repoRoot 'scripts\Invoke-BuildDeploy.ps1'
        $script:gateRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("build-deploy-gate-" + [guid]::NewGuid().ToString('N'))
        $bin = Join-Path $script:gateRoot 'bin'
        New-Item -ItemType Directory -Path $bin -Force | Out-Null
        Set-Content -Path (Join-Path $bin 'az.cmd') -Value "@echo az stub: never a real deploy 1>&2`r`n@exit /b 1" -Encoding ascii
        $script:savedPath = $env:PATH
        $env:PATH = "$bin;$env:PATH"

        # Under the pre-commit gate these run inside a git hook, which exports
        # GIT_INDEX_FILE (and may export GIT_DIR) for the commit in progress. A
        # fixture 'git add' would write into THAT index -- JanetHome's -- so every
        # GIT_* variable is lifted for the duration and put back afterwards.
        $script:savedGit = @(Get-ChildItem Env: | Where-Object { $_.Name -like 'GIT_*' } |
                ForEach-Object { [pscustomobject]@{ Name = $_.Name; Value = $_.Value } })
        foreach ($variable in $script:savedGit) { Remove-Item "Env:$($variable.Name)" }

        function New-DeployRepo {
            # A committed repo holding a manifest and one tracked file.
            param([string]$Name)
            $repo = Join-Path $script:gateRoot $Name
            New-Item -ItemType Directory -Path $repo -Force | Out-Null
            git -C $repo init -q
            git -C $repo config user.email 'fixture@example.invalid'
            git -C $repo config user.name 'Fixture'
            git -C $repo config commit.gpgsign false
            [pscustomobject]@{
                name     = 'GateFixture'
                identity = [pscustomobject]@{ source = 'git' }
                stages   = [pscustomobject]@{
                    build   = [pscustomobject]@{ run = 'cmd /c exit 7' }
                    package = [pscustomobject]@{ run = 'cmd /c exit 7' }
                    push    = [pscustomobject]@{ type = 'azureWebAppZip'; app = 'never-a-real-app'; resourceGroup = 'never-a-real-group' }
                }
            } | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $repo 'deploy-manifest.json') -Encoding UTF8
            Set-Content -Path (Join-Path $repo 'tracked.txt') -Value 'committed' -Encoding UTF8
            git -C $repo add -- deploy-manifest.json tracked.txt
            git -C $repo commit -q -m 'fixture'
            return $repo
        }

        function Invoke-Deploy {
            param([string]$Repo, [string[]]$Extra = @())
            $manifest = Join-Path $Repo 'deploy-manifest.json'
            $output = pwsh -NoProfile -File $script:deployScript -ManifestPath $manifest @Extra *>&1 | Out-String
            [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
        }
    }

    AfterAll {
        $env:PATH = $script:savedPath
        foreach ($variable in $script:savedGit) { Set-Item "Env:$($variable.Name)" $variable.Value }
        Remove-Item -Recurse -Force $script:gateRoot -ErrorAction SilentlyContinue
    }

    It 'accepts a clean tree, ignoring its own deploy.zip, and stamps it without +dirty' {
        $repo = New-DeployRepo 'clean'
        Set-Content -Path (Join-Path $repo 'deploy.zip') -Value 'left by an earlier run' -Encoding UTF8
        $result = Invoke-Deploy $repo @('-Validate')
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Match 'Manifest OK'
        $result.Output | Should -Match ([regex]::Escape((Join-Path $script:gateRoot 'bin\az.cmd'))) -Because 'the az stub, never the real CLI, must be what resolved'
        $result.Output | Should -Match 'tree:\s+clean'
        $result.Output | Should -Match 'Commit [0-9a-f]{12} \|'
    }

    It 'refuses a modified tracked file under -Validate, naming it' {
        $repo = New-DeployRepo 'modified'
        Add-Content -Path (Join-Path $repo 'tracked.txt') -Value 'uncommitted'
        $result = Invoke-Deploy $repo @('-Validate')
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'working tree is not clean'
        $result.Output | Should -Match 'M tracked\.txt'
        $result.Output | Should -Not -Match 'Manifest OK'
    }

    It 'refuses a dirty tree on the deploy path before any stage starts' {
        $repo = New-DeployRepo 'deploy-path'
        Add-Content -Path (Join-Path $repo 'tracked.txt') -Value 'uncommitted'
        $result = Invoke-Deploy $repo
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'working tree is not clean'
        $result.Output | Should -Not -Match '=== Build ==='
    }

    It 'counts an untracked file that .gitignore does not cover' {
        $repo = New-DeployRepo 'untracked'
        Set-Content -Path (Join-Path $repo 'NewController.cs') -Value 'class NewController {}' -Encoding UTF8
        $result = Invoke-Deploy $repo @('-Validate')
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match '\?\? NewController\.cs'
    }

    It 'goes ahead under -AllowDirty, stamping +dirty and naming the dirty files' {
        $repo = New-DeployRepo 'allow-dirty'
        Add-Content -Path (Join-Path $repo 'tracked.txt') -Value 'uncommitted'
        $result = Invoke-Deploy $repo @('-Validate', '-AllowDirty')
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $result.Output | Should -Match '-AllowDirty: deploying a working tree with 1 uncommitted path'
        $result.Output | Should -Match 'M tracked\.txt'
        $result.Output | Should -Match 'Commit [0-9a-f]{12}\+dirty'
    }
}
