<#
.SYNOPSIS
    Compares two PNGs pixel by pixel and reports where, and how much, they differ.

.DESCRIPTION
    The other half of Get-WindowCapture.ps1: capture answers "what does it look
    like", this answers "did it change".

    Reports a count, a ratio, the maximum per-channel delta, and the BOUNDING BOX
    of the differences. The bounding box is the field that earns its place --
    a bare count says something changed, while a box says whether it was the
    thing you touched or the corner of the window, and those need different
    reactions.

    WHY AN EXACT-ZERO THRESHOLD IS USUALLY WRONG FOR WINDOW CAPTURES. Windows 11
    rounds window corners and composites the desktop through them, so the corner
    pixels of any captured window carry whatever was behind it. Two captures of
    the identical application taken moments apart differ there by a few grey
    levels and nothing else. Measured on a 996x607 client area: 31 differing
    pixels of 604,572, all inside a 10x10 patch at one corner, none of them the
    application's doing. Read the bounding box before believing a small non-zero
    count means a regression.

    Comparison is done over locked bits rather than GetPixel, which matters:
    GetPixel over a 600k-pixel image is seconds, LockBits is milliseconds.

.PARAMETER Reference
    The image to compare against -- the "before", or the committed golden.

.PARAMETER Candidate
    The image under test.

.PARAMETER Tolerance
    Per-channel difference, 0-255, at or below which two pixels count as equal.
    Default 0. Use a small value to absorb compositing and antialiasing noise.

.PARAMETER MaxDifferingRatio
    Fail (exit 1) if the differing ratio exceeds this. Omit to report without
    gating.

.PARAMETER DiffFile
    Write a visualisation: differing pixels in red over a dimmed copy of the
    reference.

.PARAMETER Text
    Formatted output for a terminal. The default is JSON.

.PARAMETER Pretty
    Indent the JSON.

.EXAMPLE
    & "$env:JanetBase\scripts\Compare-Image.ps1" -Reference before.png -Candidate after.png -Text

.EXAMPLE
    & "$env:JanetBase\scripts\Compare-Image.ps1" -Reference golden.png -Candidate shot.png -Tolerance 4 -MaxDifferingRatio 0.001 -DiffFile diff.png
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Reference,

    [Parameter(Mandatory = $true)]
    [string]$Candidate,

    [ValidateRange(0, 255)]
    [int]$Tolerance = 0,

    [double]$MaxDifferingRatio = -1.0,

    [string]$DiffFile,

    [switch]$Text,

    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function Read-Pixels {
    # ASSIGN THE RESULT. Returns a hashtable rather than multiple values, so
    # nothing depends on PowerShell's output unrolling.
    param([System.Drawing.Bitmap]$Bitmap)

    $area = New-Object System.Drawing.Rectangle(0, 0, $Bitmap.Width, $Bitmap.Height)
    $locked = $Bitmap.LockBits(
        $area,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $buffer = New-Object byte[] ($locked.Stride * $Bitmap.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $buffer, 0, $buffer.Length)
        return @{ Bytes = $buffer; Stride = $locked.Stride }
    }
    finally {
        $Bitmap.UnlockBits($locked)
    }
}

$result = [ordered]@{
    ok                = $false
    width             = 0
    height            = 0
    differingPixels   = 0
    totalPixels       = 0
    differingRatio    = 0.0
    maxChannelDelta   = 0
    boundingBox       = $null
    tolerance         = $Tolerance
    diffFile          = $null
    gated             = ($MaxDifferingRatio -ge 0.0)
    withinGate        = $true
    error             = $null
}

$left = $null
$right = $null

try {
    foreach ($candidatePath in @($Reference, $Candidate)) {
        if (-not (Test-Path $candidatePath -PathType Leaf)) {
            throw "Image not found: $candidatePath"
        }
    }

    $left = New-Object System.Drawing.Bitmap([System.IO.Path]::GetFullPath($Reference))
    $right = New-Object System.Drawing.Bitmap([System.IO.Path]::GetFullPath($Candidate))

    if ($left.Width -ne $right.Width -or $left.Height -ne $right.Height) {
        throw "Dimensions differ: $($left.Width)x$($left.Height) vs $($right.Width)x$($right.Height)"
    }

    $width = $left.Width
    $height = $left.Height
    $result.width = $width
    $result.height = $height
    $result.totalPixels = $width * $height

    $leftPixels = Read-Pixels -Bitmap $left
    $rightPixels = Read-Pixels -Bitmap $right
    $bytesLeft = $leftPixels.Bytes
    $bytesRight = $rightPixels.Bytes
    $stride = $leftPixels.Stride

    $wantDiff = -not [string]::IsNullOrWhiteSpace($DiffFile)
    $diffBytes = $null
    if ($wantDiff) {
        $diffBytes = New-Object byte[] $bytesLeft.Length
    }

    $differing = 0
    $maxDelta = 0
    $minX = [int]::MaxValue
    $minY = [int]::MaxValue
    $maxX = -1
    $maxY = -1

    for ($y = 0; $y -lt $height; $y++) {
        $row = $y * $stride
        for ($x = 0; $x -lt $width; $x++) {
            $at = $row + ($x * 4)

            # Format32bppArgb is B,G,R,A in memory. Alpha is ignored: a screen
            # capture is opaque, and a captured alpha channel carries nothing.
            $deltaB = [Math]::Abs([int]$bytesLeft[$at] - [int]$bytesRight[$at])
            $deltaG = [Math]::Abs([int]$bytesLeft[$at + 1] - [int]$bytesRight[$at + 1])
            $deltaR = [Math]::Abs([int]$bytesLeft[$at + 2] - [int]$bytesRight[$at + 2])

            $worst = [Math]::Max($deltaB, [Math]::Max($deltaG, $deltaR))
            if ($worst -gt $maxDelta) { $maxDelta = $worst }

            if ($worst -gt $Tolerance) {
                $differing++
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }

                if ($wantDiff) {
                    $diffBytes[$at] = 0
                    $diffBytes[$at + 1] = 0
                    $diffBytes[$at + 2] = 255
                    $diffBytes[$at + 3] = 255
                }
            }
            elseif ($wantDiff) {
                # Dimmed grey, so the red stands out against the layout it sits in.
                $grey = [byte](([int]$bytesLeft[$at] + [int]$bytesLeft[$at + 1] + [int]$bytesLeft[$at + 2]) / 6)
                $diffBytes[$at] = $grey
                $diffBytes[$at + 1] = $grey
                $diffBytes[$at + 2] = $grey
                $diffBytes[$at + 3] = 255
            }
        }
    }

    $result.differingPixels = $differing
    $result.maxChannelDelta = $maxDelta
    $result.differingRatio = if ($result.totalPixels -gt 0) { $differing / $result.totalPixels } else { 0.0 }

    if ($differing -gt 0) {
        $result.boundingBox = [ordered]@{
            x      = $minX
            y      = $minY
            width  = $maxX - $minX + 1
            height = $maxY - $minY + 1
        }
    }

    if ($wantDiff) {
        $destination = [System.IO.Path]::GetFullPath($DiffFile)
        $destinationDirectory = Split-Path $destination -Parent
        if ($destinationDirectory -and -not (Test-Path $destinationDirectory)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        }

        $diffBitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $area = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
            $locked = $diffBitmap.LockBits(
                $area,
                [System.Drawing.Imaging.ImageLockMode]::WriteOnly,
                [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                [System.Runtime.InteropServices.Marshal]::Copy($diffBytes, 0, $locked.Scan0, $diffBytes.Length)
            }
            finally {
                $diffBitmap.UnlockBits($locked)
            }

            $diffBitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
            $result.diffFile = $destination
        }
        finally {
            $diffBitmap.Dispose()
        }
    }

    if ($result.gated -and $result.differingRatio -gt $MaxDifferingRatio) {
        $result.withinGate = $false
    }

    $result.ok = $true
}
catch {
    $result.error = $_.Exception.Message
}
finally {
    if ($null -ne $left) { $left.Dispose() }
    if ($null -ne $right) { $right.Dispose() }
}

$failed = (-not $result.ok) -or (-not $result.withinGate)

if (-not $Text) {
    if ($Pretty) { [PSCustomObject]$result | ConvertTo-Json -Depth 4 }
    else { [PSCustomObject]$result | ConvertTo-Json -Depth 4 -Compress }
    if ($failed) { exit 1 }
    exit 0
}

if (-not $result.ok) {
    Write-Host "compare failed: $($result.error)" -ForegroundColor Yellow
    exit 1
}

$percent = '{0:P4}' -f $result.differingRatio
Write-Host "$($result.width)x$($result.height): $($result.differingPixels) of $($result.totalPixels) pixels differ ($percent)"
Write-Host "  max channel delta: $($result.maxChannelDelta)   tolerance: $($result.tolerance)"
if ($null -ne $result.boundingBox) {
    $box = $result.boundingBox
    Write-Host "  bounding box:      $($box.width)x$($box.height) at $($box.x),$($box.y)"
}
if ($result.diffFile) { Write-Host "  diff image:        $($result.diffFile)" }
if ($result.gated) {
    $verdict = if ($result.withinGate) { 'within gate' } else { 'OVER GATE' }
    Write-Host "  gate:              $verdict (max $MaxDifferingRatio)"
}

if ($failed) { exit 1 }
exit 0
