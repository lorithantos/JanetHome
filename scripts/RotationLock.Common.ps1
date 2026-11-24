<#
.SYNOPSIS
    One MCP server rotation at a time, across every server: a cross-process lock that
    Update-McpServer.ps1 holds for its whole run.

.DESCRIPTION
    Dot-sourced by Update-McpServer.ps1. Not a standalone script -- it defines functions
    and does nothing on its own. Like GraphCarryOver.Common.ps1 it does NOT set
    Set-StrictMode, which would leak into the caller; the caller sets its own.

    WHY ACROSS SERVERS. server_rotate refuses a second rotation of the SAME server, because
    two would race to repoint one junction. Rotations of DIFFERENT servers were left free to
    overlap, and on 2026-11-24 they did: janet's rotation was reinstalling the Janet.Cli
    global tool at the moment razorgraph's carry-over ran `janet mcp-headers`, that call
    failed ("StandardOutputEncoding is only supported when standard output is redirected"),
    and the post-load list_graphs got a 401. razorgraph rotated alone minutes later with a
    clean carry-over. A rotation uses the other server's tools (the CLI, the token), so the
    whole run -- publish, junction swap, restart, CLI reinstall, carry-over -- is serialised.

    THE PRIMITIVE is the one Janet.Core's FileLock uses (WriteQueue.cs): a sidecar file held
    open by a FileStream whose share mode refuses a second writer. The OS releases the
    handle when the process dies, so a crashed rotation cannot leave the lock behind -- the
    file may stay, and an unheld file is not a lock. Not a named mutex: the thread-item
    scripts used one, and a dead holder hands the next caller AbandonedMutexException to
    reason about. Not a file whose EXISTENCE is the lock: a crash leaves that held forever.

    The holder writes a line naming itself INTO the lock file, and shares Read so a waiter
    can read it: that is how the waiting line and the timeout name the rotation in the way.
    The waiter's own open asks for ReadWrite, which the holder's share mode refuses, so the
    lock is exactly as exclusive as FileShare.None for anyone trying to take it.

    The handle is opened NON-INHERITABLE (no FileShare.Inheritable). That is load-bearing:
    the rotation starts the new server while holding the lock, and an inherited handle would
    keep the lock held for the life of that server. Pinned by a test.
#>

# A rotation takes 6-10 s (rotation-*.json startedAt to rotation-*.result.json finishedAt,
# 2026-10-08 and 2026-11-24). The slow path is a cold publish, the 15 s listener wait, a
# carry-over of several graphs and a pack and reinstall of the CLI -- a minute or two at
# worst. Five minutes is far past that: a holder still going then is stuck, and waiting
# longer only hides it.
$script:RotationLockTimeoutSeconds = 300

function Get-RotationLockPath {
    # Beside server_rotate's state, log and result files (ServerRotation.StateDirectory).
    return Join-Path ([System.IO.Path]::GetTempPath()) 'Janet\rotation.lock'
}

function Test-RotationLockContention {
    # True when an IOException is the file being held by someone else -- a sharing or lock
    # violation -- rather than a real fault (a bad path, a full disk), which must not be
    # waited out for five minutes and then misreported as contention.
    param([Parameter(Mandatory)][System.IO.IOException]$Exception)
    $code = $Exception.HResult -band 0xFFFF
    return ($code -eq 32 -or $code -eq 33)
}

function Get-RotationLockHolder {
    <#
    .SYNOPSIS
        The line the current holder wrote into the lock file, or $null when there is none
        to read (nobody wrote one yet, or the file is not there).
    #>
    param([Parameter(Mandatory)][string]$Path)

    try {
        $stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    }
    catch { return $null }

    try {
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.UTF8Encoding]::new($false))
        $text = $reader.ReadToEnd().Trim()
        if ($text) { return $text }
        return $null
    }
    catch { return $null }
    finally { $stream.Dispose() }
}

function Enter-RotationLock {
    <#
    .SYNOPSIS
        Takes the rotation lock, waiting up to -TimeoutSeconds for the holder to let go.
        Returns the open FileStream; hand it to Exit-RotationLock in a finally.

    .DESCRIPTION
        Logs ONE line when it has to wait, naming the holder. On timeout it throws, naming
        the holder when the lock file says who that is.

    .PARAMETER Holder
        Who is taking it, as the waiting line and the timeout will name it to the next one,
        e.g. "janet-mcp rotation (pid 1234, since 2026-11-24T19:14:07Z)".

    .PARAMETER Log
        Receives the waiting line. Defaults to Write-Host, which a server_rotate launcher
        sends to rotation-<server>.log, so the line shows in server_rotation's logTail.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Holder,
        [int]$TimeoutSeconds = $script:RotationLockTimeoutSeconds,
        [int]$PollMilliseconds = 250,
        [scriptblock]$Log = { param($line) Write-Host $line -ForegroundColor Yellow }
    )

    $directory = Split-Path -Path $Path -Parent
    if ($directory -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $logged = $false
    while ($true) {
        $stream = $null
        try {
            $stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
        }
        catch [System.IO.IOException] {
            if (-not (Test-RotationLockContention -Exception $_.Exception)) { throw }
        }

        if ($null -ne $stream) {
            try {
                $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Holder)
                $stream.SetLength(0)
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush()
            }
            catch {
                # Naming the holder is a courtesy to the next waiter; failing to write it
                # must not fail a rotation that already holds the lock.
                Write-Verbose "could not record the rotation lock holder: $($_.Exception.Message)"
            }
            return $stream
        }

        if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            $now = Get-RotationLockHolder -Path $Path
            $who = if ($now) { "held by $now" } else { 'held by a process that did not record itself' }
            throw ("Gave up after $TimeoutSeconds s waiting for the rotation lock $Path, $who. " +
                'Nothing was published, swapped or restarted. Read that rotation with server_rotation; ' +
                'a rotation that died released the lock with it, so a lock still held is a live process.')
        }

        if (-not $logged) {
            $logged = $true
            $who = Get-RotationLockHolder -Path $Path
            if (-not $who) { $who = 'another rotation (it has not recorded itself yet)' }
            & $Log "waiting for the rotation lock, held by $who; up to $TimeoutSeconds s"
        }

        Start-Sleep -Milliseconds $PollMilliseconds
    }
}

function Exit-RotationLock {
    # Clears the holder line, then closes the handle -- the close is the release. Cleared
    # first so a waiter that reads between another holder's open and its write sees nobody
    # rather than this rotation. Safe to call with $null, and twice.
    param([System.IO.FileStream]$Lock)

    if ($null -eq $Lock) { return }
    try { if ($Lock.CanWrite) { $Lock.SetLength(0) } }
    catch { Write-Verbose "could not clear the rotation lock holder: $($_.Exception.Message)" }
    $Lock.Dispose()
}
