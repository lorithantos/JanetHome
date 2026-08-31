<#
.SYNOPSIS
    Captures a running application's window to a PNG, so an agent can see what it drew.

.DESCRIPTION
    Launches an executable (or attaches to a running process), waits for its first
    titled window, captures that window's client area, and writes a PNG.

    Exists because an agent session cannot look at a window. Without this, "does
    the app render correctly" degrades into "it started and did not crash", which
    is a much weaker claim than it sounds -- a viewer that draws nothing at all
    passes it. A PNG can be read back and compared; a window cannot.

    Three things here were learned by getting them wrong, and each one produced a
    confidently wrong answer rather than an error:

      * MATCH BY PROCESS ID, NOT TITLE. Title matching picked up an unrelated
        window that merely had the application's name in its caption -- an editor
        showing the source path -- and silently captured that instead.
      * SKIP THE CONSOLE WINDOW. A console-subsystem exe owns a console window
        too, and Process.MainWindowHandle can resolve to it. It has no title,
        which is the discriminator used here.
      * DECLARE DPI AWARENESS FIRST. A DPI-unaware process receives virtualized
        screen coordinates, so ClientToScreen returns a position that is simply
        somewhere else on a scaled display. The capture succeeds and shows the
        wrong part of the screen.

    Pairs with Compare-Image.ps1, which diffs two captures.

.PARAMETER Path
    Executable to launch and capture.

.PARAMETER ArgumentList
    Arguments for the launched executable.

.PARAMETER ProcessId
    Attach to an already-running process instead of launching one. An attached
    process is never killed, regardless of -KeepRunning.

.PARAMETER OutFile
    Where to write the PNG.

.PARAMETER TimeoutSeconds
    How long to wait for a titled window to appear. Default 15.

.PARAMETER SettleMilliseconds
    Pause after bringing the window forward, before capturing. Default 1200.
    A GPU-rendered window may present its first real frame a few frames after it
    becomes visible, and capturing too early yields a blank or partial image.

.PARAMETER FullWindow
    Capture the whole window including its frame, rather than the client area.
    The client area is the default because it is the part the application drew.

.PARAMETER KeepRunning
    Leave a launched process running. By default it is killed after capture.

.PARAMETER Text
    Formatted output for a terminal. The default is JSON.

.PARAMETER Pretty
    Indent the JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Get-WindowCapture.ps1" -Path .\bin\Debug\net10.0\Viewer.exe -OutFile shot.png

.EXAMPLE
    & "$env:JanetBase\scripts\Get-WindowCapture.ps1" -ProcessId 1234 -OutFile shot.png -Text
#>
[CmdletBinding(DefaultParameterSetName = 'Launch')]
param(
    [Parameter(ParameterSetName = 'Launch', Mandatory = $true)]
    [string]$Path,

    [Parameter(ParameterSetName = 'Launch')]
    [string[]]$ArgumentList = @(),

    [Parameter(ParameterSetName = 'Attach', Mandatory = $true)]
    [int]$ProcessId,

    [Parameter(Mandatory = $true)]
    [string]$OutFile,

    [int]$TimeoutSeconds = 15,

    [int]$SettleMilliseconds = 1200,

    [switch]$FullWindow,

    [switch]$KeepRunning,

    [switch]$Text,

    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# Guarded: Add-Type throws if the type is already defined, and this script is
# expected to be called repeatedly within one session.
if (-not ('JanetWindowCapture' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class JanetWindowCapture
{
    private delegate bool EnumProc(IntPtr window, IntPtr param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr window, ref POINT point);

    public struct RECT { public int Left, Top, Right, Bottom; }
    public struct POINT { public int X, Y; }

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. Returns false if awareness was
    // already set for the process, which is not an error.
    public static void DeclareDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
    }

    // The owning process is the only unambiguous identity available. A titled
    // window is required so the console window of a console-subsystem exe is
    // skipped.
    public static IntPtr FindTitledWindow(int wantedProcessId, out string title)
    {
        IntPtr found = IntPtr.Zero;
        string caption = null;

        EnumWindows((window, param) =>
        {
            if (!IsWindowVisible(window)) { return true; }

            int owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != wantedProcessId) { return true; }

            var builder = new StringBuilder(512);
            GetWindowTextW(window, builder, builder.Capacity);
            if (builder.Length == 0) { return true; }

            found = window;
            caption = builder.ToString();
            return false;
        }, IntPtr.Zero);

        title = caption;
        return found;
    }
}
'@
}

[JanetWindowCapture]::DeclareDpiAwareness()

$launched = $false
$target = $null
$result = [ordered]@{
    ok           = $false
    outFile      = $null
    width        = 0
    height       = 0
    originX      = 0
    originY      = 0
    processId    = 0
    windowTitle  = $null
    region       = $(if ($FullWindow) { 'window' } else { 'client' })
    launched     = $false
    waitedMs     = 0
    error        = $null
}

try {
    if ($PSCmdlet.ParameterSetName -eq 'Launch') {
        if (-not (Test-Path $Path -PathType Leaf)) {
            throw "Executable not found: $Path"
        }

        $target = if ($ArgumentList.Count -gt 0) {
            Start-Process -FilePath $Path -ArgumentList $ArgumentList -PassThru
        }
        else {
            Start-Process -FilePath $Path -PassThru
        }
        $launched = $true
    }
    else {
        $target = Get-Process -Id $ProcessId
    }

    $result.processId = $target.Id
    $result.launched = $launched

    $window = [IntPtr]::Zero
    $caption = $null
    $waited = [System.Diagnostics.Stopwatch]::StartNew()

    while ($window -eq [IntPtr]::Zero -and $waited.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($target.HasExited) {
            throw "Process $($target.Id) exited with $($target.ExitCode) before showing a window."
        }

        Start-Sleep -Milliseconds 200
        $window = [JanetWindowCapture]::FindTitledWindow($target.Id, [ref]$caption)
    }

    $waited.Stop()
    $result.waitedMs = [int]$waited.Elapsed.TotalMilliseconds

    if ($window -eq [IntPtr]::Zero) {
        throw "No titled window appeared for process $($target.Id) within $TimeoutSeconds seconds."
    }

    $result.windowTitle = $caption

    [JanetWindowCapture]::SetForegroundWindow($window) | Out-Null
    Start-Sleep -Milliseconds $SettleMilliseconds

    $rect = New-Object JanetWindowCapture+RECT
    $originX = 0
    $originY = 0

    if ($FullWindow) {
        [JanetWindowCapture]::GetWindowRect($window, [ref]$rect) | Out-Null
        $originX = $rect.Left
        $originY = $rect.Top
    }
    else {
        [JanetWindowCapture]::GetClientRect($window, [ref]$rect) | Out-Null
        $origin = New-Object JanetWindowCapture+POINT
        [JanetWindowCapture]::ClientToScreen($window, [ref]$origin) | Out-Null
        $originX = $origin.X
        $originY = $origin.Y
    }

    $captureWidth = $rect.Right - $rect.Left
    $captureHeight = $rect.Bottom - $rect.Top

    if ($captureWidth -le 0 -or $captureHeight -le 0) {
        throw "Window reported a $captureWidth x $captureHeight region; nothing to capture."
    }

    $destination = [System.IO.Path]::GetFullPath($OutFile)
    $destinationDirectory = Split-Path $destination -Parent
    if ($destinationDirectory -and -not (Test-Path $destinationDirectory)) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }

    $bitmap = New-Object System.Drawing.Bitmap($captureWidth, $captureHeight)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            # CopyFromScreen grabs the SCREEN region, not the window's own
            # backing store, which is why the window is brought forward first.
            # PrintWindow would capture an occluded window, but returns black for
            # GPU-presented content (Direct3D, OpenGL) -- which is exactly the
            # content this script exists to look at.
            $graphics.CopyFromScreen(
                $originX, $originY, 0, 0,
                (New-Object System.Drawing.Size($captureWidth, $captureHeight)))
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    $result.ok = $true
    $result.outFile = $destination
    $result.width = $captureWidth
    $result.height = $captureHeight
    $result.originX = $originX
    $result.originY = $originY
}
catch {
    $result.error = $_.Exception.Message
}
finally {
    if ($launched -and -not $KeepRunning -and $null -ne $target) {
        try {
            if (-not $target.HasExited) {
                $target.Kill()
                $target.WaitForExit(5000) | Out-Null
            }
        }
        catch {
            # A process that has already gone is the outcome we wanted.
        }
    }
}

if (-not $Text) {
    if ($Pretty) { [PSCustomObject]$result | ConvertTo-Json -Depth 4 }
    else { [PSCustomObject]$result | ConvertTo-Json -Depth 4 -Compress }
    if (-not $result.ok) { exit 1 }
    exit 0
}

if ($result.ok) {
    Write-Host "captured $($result.width)x$($result.height) ($($result.region)) from pid $($result.processId)"
    Write-Host "  window: $($result.windowTitle)"
    Write-Host "  origin: $($result.originX),$($result.originY)   waited: $($result.waitedMs)ms"
    Write-Host "  file:   $($result.outFile)"
    exit 0
}

Write-Host "capture failed: $($result.error)" -ForegroundColor Yellow
exit 1
