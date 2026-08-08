#Requires -Version 7.0
<#
.SYNOPSIS
    Configure Windows Error Reporting to capture a crash dump for an executable.

.DESCRIPTION
    Writes the per-executable WER LocalDumps key, so the next unhandled crash leaves
    a dump on disk instead of only an Application-log entry.

    This exists because a native access violation is not a managed exception. It
    bypasses DispatcherUnhandledException, TaskScheduler.UnobservedTaskException and
    AppDomain.CurrentDomain.UnhandledException alike -- the process fails fast, so a
    crash log written from those handlers stays empty. An empty log is the signature
    of any native fault and says nothing about the cause. A dump is the only thing
    that names the faulting frame.

    Requires elevation to set or remove: LocalDumps lives under HKLM. Listing does
    not. WER reads the key at fault time, so nothing needs restarting.

    Full dumps are large -- a UI process with native rendering and a big working set
    can produce hundreds of megabytes each, so DumpCount defaults to 3 rather than
    the usual 10. Remove the key once the diagnosis is done.

.PARAMETER Executable
    The crashing program: 'MyApp.exe', or a full path (the leaf is taken). An .exe
    extension is added when missing, because that is what WER matches on.

.PARAMETER DumpFolder
    Where dumps are written. Stored as REG_EXPAND_SZ, so %LOCALAPPDATA% and friends
    expand per-user at fault time -- pass them unexpanded. Defaults to WER's own
    %LOCALAPPDATA%\CrashDumps; point it beside an existing crash log when there is one,
    so both artifacts land together.

.PARAMETER DumpCount
    How many dumps to keep before WER starts discarding. Default 3.

.PARAMETER DumpType
    Mini captures stacks and loaded modules -- enough to name a faulting module, and
    small. Full adds process memory, which is what a use-after-free needs, because the
    question is what the memory at the faulting address used to be. Custom honours a
    CustomDumpFlags value this script does not write. Default Full.

.PARAMETER Remove
    Delete the key for this executable, restoring default WER behaviour.

.PARAMETER List
    Report every executable currently configured. Needs no elevation.

.PARAMETER DryRun
    Report what would change without writing.

.PARAMETER Text
    Human-readable output instead of JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Set-CrashDumpCapture.ps1" MyApp.exe -Text

.EXAMPLE
    & "$env:JanetBase\scripts\Set-CrashDumpCapture.ps1" MyApp.exe `
        -DumpFolder '%LOCALAPPDATA%\MyApp\Dumps' -DumpCount 5

.EXAMPLE
    & "$env:JanetBase\scripts\Set-CrashDumpCapture.ps1" -List -Text

.EXAMPLE
    & "$env:JanetBase\scripts\Set-CrashDumpCapture.ps1" MyApp.exe -Remove
#>
[CmdletBinding(DefaultParameterSetName = 'Set')]
param(
    [Parameter(Mandatory, Position = 0, ParameterSetName = 'Set')]
    [Parameter(Mandatory, Position = 0, ParameterSetName = 'Remove')]
    [string]$Executable,

    [Parameter(ParameterSetName = 'Set')]
    [string]$DumpFolder = '%LOCALAPPDATA%\CrashDumps',

    [Parameter(ParameterSetName = 'Set')]
    [ValidateRange(1, 1000)]
    [int]$DumpCount = 3,

    [Parameter(ParameterSetName = 'Set')]
    [ValidateSet('Mini', 'Full', 'Custom')]
    [string]$DumpType = 'Full',

    [Parameter(Mandatory, ParameterSetName = 'Remove')]
    [switch]$Remove,

    [Parameter(Mandatory, ParameterSetName = 'List')]
    [switch]$List,

    [switch]$DryRun,
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$localDumpsRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps'

# WER's numeric encoding. Named on the way in because 0/1/2 at a call site is the
# kind of thing that gets copied wrong and produces a dump missing what you needed.
$dumpTypeValues = @{ Custom = 0; Mini = 1; Full = 2 }

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Reads one value, or $null when absent. Registry properties are surfaced on a
# PSCustomObject, so an absent value is a missing property -- terminating under
# StrictMode (house rule 2) rather than simply null.
function Get-RegistryValue {
    param([string]$Path, [string]$Name)

    if (-not (Test-Path $Path)) { return $null }
    $item = Get-ItemProperty -Path $Path
    if ($item.PSObject.Properties.Name -notcontains $Name) { return $null }
    return $item.$Name
}

function Read-DumpConfig {
    param([string]$Name)

    $path = Join-Path $localDumpsRoot $Name
    return [PSCustomObject]@{
        executable = $Name
        dumpFolder = Get-RegistryValue $path 'DumpFolder'
        dumpCount  = Get-RegistryValue $path 'DumpCount'
        dumpType   = Get-RegistryValue $path 'DumpType'
    }
}

# ---- List ------------------------------------------------------------------

if ($List) {
    $configured = @()
    if (Test-Path $localDumpsRoot) {
        $configured = @(Get-ChildItem $localDumpsRoot |
            ForEach-Object { Read-DumpConfig $_.PSChildName })
    }

    if ($Text) {
        if ($configured.Count -eq 0) {
            Write-Host 'No per-executable dump capture configured.'
        }
        foreach ($entry in $configured) {
            Write-Host $entry.executable -ForegroundColor Cyan
            Write-Host "  folder: $($entry.dumpFolder)"
            Write-Host "  count:  $($entry.dumpCount)   type: $($entry.dumpType)"
        }
        return
    }

    [PSCustomObject]@{
        action     = 'list'
        rootExists = (Test-Path $localDumpsRoot)
        configured = $configured
    } | ConvertTo-Json -Depth 4 -Compress
    return
}

# ---- Resolve the target ----------------------------------------------------

# Accept a full path so callers can pass whatever they just built; WER matches on
# the file name alone.
$exeName = Split-Path $Executable -Leaf
if (-not $exeName.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
    $exeName = "$exeName.exe"
}

$keyPath = Join-Path $localDumpsRoot $exeName
$before = Read-DumpConfig $exeName
$exists = Test-Path $keyPath

# Elevation is checked before anything is attempted: the raw failure is an
# access-denied on a registry path, which reads as a missing key rather than a
# missing privilege.
if (-not $DryRun -and -not (Test-Elevated)) {
    throw "Setting or removing a LocalDumps key needs an elevated shell. " +
        "Re-run this script from PowerShell started as administrator."
}

# ---- Remove ----------------------------------------------------------------

if ($Remove) {
    if ($DryRun) {
        $removeResult = [PSCustomObject]@{
            action = 'remove'; dryRun = $true; executable = $exeName
            existed = $exists; before = $before
        }
    }
    else {
        if ($exists) { Remove-Item -Path $keyPath -Recurse -Force }
        $removeResult = [PSCustomObject]@{
            action = 'remove'; dryRun = $false; executable = $exeName
            existed = $exists; before = $before
        }
    }

    if ($Text) {
        $verb = if ($DryRun) { 'Would remove' } else { 'Removed' }
        if ($exists) { Write-Host "$verb dump capture for $exeName" -ForegroundColor Green }
        else { Write-Host "No dump capture configured for $exeName" }
        return
    }

    $removeResult | ConvertTo-Json -Depth 4 -Compress
    return
}

# ---- Set -------------------------------------------------------------------

$typeValue = $dumpTypeValues[$DumpType]

if ($DryRun) {
    $plan = [PSCustomObject]@{
        action     = 'set'
        dryRun     = $true
        executable = $exeName
        keyPath    = $keyPath
        before     = $before
        after      = [PSCustomObject]@{
            executable = $exeName; dumpFolder = $DumpFolder
            dumpCount = $DumpCount; dumpType = $typeValue
        }
        elevated   = (Test-Elevated)
    }

    if ($Text) {
        Write-Host "Would configure $exeName" -ForegroundColor Cyan
        Write-Host "  folder: $DumpFolder"
        Write-Host "  count:  $DumpCount   type: $DumpType ($typeValue)"
        if (-not $plan.elevated) {
            Write-Host '  ! needs an elevated shell to apply' -ForegroundColor Yellow
        }
        return
    }

    $plan | ConvertTo-Json -Depth 4 -Compress
    return
}

New-Item -Path $keyPath -Force | Out-Null
New-ItemProperty -Path $keyPath -Name 'DumpFolder' -PropertyType ExpandString `
    -Value $DumpFolder -Force | Out-Null
New-ItemProperty -Path $keyPath -Name 'DumpCount' -PropertyType DWord `
    -Value $DumpCount -Force | Out-Null
New-ItemProperty -Path $keyPath -Name 'DumpType' -PropertyType DWord `
    -Value $typeValue -Force | Out-Null

# Read back rather than trusting the write: a silently absent value here means no
# dump at the moment one is needed, which is exactly when nobody is checking.
$after = Read-DumpConfig $exeName
$verified = $after.dumpFolder -eq $DumpFolder -and
    $after.dumpCount -eq $DumpCount -and
    $after.dumpType -eq $typeValue

if (-not $verified) {
    throw "Wrote $keyPath but read back $($after | ConvertTo-Json -Compress)"
}

if ($Text) {
    Write-Host "Configured dump capture for $exeName" -ForegroundColor Green
    Write-Host "  folder: $DumpFolder"
    Write-Host "  count:  $DumpCount   type: $DumpType ($typeValue)"
    Write-Host '  WER reads this at fault time -- no restart needed.' -ForegroundColor DarkGray
    if ($DumpType -eq 'Full') {
        Write-Host '  Full dumps are large; remove the key when done.' -ForegroundColor DarkGray
    }
    return
}

[PSCustomObject]@{
    action     = 'set'
    dryRun     = $false
    executable = $exeName
    keyPath    = $keyPath
    before     = $before
    after      = $after
    verified   = $verified
} | ConvertTo-Json -Depth 4 -Compress
