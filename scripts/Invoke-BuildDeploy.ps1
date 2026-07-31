<#
.SYNOPSIS
    Manifest-driven build-and-deploy orchestrator.  The pipeline shape lives here;
    everything project-specific lives in the project's deploy-manifest.json.

.DESCRIPTION
    The deploy sibling of the manifest-startup pattern (DESIGN-NOTES section 1):
    an explicit checkable contract instead of a per-project script that gets
    copy-pasted and drifts.  A new project writes a manifest, not a pipeline.

    The orchestrator owns the transferable structure:
      - validate everything before executing anything (all problems at once)
      - resolve tools defensively (PATH first, well-known fallbacks second)
      - compute a build-identity stamp from git before building
      - run build -> package -> push with an exit-code gate after each
      - apply the stamp only after a successful push, so a failed deploy
        never lies about what is running
      - activate, then verify against the LIVE system, distinguishing
        "warming up" from "broken"
      - print the promote command, never run it

    The manifest owns the nouns: build/package command lines, the push target,
    the stamp sink, the health URL, the promote hint.

    Manifest shape (all sections except 'stages.push' optional):
      {
        "name": "MyApp",
        "identity": {
          "source": "git",
          "sink": { "type": "azureAppSetting", "setting": "LATEST_BUILD_INFO" }
        },
        "stages": {
          "build":   { "run": "dotnet build MyApp.slnx -c Release" },
          "package": { "run": "dotnet publish MyApp\\MyApp.csproj -c Release -o {packageDir}", "zip": true },
          "push":    { "type": "azureWebAppZip", "app": "MyApp", "resourceGroup": "MyGroup", "slot": "staging" }
        },
        "verify":  { "url": "https://myapp-staging.azurewebsites.net", "warmingStatuses": [503],
                     "attempts": 12, "delaySeconds": 5, "initialDelaySeconds": 10 },
        "promote": { "hint": "az webapp deployment slot swap ..." }
      }
    'slot' is optional (omit to target the production slot).  '{packageDir}' in a
    command is replaced with the orchestrator's package directory.

.PARAMETER ManifestPath
    Path to the project's deploy manifest.  The manifest's directory is the
    working directory for every stage command.

.PARAMETER Validate
    Resolve the manifest, the tools, and the build identity, report, and stop
    before executing any stage.  Lints the contract the way -SkipRun lints the
    startup manifest.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-BuildDeploy.ps1" -ManifestPath D:\Repos\MyApp\deploy-manifest.json

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-BuildDeploy.ps1" -ManifestPath .\deploy-manifest.json -Validate
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ManifestPath,
    [switch]$Validate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Prop {
    # StrictMode-safe optional property read (house rule 2). Uses the indexer,
    # not `.Properties.Name -contains`: member enumeration on an EMPTY property
    # collection (a `{}` in the JSON) itself throws under StrictMode.
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -ne $prop -and $null -ne $prop.Value) { return $prop.Value }
    return $Default
}

function Resolve-Tool {
    # PATH first, then well-known fallbacks. Returns $null when nothing resolves;
    # the caller decides whether that is a problem.
    param([string]$Name, [string[]]$Fallbacks)
    $found = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $found) { return $found.Source }
    foreach ($candidate in $Fallbacks) {
        foreach ($hit in @(Get-Item $candidate -ErrorAction SilentlyContinue)) {
            return $hit.FullName
        }
    }
    return $null
}

# ---- Load manifest ---------------------------------------------------------

if (-not (Test-Path $ManifestPath -PathType Leaf)) {
    throw "Deploy manifest not found: $ManifestPath"
}
$manifestFull = (Resolve-Path $ManifestPath).Path
$repoRoot = Split-Path $manifestFull -Parent

try {
    $manifest = Get-Content $manifestFull -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "Deploy manifest is not valid JSON ($manifestFull): $($_.Exception.Message)"
}

$appName    = Get-Prop $manifest 'name' (Split-Path $repoRoot -Leaf)
$identity   = Get-Prop $manifest 'identity'
$stages     = Get-Prop $manifest 'stages'
$buildStage = Get-Prop $stages 'build'
$package    = Get-Prop $stages 'package'
$push       = Get-Prop $stages 'push'
$verify     = Get-Prop $manifest 'verify'
$promote    = Get-Prop $manifest 'promote'

# ---- Validation pass: every problem, all at once ---------------------------

$problems = @()

if ($null -eq $push) { $problems += "stages.push is required" }
$pushType = Get-Prop $push 'type'
$slot = Get-Prop $push 'slot'
switch ($pushType) {
    'azureWebAppZip' {
        foreach ($field in @('app', 'resourceGroup')) {
            if ($null -eq (Get-Prop $push $field)) { $problems += "stages.push: missing '$field'" }
        }
        if ($null -eq $package) { $problems += "stages.package is required for azureWebAppZip push" }
    }
    $null   { if ($null -ne $push) { $problems += "stages.push: missing 'type'" } }
    default { $problems += "stages.push: unknown type '$pushType' (known: azureWebAppZip)" }
}

if ($null -ne $buildStage -and $null -eq (Get-Prop $buildStage 'run')) { $problems += "stages.build: missing 'run'" }
if ($null -ne $package -and $null -eq (Get-Prop $package 'run')) { $problems += "stages.package: missing 'run'" }

$sink = Get-Prop $identity 'sink'
$sinkType = Get-Prop $sink 'type'
if ($null -ne $sink) {
    if ($sinkType -ne 'azureAppSetting') { $problems += "identity.sink: unknown type '$sinkType' (known: azureAppSetting)" }
    if ($null -eq (Get-Prop $sink 'setting')) { $problems += "identity.sink: missing 'setting'" }
}

$verifyUrl = Get-Prop $verify 'url'
if ($null -ne $verify -and $null -eq $verifyUrl) { $problems += "verify: missing 'url'" }

# Tool resolution is part of validation: a missing tool should fail here, not
# four minutes into a build.
$gitExe = $null
if ((Get-Prop $identity 'source') -eq 'git') {
    $gitExe = Resolve-Tool -Name 'git' -Fallbacks @(
        "$env:ProgramFiles\Git\cmd\git.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe"
    )
    if ($null -eq $gitExe) {
        Write-Warning "identity.source is 'git' but git was not found; deploying without a build stamp"
    }
}

$azExe = $null
if ($pushType -eq 'azureWebAppZip' -or $sinkType -eq 'azureAppSetting') {
    $azExe = Resolve-Tool -Name 'az' -Fallbacks @(
        "$env:ProgramFiles\Microsoft SDKs\Azure\CLI2\wbin\az.cmd",
        "${env:ProgramFiles(x86)}\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"
    )
    if ($null -eq $azExe) { $problems += "Azure CLI (az) not found on PATH or in default install locations" }
}

if ($problems.Count -gt 0) {
    $detail = ($problems | ForEach-Object { "  - $_" }) -join [Environment]::NewLine
    throw "Deploy manifest has $($problems.Count) problem$(if ($problems.Count -ne 1) {'s'}):$([Environment]::NewLine)$detail"
}

# ---- Build identity --------------------------------------------------------

$buildInfo = $null
if ($null -ne $gitExe) {
    Push-Location $repoRoot
    try {
        $commit = (& $gitExe rev-parse --short=12 HEAD).Trim()
        $branch = (& $gitExe branch --show-current).Trim()
        $count  = (& $gitExe rev-list --count HEAD).Trim()
        $dirtyOutput = @(& $gitExe status --porcelain)
        $dirty = if ($dirtyOutput.Count -gt 0) { '+dirty' } else { '' }
        $utcNow = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        $buildInfo = "Build $count (local) | Commit $commit$dirty | Branch $branch | $utcNow"
    }
    finally { Pop-Location }
}

if ($Validate) {
    Write-Host "Manifest OK: $manifestFull" -ForegroundColor Green
    Write-Host "  app:     $appName"
    Write-Host "  push:    $pushType -> $(Get-Prop $push 'app') $(if ($null -ne $slot) { "(slot $slot)" } else { '(production slot)' })"
    Write-Host "  git:     $(if ($null -ne $gitExe) { $gitExe } else { '(not found - no stamp)' })"
    Write-Host "  az:      $(if ($null -ne $azExe) { $azExe } else { '(not required)' })"
    Write-Host "  stamp:   $(if ($null -ne $buildInfo) { $buildInfo } else { '(none)' })"
    Write-Host "  verify:  $(if ($null -ne $verifyUrl) { $verifyUrl } else { '(none)' })"
    return
}

# ---- Execution -------------------------------------------------------------

function Invoke-Stage {
    # Runs a manifest command line in the repo root and gates on its exit code.
    param([string]$Title, [string]$CommandLine)
    Write-Host "`n=== $Title ===" -ForegroundColor Cyan
    Push-Location $repoRoot
    try {
        & ([scriptblock]::Create($CommandLine))
        if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit $LASTEXITCODE): $CommandLine" }
    }
    finally { Pop-Location }
}

$packageDir = Join-Path $repoRoot '.deploy-package'
$zipPath = Join-Path $repoRoot 'deploy.zip'
$slotArgs = @()
if ($null -ne $slot) { $slotArgs = @('--slot', $slot) }

try {
    if ($null -ne $buildStage) {
        Invoke-Stage -Title 'Build' -CommandLine (Get-Prop $buildStage 'run')
    }

    $packageRun = (Get-Prop $package 'run').Replace('{packageDir}', $packageDir)
    Invoke-Stage -Title 'Package' -CommandLine $packageRun

    if (Get-Prop $package 'zip' $false) {
        Write-Host "`n=== Zip ===" -ForegroundColor Cyan
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Compress-Archive -Path "$packageDir\*" -DestinationPath $zipPath -Force
        $sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
        Write-Host "Created $zipPath ($sizeMB MB)"
    }

    Write-Host "`n=== Push ($pushType) ===" -ForegroundColor Cyan
    $pushApp = Get-Prop $push 'app'
    $pushGroup = Get-Prop $push 'resourceGroup'
    # Classic zipdeploy, NOT 'az webapp deploy' (OneDeploy): OneDeploy has been
    # observed reporting 'Clean deploying to wwwroot' + success in ~1s without
    # writing any files, silently serving stale builds. Zipdeploy extracts for
    # real and its duration scales with the payload -- trust the engine whose
    # work is observable.
    & $azExe webapp deployment source config-zip --name $pushApp --resource-group $pushGroup --src $zipPath @slotArgs
    if ($LASTEXITCODE -ne 0) { throw "Push failed (exit $LASTEXITCODE)" }

    if ($null -ne $sink -and $null -ne $buildInfo) {
        Write-Host "`n=== Stamp ===" -ForegroundColor Cyan
        $setting = Get-Prop $sink 'setting'
        & $azExe webapp config appsettings set --name $pushApp --resource-group $pushGroup --settings "$setting=$buildInfo" --output none @slotArgs
        if ($LASTEXITCODE -ne 0) { throw "Stamp failed: could not set $setting" }
        Write-Host $buildInfo
    }
    elseif ($null -ne $sink) {
        Write-Host "`n=== Stamp ===" -ForegroundColor Cyan
        Write-Host 'git not found; leaving existing stamp in place' -ForegroundColor Yellow
    }

    Write-Host "`n=== Activate ===" -ForegroundColor Cyan
    & $azExe webapp restart --name $pushApp --resource-group $pushGroup @slotArgs
    if ($LASTEXITCODE -ne 0) { throw 'Restart failed' }

    if ($null -ne $verifyUrl) {
        Write-Host "`n=== Verify ===" -ForegroundColor Cyan
        $attempts = Get-Prop $verify 'attempts' 12
        $delay = Get-Prop $verify 'delaySeconds' 5
        $warming = @(Get-Prop $verify 'warmingStatuses' @(503))
        $healthy = $false
        Start-Sleep -Seconds (Get-Prop $verify 'initialDelaySeconds' 10)
        for ($attempt = 1; $attempt -le $attempts -and -not $healthy; $attempt++) {
            try {
                $response = Invoke-WebRequest -Uri $verifyUrl -UseBasicParsing -TimeoutSec 30
                if ($response.StatusCode -eq 200) {
                    Write-Host "`nHealthy (HTTP 200) after $attempt attempt(s)" -ForegroundColor Green
                    $healthy = $true
                }
            }
            catch {
                $status = $_.Exception.Response.StatusCode.value__
                if ($warming -contains $status) {
                    Write-Host "  Attempt ${attempt}/${attempts}: warming up ($status)..." -ForegroundColor Yellow
                }
                else {
                    Write-Host "  Attempt ${attempt}/${attempts}: HTTP $status" -ForegroundColor Yellow
                }
            }
            if (-not $healthy) { Start-Sleep -Seconds $delay }
        }
        if (-not $healthy) {
            # Not a deployment failure: everything landed and the stamp is applied.
            # The app just did not answer within the polling budget -- slow warmups
            # look exactly like this. Exit 2 so callers can tell the states apart.
            Write-Host "`n=== DEPLOYED, NOT VERIFIED ===" -ForegroundColor Yellow
            Write-Host "The artifact landed and the site was restarted, but $verifyUrl did not return 200 within $attempts attempts." -ForegroundColor Yellow
            Write-Host 'Check the URL yourself before assuming failure -- a slow warmup outlives this poll. If it is genuinely down, check the app logs.' -ForegroundColor Yellow
            exit 2
        }
    }

    Write-Host "`n=== Deployment complete ===" -ForegroundColor Green
    if ($null -ne $verifyUrl) { Write-Host "Live at: $verifyUrl" }
    $promoteHint = Get-Prop $promote 'hint'
    if ($null -ne $promoteHint) {
        Write-Host ''
        Write-Host 'To promote:' -ForegroundColor Yellow
        Write-Host "  $promoteHint"
    }
}
catch {
    Write-Host "`n=== DEPLOYMENT FAILED ===" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force -ErrorAction SilentlyContinue }
}
