<#
.SYNOPSIS
    Circuit-breaker wrapper for external commands: captured output, explicit
    timeout, and a legible failure path.

.DESCRIPTION
    DESIGN-NOTES section 8 as code: a hung external call is indistinguishable
    from thinking, so every call gets a timeout and a kill, and a failed call
    throws by default instead of degrading quietly.

    Dot-source this file, then call Invoke-External.

.NOTES
    Recreated 2026-08-02. The original never came across in the departure
    extraction (PROVENANCE.md); this is a clean rewrite against the call sites
    in Test-PreCommit.ps1 and New-RepoStatusPage.ps1.
#>

function Invoke-External {
    <#
    .SYNOPSIS
        Run an external command; throw on failure unless -NoThrow.

    .PARAMETER Executable
        Executable name or full path. Not resolved against anything: what the
        OS cannot find, this reports as a start failure. Named Executable, not
        Command, so that a pass-through argument literally spelled -Command
        (pwsh's own flag) cannot bind to the wrapper instead of the tool.

    .PARAMETER Arguments
        Everything after the executable, passed through verbatim via
        ArgumentList (no re-quoting, no escaping pain). An argument that
        matches a wrapper parameter name (-NoThrow, -Quiet, -TimeoutSec,
        -Executable) must be quoted at the call site to reach the tool.

    .PARAMETER NoThrow
        On failure, return the result object instead of throwing. The caller
        owns the outcome and reads ExitCode/Success.

    .PARAMETER Quiet
        With -NoThrow: suppress the warning a failure otherwise emits.

    .PARAMETER TimeoutSec
        Kill the process after this many seconds (default 120; 0 disables).
        A timeout is a failure: Success is false and TimedOut is true.

    .PARAMETER WorkingDirectory
        Defaults to the caller's PowerShell location. Without this,
        Process.Start inherits the .NET process CWD, which does NOT follow
        Set-Location/Push-Location -- git in particular then quietly runs
        against whatever repo the session happened to start in.

    .OUTPUTS
        [pscustomobject] Command, ExitCode, Success, TimedOut, StdOut, StdErr.
        StdOut/StdErr are string arrays (lines, trailing blank lines removed);
        ExitCode is $null when the process never ran or was killed on timeout.

    .EXAMPLE
        $r = Invoke-External -NoThrow git status --porcelain
        if ($r.ExitCode -eq 0) { $r.StdOut | Where-Object { $_ } }
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)]
        [string]$Executable,

        [Parameter(Position = 1, ValueFromRemainingArguments)]
        [string[]]$Arguments = @(),

        [switch]$NoThrow,
        [switch]$Quiet,
        [int]$TimeoutSec = 120,
        [string]$WorkingDirectory = ''
    )

    # Scoped to this function and its callees only; dot-sourcing the file must
    # not impose StrictMode on the caller's script.
    Set-StrictMode -Version Latest

    $display = ($Executable + ' ' + ($Arguments -join ' ')).Trim()

    # Lines, not raw text: every call site reads StdOut as an array. Trailing
    # newline is trimmed first so the last element is content, not ''.
    function ConvertTo-Lines ([string]$Raw) {
        $trimmed = $Raw.TrimEnd("`r", "`n")
        if ('' -eq $trimmed) { return ,@() }
        return ,@($trimmed -split "`r?`n")
    }

    function New-Result ([object]$ExitCode, [bool]$TimedOut, [object]$StdOut, [object]$StdErr) {
        [pscustomobject]@{
            Command  = $display
            ExitCode = $ExitCode
            Success  = ((-not $TimedOut) -and (0 -eq $ExitCode))
            TimedOut = $TimedOut
            StdOut   = $StdOut
            StdErr   = $StdErr
        }
    }

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Executable
    $psi.WorkingDirectory = if ('' -eq $WorkingDirectory) { (Get-Location).ProviderPath } else { $WorkingDirectory }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }

    $proc = $null
    try {
        $proc = [System.Diagnostics.Process]::Start($psi)
    } catch {
        $message = "Invoke-External: cannot start '$display': $($_.Exception.Message)"
        if (-not $NoThrow) { throw $message }
        if (-not $Quiet) { Write-Warning $message }
        return New-Result -ExitCode $null -TimedOut $false -StdOut (ConvertTo-Lines '') -StdErr (ConvertTo-Lines $_.Exception.Message)
    }

    # Async reads prevent the redirected-stream deadlock: a process that fills
    # one pipe blocks forever unless somebody is draining it while we wait.
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()

    $timedOut = $false
    if ($TimeoutSec -gt 0 -and -not $proc.WaitForExit($TimeoutSec * 1000)) {
        $timedOut = $true
        try { $proc.Kill($true) } catch { }
    }
    $proc.WaitForExit()   # second wait flushes the redirected streams

    $stdOut = ConvertTo-Lines $outTask.GetAwaiter().GetResult()
    $stdErr = ConvertTo-Lines $errTask.GetAwaiter().GetResult()
    $exitCode = if ($timedOut) { $null } else { $proc.ExitCode }
    $result = New-Result -ExitCode $exitCode -TimedOut $timedOut -StdOut $stdOut -StdErr $stdErr

    if ($result.Success) { return $result }

    $reason = if ($timedOut) { "timed out after ${TimeoutSec}s and was killed" } else { "exited $exitCode" }
    $detail = if ($stdErr.Count -gt 0) { ": $($stdErr[0])" } else { '' }
    $message = "Invoke-External: '$display' $reason$detail"

    if (-not $NoThrow) { throw $message }
    if (-not $Quiet) { Write-Warning $message }
    return $result
}
