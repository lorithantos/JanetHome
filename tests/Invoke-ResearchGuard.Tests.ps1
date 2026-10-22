#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\Invoke-ResearchGuard.ps1 -- the catalog window: a trace
    written N minutes ago is inside or outside it, whatever the machine's time zone.

.DESCRIPTION
    Written 2026-10-21 for the bug that made the guard almost never fire. Under pwsh 7
    ConvertFrom-Json hands the trace's lastUtc back as a DateTime of Kind Utc, and the
    guard parsed it AGAIN; that went through the zone-less string form, came back Local,
    and ToUniversalTime added the offset. In PDT a trace read seven hours in the future,
    so a 60-minute window held for about eight hours and -WindowMinutes 0 never denied.

    The guard reads its trace from the temp directory, so each case runs it in a child
    process whose TMP and TEMP point into TestDrive, and writes the trace there with a
    timestamp computed from now. The child gets its payload on stdin, as the real hook
    does. Each case runs under pwsh, and again under Windows PowerShell 5.1 when it is
    installed, because the hook's "shell": "powershell" may resolve to either -- and 5.1's
    ConvertFrom-Json leaves lastUtc a string, which is the other branch of the fix.

    Limitation: the stretch IS the local UTC offset, so on a machine whose zone is UTC
    the outside-the-window cases pass against the old code too. They prove the bug only
    off UTC; this machine is Pacific time, where they failed on the pre-fix guard.

    A deny is a JSON envelope carrying permissionDecision 'deny'; a pass is silence. The
    script fails open on every error, so exit 0 alone would pass a crashed guard.
#>

BeforeAll {
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:guard = Join-Path $script:repoRoot 'scripts\Invoke-ResearchGuard.ps1'

    # A NEW .ps1 directly inside a folder named scripts -- the one shape the guard
    # fires on. The folder exists; the file does not.
    $script:scriptsDir = Join-Path $TestDrive 'repo\scripts'
    New-Item -ItemType Directory -Path $script:scriptsDir -Force | Out-Null
    $script:payload = @{
        tool_name  = 'Write'
        tool_input = @{ file_path = (Join-Path $script:scriptsDir 'Get-Nothing.ps1') }
    } | ConvertTo-Json -Compress

    function Invoke-GuardAt {
        <#
            Runs the guard in a child process whose temp directory holds a trace written
            -AgeMinutes ago (or no trace at all), and returns what it printed.
        #>
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][string]$HostExe,
            [Nullable[double]]$AgeMinutes,
            [int]$WindowMinutes = 60
        )

        $temp = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temp | Out-Null
        if ($null -ne $AgeMinutes) {
            # The shape Janet.Core.ResearchTrace writes: round-trip UTC text ending in Z.
            $lastUtc = [datetime]::UtcNow.AddMinutes(-$AgeMinutes).ToString('o')
            @{ lastUtc = $lastUtc; recent = @() } | ConvertTo-Json |
                Set-Content -LiteralPath (Join-Path $temp 'janet-research-trace.json') -Encoding utf8
        }

        $savedTmp = $env:TMP
        $savedTemp = $env:TEMP
        try {
            $env:TMP = $temp
            $env:TEMP = $temp
            $out = $script:payload | & $HostExe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $script:guard -WindowMinutes $WindowMinutes
        }
        finally {
            $env:TMP = $savedTmp
            $env:TEMP = $savedTemp
        }
        return ($out -join "`n")
    }

    function Test-Denied {
        param([string]$Output)
        if (-not $Output) { return $false }
        return (($Output | ConvertFrom-Json).hookSpecificOutput.permissionDecision -eq 'deny')
    }
}

Describe 'Invoke-ResearchGuard catalog window under <Name>' -ForEach @(
    @{ Name = 'pwsh'; Exe = 'pwsh' }
    @{ Name = 'Windows PowerShell'; Exe = 'powershell.exe' }
) {
    BeforeAll {
        $script:exe = (Get-Command $Exe -ErrorAction SilentlyContinue).Source
    }

    It 'denies when no trace exists' {
        if (-not $script:exe) { Set-ItResult -Skipped -Because "$Exe is not installed"; return }
        Test-Denied (Invoke-GuardAt -HostExe $script:exe) | Should -BeTrue
    }

    It 'stays silent for a trace written 5 minutes ago with a 60-minute window' {
        if (-not $script:exe) { Set-ItResult -Skipped -Because "$Exe is not installed"; return }
        Invoke-GuardAt -HostExe $script:exe -AgeMinutes 5 | Should -BeNullOrEmpty
    }

    It 'denies for a trace written 120 minutes ago with a 60-minute window' {
        if (-not $script:exe) { Set-ItResult -Skipped -Because "$Exe is not installed"; return }
        Test-Denied (Invoke-GuardAt -HostExe $script:exe -AgeMinutes 120) | Should -BeTrue
    }

    It 'denies for a trace written 5 minutes ago with a 0-minute window' {
        if (-not $script:exe) { Set-ItResult -Skipped -Because "$Exe is not installed"; return }
        Test-Denied (Invoke-GuardAt -HostExe $script:exe -AgeMinutes 5 -WindowMinutes 0) | Should -BeTrue
    }
}
