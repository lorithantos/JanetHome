<#
.SYNOPSIS
LSP client that starts csharp-ls, indexes a solution, and finds all references for types and extension methods.

.PARAMETER Solution
Path to .slnx or .sln file.

.PARAMETER ScanPaths
Directories to scan for .cs files containing types/methods to look up.

.PARAMETER OutFile
JSON output file.
#>
param(
    [Parameter(Mandatory)][string]$Solution,
    [Parameter(Mandatory)][string[]]$ScanPaths,
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

# Start LSP server
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = 'csharp-ls'
$psi.Arguments = "--solution `"$Solution`""
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true

$process = [System.Diagnostics.Process]::Start($psi)
$reader = $process.StandardOutput
$writer = $process.StandardInput

$msgId = 0

function Send-Request($method, $params) {
    $script:msgId++
    $body = @{ jsonrpc = '2.0'; id = $script:msgId; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
    $header = "Content-Length: $($body.Length)`r`n`r`n"
    $writer.Write($header)
    $writer.Write($body)
    $writer.Flush()
    return $script:msgId
}

function Send-Notification($method, $params) {
    $body = @{ jsonrpc = '2.0'; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
    $header = "Content-Length: $($body.Length)`r`n`r`n"
    $writer.Write($header)
    $writer.Write($body)
    $writer.Flush()
}

function Read-Response {
    # Read Content-Length header
    $headerLine = $reader.ReadLine()
    while ($headerLine -and $headerLine -notmatch 'Content-Length') {
        $headerLine = $reader.ReadLine()
    }
    if (-not $headerLine) { return $null }
    $length = [int]($headerLine -replace 'Content-Length:\s*', '')
    $reader.ReadLine() # empty line
    $buffer = [char[]]::new($length)
    $read = 0
    while ($read -lt $length) {
        $read += $reader.Read($buffer, $read, $length - $read)
    }
    $json = [string]::new($buffer)
    return $json | ConvertFrom-Json
}

function Wait-ForResponse($expectedId, $timeoutMs = 60000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        if ($reader.Peek() -ge 0) {
            $response = Read-Response
            if ($response -and $response.id -eq $expectedId) { return $response }
            # Otherwise it's a notification or different response, continue
        }
        Start-Sleep -Milliseconds 100
    }
    Write-Warning "Timeout waiting for response $expectedId"
    return $null
}

try {
    # Initialize
    Write-Host 'Initializing LSP...' -ForegroundColor Cyan
    $solutionDir = (Resolve-Path (Split-Path $Solution -Parent)).Path
    if (-not $solutionDir) { $solutionDir = (Get-Location).Path }
    $repoRootEscaped = [regex]::Escape($solutionDir) + '\\\\'
    
    $initId = Send-Request 'initialize' @{
        processId = $PID
        rootUri = "file:///$($solutionDir.Replace('\', '/'))"
        capabilities = @{
            textDocument = @{
                references = @{ dynamicRegistration = $false }
                definition = @{ dynamicRegistration = $false }
            }
        }
    }

    $initResponse = Wait-ForResponse $initId 120000
    if (-not $initResponse) { throw 'LSP initialization failed' }
    Write-Host 'LSP initialized. Waiting for indexing...' -ForegroundColor Cyan

    Send-Notification 'initialized' @{}

    # Give it time to index
    Start-Sleep -Seconds 30
    Write-Host 'Ready.' -ForegroundColor Green

    # Now find all types in Core/ and get their references
    $coreFiles = $ScanPaths | ForEach-Object {
        Get-ChildItem -Path $_ -Recurse -Filter '*.cs' |
            Where-Object { $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*' }
    }

    $results = @{}

    foreach ($file in $coreFiles) {
        $uri = "file:///$($file.FullName.Replace('\', '/'))"
        $content = Get-Content $file.FullName -Raw
        $lines = $content -split "`n"

        # Find type and method declarations
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            # Match type declarations
            if ($line -match '^\s*(?:public|internal|private|protected)?\s*(?:static\s+)?(?:abstract\s+)?(?:sealed\s+)?(?:partial\s+)?(?:class|interface|enum|struct|record)\s+(\w+)') {
                $typeName = $Matches[1]
                $col = $line.IndexOf($typeName)
                $refId = Send-Request 'textDocument/references' @{
                    textDocument = @{ uri = $uri }
                    position = @{ line = $i; character = $col }
                    context = @{ includeDeclaration = $false }
                }
                $refResponse = Wait-ForResponse $refId 10000
                $refs = @()
                if ($refResponse -and $refResponse.result) {
                    $refs = $refResponse.result | ForEach-Object {
                        $_.uri -replace 'file:///', '' -replace '/', '\'
                    }
                }
                $results[$typeName] = @{
                    file = $file.FullName -replace $repoRootEscaped, ''
                    type = 'type'
                    references = $refs
                    refCount = $refs.Count
                }
            }
            # Match extension methods
            if ($line -match 'public\s+static\s+\S+\s+(\w+)\s*(?:<[^>]+>)?\s*\(\s*this\s+') {
                $methodName = $Matches[1]
                $col = $line.IndexOf($methodName)
                $refId = Send-Request 'textDocument/references' @{
                    textDocument = @{ uri = $uri }
                    position = @{ line = $i; character = $col }
                    context = @{ includeDeclaration = $false }
                }
                $refResponse = Wait-ForResponse $refId 10000
                $refs = @()
                if ($refResponse -and $refResponse.result) {
                    $refs = $refResponse.result | ForEach-Object {
                        $_.uri -replace 'file:///', '' -replace '/', '\'
                    }
                }
                $results["$typeName.$methodName"] = @{
                    file = $file.FullName -replace $repoRootEscaped, ''
                    type = 'extensionMethod'
                    references = $refs
                    refCount = $refs.Count
                }
            }
        }
        Write-Host "  Scanned: $($file.Name) ($($results.Count) symbols so far)"
    }

    # Output
    if ($OutFile) {
        $results | ConvertTo-Json -Depth 5 | Set-Content $OutFile -Encoding utf8
        Write-Host "Written to $OutFile ($($results.Count) symbols)" -ForegroundColor Green
    } else {
        $results | ConvertTo-Json -Depth 5
    }
}
finally {
    # Shutdown
    Send-Request 'shutdown' $null | Out-Null
    Send-Notification 'exit' $null
    Start-Sleep -Seconds 2
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
}
