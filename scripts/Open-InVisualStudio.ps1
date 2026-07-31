<#
.SYNOPSIS
    Opens a file in a specific running Visual Studio instance via COM (ROT + EnvDTE).

.DESCRIPTION
    Enumerates all running Visual Studio instances from the Windows Running Object
    Table (ROT), selects the one whose loaded solution matches -SolutionMatch (substring),
    and calls DTE.ItemOperations.OpenFile() to open the target file in that instance.

    If -SolutionMatch is not specified, falls back to the foreground VS window.
    If only one VS instance is running, uses that one regardless.

.PARAMETER Path
    The file to open. Must exist.

.PARAMETER SolutionMatch
    Substring to match against DTE.Solution.FullName (e.g. 'MyApp.Services').
    Case-insensitive. If omitted, uses heuristics (single instance or foreground).

.PARAMETER Line
    Optional line number to navigate to after opening.

.EXAMPLE
    .\Open-InVisualStudio.ps1 -Path "$env:TEMP\report.md"

.EXAMPLE
    .\Open-InVisualStudio.ps1 -Path "<repo-root>\file.cs" -SolutionMatch "MyApp.Services" -Line 42
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$Path,

    [Parameter(Position = 1)]
    [string]$SolutionMatch,

    [int]$Line = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Path = (Resolve-Path $Path -ErrorAction Stop).Path

# --- Enumerate running VS instances from the ROT ---
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class RotHelper
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable rot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx ctx);

    public static List<object[]> GetDteInstances()
    {
        var result = new List<object[]>();
        IRunningObjectTable rot;
        if (GetRunningObjectTable(0, out rot) != 0) return result;

        IEnumMoniker enumMoniker;
        rot.EnumRunning(out enumMoniker);
        enumMoniker.Reset();

        IMoniker[] monikers = new IMoniker[1];
        IntPtr fetched = IntPtr.Zero;
        IBindCtx ctx;
        CreateBindCtx(0, out ctx);

        while (enumMoniker.Next(1, monikers, fetched) == 0)
        {
            string displayName;
            monikers[0].GetDisplayName(ctx, null, out displayName);

            if (displayName.StartsWith("!VisualStudio.DTE."))
            {
                object comObj;
                if (rot.GetObject(monikers[0], out comObj) == 0)
                {
                    result.Add(new object[] { displayName, comObj });
                }
            }
        }
        return result;
    }
}
'@ -Language CSharp -ErrorAction SilentlyContinue

$instances = [RotHelper]::GetDteInstances()

if ($instances.Count -eq 0) {
    Write-Error 'No running Visual Studio instances found.'
    return
}

# --- Select the right instance ---
$dte = $null

if ($instances.Count -eq 1) {
    $dte = $instances[0][1]
    $sol = $dte.Solution.FullName
    Write-Host "Single VS instance: $sol"
}
elseif ($SolutionMatch) {
    foreach ($inst in $instances) {
        $d = $inst[1]
        try {
            $sol = $d.Solution.FullName
            if ($sol -and $sol -like "*$SolutionMatch*") {
                $dte = $d
                Write-Host "Matched VS instance: $sol"
                break
            }
        }
        catch {
            continue
        }
    }
    if (-not $dte) {
        $available = ($instances | ForEach-Object {
            try { $_[1].Solution.FullName } catch { '(no solution)' }
        }) -join "`n  "
        Write-Error "No VS instance matched '$SolutionMatch'. Running instances:`n  $available"
        return
    }
}
else {
    # Heuristic: try to find the foreground VS window
    Add-Type -TypeDefinition @'
    using System;
    using System.Runtime.InteropServices;
    public static class FgWindow {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    }
'@ -ErrorAction SilentlyContinue

    $fgHwnd = [FgWindow]::GetForegroundWindow()
    $fgPid = 0u
    [void][FgWindow]::GetWindowThreadProcessId($fgHwnd, [ref]$fgPid)

    foreach ($inst in $instances) {
        $d = $inst[1]
        try {
            # ROT moniker format: !VisualStudio.DTE.18.0:<PID>
            $monikerPid = ($inst[0] -split ':')[-1]
            if ($monikerPid -eq $fgPid.ToString()) {
                $dte = $d
                Write-Host "Foreground VS instance (PID $fgPid): $($d.Solution.FullName)"
                break
            }
        }
        catch {
            continue
        }
    }

    if (-not $dte) {
        # Last resort: list them and ask
        Write-Host "$($instances.Count) VS instances found. Use -SolutionMatch to pick one:"
        foreach ($inst in $instances) {
            $d = $inst[1]
            $pid = ($inst[0] -split ':')[-1]
            try { $sol = $d.Solution.FullName } catch { $sol = '(no solution)' }
            Write-Host "  PID $pid : $sol"
        }
        Write-Error 'Ambiguous: multiple VS instances running. Specify -SolutionMatch.'
        return
    }
}

# --- Open the file ---
$window = $dte.ItemOperations.OpenFile($Path)
Write-Host "Opened: $Path"

if ($Line -gt 0 -and $window) {
    try {
        $sel = $window.Document.Selection
        $sel.GotoLine($Line, $false)
        Write-Host "Navigated to line $Line"
    }
    catch {
        Write-Warning "Opened file but could not navigate to line ${Line}: $_"
    }
}
