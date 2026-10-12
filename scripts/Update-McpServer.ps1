<#
.SYNOPSIS
    Publishes an MCP server to a fresh directory and repoints a stable path at it, so a
    rebuild does not require closing the sessions using it.

.DESCRIPTION
    A running MCP server holds its binaries open. Publishing over them fails while any
    session is attached, which is why note.razorgraph-cli exists: the tool you reach for to
    understand a codebase is the tool that stops you building it.

    Publishing to a SEPARATE directory each time removes the conflict entirely, because the
    new build never touches the files the running server has open. A junction named 'current'
    is then repointed at the new build, and that is what client config names -- so the config
    is a stable path that resolves to the newest build by lookup, and no .mcp.json ever has to
    change. Repointing a junction does not disturb the running process: it opened the real
    directory, not the link.

    What that buys, per transport:

      HTTP   The server is a separate process the client dials. Publish while it is still
             serving, then stop and start it from 'current'. Downtime is a process start, not
             a build -- and the client reconnects on its own (five attempts, 1s doubling).

      stdio  The client owns the process, so this script cannot restart it. The win is that
             the publish SUCCEEDS while sessions are attached; each new session then picks up
             the newest build through the junction, with no publish-time coordination at all.

    GRAPHS SURVIVE A RAZORGRAPH ROTATION. A razorgraph server holds its graphs in memory,
    so stopping it used to drop every one silently. When the running server serves
    list_graphs, the graphs are saved to %TEMP%\Janet\razorgraph-rotation before the stop
    and loaded back under their original ids once the new server listens -- the newest per
    (main repo, graph kind) only, the previously current one last so it stays current. The
    report is the last thing printed. A save or load failure is reported verbatim and never
    fails the rotation. Decision logic: GraphCarryOver.Common.ps1.

.PARAMETER Project
    The server .csproj to publish.

.PARAMETER Root
    Directory holding the rotating builds and the 'current' junction. Defaults to
    <repo>\.janet-bin.

.PARAMETER ProcessName
    Running process to stop and restart. Omit for stdio servers, where the client owns it.

.PARAMETER ServerArgument
    Arguments for the restarted process. Only used when -ProcessName is given.

.PARAMETER Keep
    Old build directories to retain. Defaults to 3 -- enough to roll back by repointing.

.PARAMETER ToolProject
    Global-tool projects to pack and reinstall in the same pass. The CLI is NOT rotated -- a
    global tool is a separate copy on PATH -- so rebuilding the server leaves it stale, and a
    stale CLI reports the new verb as unknown while the server serves it fine. That has been
    written down and still caught us three times, which is what a note cannot fix and a
    parameter can. Note `dotnet tool update` no-ops on an identical version, so this
    uninstalls and installs.

.EXAMPLE
    # HTTP dev loop: publish, swap, restart. Sessions reconnect themselves.
    .\Update-McpServer.ps1 -Project src\Janet.Mcp\Janet.Mcp.csproj -ProcessName janet-mcp `
        -ServerArgument '--http', '--base', 'D:\Repos\JanetHome'

.EXAMPLE
    # RazorGraph, stdio: the publish now succeeds with sessions attached.
    .\Update-McpServer.ps1 -Project src\RazorGraph.Mcp\RazorGraph.Mcp.csproj `
        -Root D:\Repos\RazorGraphTool\.mcp-bin
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [string]$Root,
    [string]$ProcessName,
    [string[]]$ServerArgument = @(),
    [string[]]$ToolProject = @(),
    [int]$Keep = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'GraphCarryOver.Common.ps1')
if (-not $Root) { $Root = Join-Path $repoRoot '.janet-bin' }
if (-not (Test-Path -LiteralPath $Project)) { throw "Project not found: $Project" }
if (-not (Test-Path -LiteralPath $Root)) { New-Item -ItemType Directory -Path $Root | Out-Null }

# ---- Who is running out of a directory -------------------------------------

# Win32_Process rather than Get-Process, because the decisions here need the executable path
# (which build is this?) and the command line (stdio or HTTP?), and Get-Process carries
# neither reliably for a process started by another user session.
function Get-ServerProcess {
    [CmdletBinding()]
    param([string]$Name, [Parameter(Mandatory)][string]$Under)

    $full = [System.IO.Path]::GetFullPath($Under).TrimEnd('\')

    $filter = if ($Name) { "Name = '$Name.exe'" } else { $null }
    $all = if ($filter) {
        @(Get-CimInstance Win32_Process -Filter $filter -ErrorAction SilentlyContinue)
    }
    else {
        @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $null -ne $_.ExecutablePath })
    }

    # Normalised on the way out, so no caller ever reads a property that may not be there.
    # CommandLine in particular is absent, not null, on a process this user cannot inspect --
    # and a missing property is a terminating error under StrictMode (house rule 2).
    $matched = foreach ($p in $all) {
        if ([string]::IsNullOrEmpty($p.ExecutablePath)) { continue }

        # Resolve first: 'current' is a junction, so a process launched through it reports the
        # REAL build directory, and a string compare against the link would miss it entirely.
        $exe = try { [System.IO.Path]::GetFullPath($p.ExecutablePath) } catch { $p.ExecutablePath }
        if (-not $exe.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }

        $names = $p.PSObject.Properties.Name
        [PSCustomObject]@{
            ProcessId      = $p.ProcessId
            ExecutablePath = $exe
            CommandLine    = if ($names -contains 'CommandLine' -and $p.CommandLine) { [string]$p.CommandLine } else { '' }
        }
    }

    return , @($matched)
}

function Get-ListenerPid {
    # $null when nothing holds the port. Get-NetTCPConnection is Windows-only and throws
    # rather than returning empty when there is no match, so both are caught here. Same
    # function as Ensure-McpServer.ps1 carries, for the same reason.
    param([int]$TcpPort)
    try {
        $conn = @(Get-NetTCPConnection -State Listen -LocalPort $TcpPort -ErrorAction Stop)
        if ($conn.Count -eq 0) { return $null }
        return [int]$conn[0].OwningProcess
    }
    catch { return $null }
}

# ---- Graph carry-over (razorgraph only) -------------------------------------

$mcpTool = Join-Path $PSScriptRoot 'Invoke-McpTool.ps1'
$carryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'Janet\razorgraph-rotation'

function Invoke-GraphTool {
    # One call through Invoke-McpTool, as { Ok; Result; Error }. Never throws: the carry-over
    # is a convenience and the swap is the job, so a failure here is reported, not raised.
    # Error is the server's own text, verbatim, so the log says what actually went wrong.
    param([string]$Tool, [hashtable]$Arguments = @{}, [Parameter(Mandatory)][string]$Uri, [switch]$ListTools)

    $out = $null
    $code = 0
    try {
        $out = if ($ListTools) { & $mcpTool -ListTools -Uri $Uri 2>&1 }
        else { & $mcpTool -Tool $Tool -Arguments $Arguments -Uri $Uri 2>&1 }
        $code = $LASTEXITCODE
    }
    catch { return [PSCustomObject]@{ Ok = $false; Result = $null; Error = $_.Exception.Message } }

    $text = (@($out) | ForEach-Object { "$_" }) -join [Environment]::NewLine
    $envelope = $null
    try { $envelope = $text | ConvertFrom-Json }
    catch { return [PSCustomObject]@{ Ok = $false; Result = $null; Error = $text } }

    $ok = ($code -eq 0) -and ((Get-GraphCarryOverProperty $envelope 'ok') -eq $true)
    $result = if ($ListTools) { $envelope } else { Get-GraphCarryOverProperty $envelope 'result' }
    $errorText = $null
    if (-not $ok) {
        $errorText = [string](Get-GraphCarryOverProperty $envelope 'error')
        if (-not $errorText) {
            $errorText = if ($result -is [string]) { $result }
            elseif ($null -ne $result) { $result | ConvertTo-Json -Depth 8 -Compress }
            else { $text }
        }
    }
    return [PSCustomObject]@{ Ok = $ok; Result = $result; Error = $errorText }
}

function Save-GraphCarryOver {
    # Before the stop: list, decide, save. Returns $null when the server on $Port does not
    # serve list_graphs -- that is the gate, so janet-mcp (and anything else) is untouched.
    param([Parameter(Mandatory)][int]$Port)

    $uri = "http://127.0.0.1:$Port/"
    $state = [PSCustomObject]@{
        Port = $Port; Uri = $uri; Directory = $carryDirectory; Plan = $null; HeldCount = 0
        Saved = @(); Loaded = @(); Failures = @(); CurrentAfter = $null; Stage = 'listed'; Note = $null
    }

    $surface = Invoke-GraphTool -ListTools -Uri $uri
    if (-not $surface.Ok) { return $null }   # Nothing answering: nothing loaded to lose.
    $tools = @(Get-GraphCarryOverProperty $surface.Result 'tools')
    if (-not ($tools -contains 'list_graphs' -and $tools -contains 'save_graph' -and $tools -contains 'load_graph')) {
        return $null
    }

    $listed = Invoke-GraphTool -Tool 'list_graphs' -Uri $uri
    if (-not $listed.Ok) {
        $state.Note = 'list_graphs failed before the stop'
        $state.Failures = @("list_graphs: $($listed.Error)")
        return $state
    }

    $graphs = @(Get-GraphCarryOverProperty $listed.Result 'graphs')
    $state.HeldCount = $graphs.Count
    if ($graphs.Count -eq 0) {
        $state.Plan = Select-GraphCarryOver -Graph @()
        return $state
    }

    # The previous rotation's manifest first, BEFORE clearing: a graph it carried has the
    # rotation file as its source now, and only the manifest knows which repo that was.
    $remembered = Read-GraphCarryOverManifest -Directory $carryDirectory
    $state.Plan = Select-GraphCarryOver -Graph $graphs -Current ([string](Get-GraphCarryOverProperty $listed.Result 'current')) -Remembered $remembered

    $failures = [System.Collections.Generic.List[string]]::new()
    try {
        if (-not (Test-Path -LiteralPath $carryDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $carryDirectory -Force | Out-Null
        }
        Get-ChildItem -LiteralPath $carryDirectory -File -Filter '*.json' -ErrorAction Stop |
            Remove-Item -Force -ErrorAction Stop
    }
    catch {
        $failures.Add("clearing $carryDirectory`: $($_.Exception.Message)")
    }

    $saved = [System.Collections.Generic.List[object]]::new()
    foreach ($row in $state.Plan.Keep) {
        $file = Join-Path $carryDirectory (Get-GraphCarryOverFileName -GraphId $row.GraphId)
        $result = Invoke-GraphTool -Tool 'save_graph' -Arguments @{ outputPath = $file; graphId = $row.GraphId } -Uri $uri
        if ($result.Ok -and (Test-Path -LiteralPath $file -PathType Leaf)) {
            $saved.Add([PSCustomObject]@{ GraphId = $row.GraphId; Source = $row.Source; Kind = $row.Kind; File = $file })
        }
        elseif ($result.Ok) { $failures.Add("save $($row.GraphId): reported ok but $file does not exist") }
        else { $failures.Add("save $($row.GraphId): $($result.Error)") }
    }

    try {
        $manifest = @($saved | ForEach-Object { [ordered]@{ graphId = $_.GraphId; file = $_.File; source = $_.Source; kind = $_.Kind } })
        ConvertTo-Json -InputObject $manifest -Depth 4 |
            Set-Content -LiteralPath (Join-Path $carryDirectory 'manifest.json') -Encoding utf8
    }
    catch { $failures.Add("writing the manifest: $($_.Exception.Message)") }

    $state.Saved = $saved.ToArray()
    $state.Failures = $failures.ToArray()
    $state.Stage = 'saved'
    return $state
}

function Restore-GraphCarryOver {
    # After the new server listens: load each saved file under its ORIGINAL id, in the
    # plan's order, so the previous current loads last and stays current.
    param([Parameter(Mandatory)][object]$State)

    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($f in @($State.Failures)) { $failures.Add($f) }
    $loaded = [System.Collections.Generic.List[object]]::new()

    foreach ($row in @($State.Saved)) {
        $result = Invoke-GraphTool -Tool 'load_graph' -Arguments @{ path = $row.File; graphId = $row.GraphId } -Uri $State.Uri
        if ($result.Ok) { $loaded.Add($row) }
        else { $failures.Add("load $($row.GraphId): $($result.Error)") }
    }

    # Read back rather than assumed: "current is now X" is the claim the report makes.
    $after = Invoke-GraphTool -Tool 'list_graphs' -Uri $State.Uri
    if ($after.Ok) { $State.CurrentAfter = [string](Get-GraphCarryOverProperty $after.Result 'current') }
    else { $failures.Add("list_graphs after the loads: $($after.Error)") }

    $State.Loaded = $loaded.ToArray()
    $State.Failures = $failures.ToArray()
    $State.Stage = 'loaded'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$buildDir = Join-Path $Root "build-$stamp"
$currentLink = Join-Path $Root 'current'

# ---- Publish to a directory nothing is holding open ------------------------

Write-Host "publishing -> $buildDir" -ForegroundColor Cyan

# Output is captured, not discarded: a swallowed publish failure reports only that something
# went wrong, and the usual cause here (MSB3021, a locked file) names the exact path holding
# things up. Hiding that turns a one-line diagnosis into a hunt.
# Redirect bin\, and ONLY bin\. `dotnet publish` normally copies through the project's own
# bin\Release, which is the directory a running server has open -- so without this, publishing
# while a server is live fails on the very lock this script exists to avoid.
#
# obj\ is deliberately left alone. Redirecting BaseIntermediateOutputPath stops MSBuild
# excluding the project's ORIGINAL obj\ from the default source glob, so the AssemblyInfo.cs
# in each gets compiled and every assembly attribute collides (CS0579). The lock is on bin\;
# moving obj\ solves nothing and breaks the build.
#
# Forward slashes: a trailing backslash inside a quoted native argument is a known mangler.
$isolatedBin = ($buildDir -replace '\\', '/') + '/_bin/'
$publishLog = & dotnet publish $Project -c Release -o $buildDir --nologo -v quiet `
    "--property:BaseOutputPath=$isolatedBin" 2>&1
if ($LASTEXITCODE -ne 0) {
    $detail = ($publishLog | Select-Object -Last 12) -join [Environment]::NewLine
    $hint = if ($detail -match 'MSB3021|being used by another process') {
        [Environment]::NewLine + [Environment]::NewLine +
        'A running server is holding the project''s own bin directory open. Rotation only ' +
        'helps when the server runs from a rotated build: start it from the "current" ' +
        'junction rather than bin\Release, and this stops happening.'
    }
    else { '' }

    throw "publish failed; $currentLink still points at the previous build.$([Environment]::NewLine)$detail$hint"
}

$exeName = [System.IO.Path]::GetFileNameWithoutExtension($Project)
$published = @(Get-ChildItem -LiteralPath $buildDir -Filter '*.exe' -ErrorAction SilentlyContinue)
$exe = $published | Where-Object { $_.BaseName -eq $exeName } | Select-Object -First 1
if ($null -eq $exe) { $exe = $published | Select-Object -First 1 }
if ($null -eq $exe) { throw "no executable in $buildDir; refusing to repoint $currentLink" }

# Prove the new build runs BEFORE anything points at it. Swapping to a binary that cannot
# start is how a working server becomes a broken one during a routine rebuild.
& $exe.FullName --help *> $null
if ($LASTEXITCODE -ne 0) { throw "$($exe.Name) --help failed in $buildDir; refusing to repoint $currentLink" }
Write-Host "  smoke test ok: $($exe.Name)" -ForegroundColor DarkGray

# ---- Repoint the stable path -----------------------------------------------

# A junction, not a copy: repointing is atomic enough and instant, and the running server
# holds the real directory rather than the link, so it is undisturbed.
if (Test-Path -LiteralPath $currentLink) { (Get-Item -LiteralPath $currentLink).Delete() }
New-Item -ItemType Junction -Path $currentLink -Target $buildDir | Out-Null
Write-Host "  current -> $(Split-Path $buildDir -Leaf)" -ForegroundColor Green

# ---- Restart, when we own the process --------------------------------------

if ($ProcessName) {
    # Scoped to servers running from under $Root, and to HTTP ones. Stop-Process by NAME alone
    # would also kill stdio servers belonging to other sessions -- and stdio is never
    # reconnected automatically, so those clients lose their tools permanently with no error
    # and no way back short of restarting the session. A rebuild must not be able to do that.
    # Assigned, never @(...)-wrapped: Get-ServerProcess comma-wraps its return, so wrapping
    # again yields ONE element holding the real array. PowerShell then member-enumerates it,
    # $_.CommandLine returns every command line at once, and the stdio/HTTP split silently
    # classifies all servers as one. House rule 1, and it bit here exactly as documented.
    $candidates = Get-ServerProcess -Name $ProcessName -Under $Root
    $stdio = @($candidates | Where-Object { $_.CommandLine -notmatch '--http' })
    $ours = @($candidates | Where-Object { $_.CommandLine -match '--http' })

    foreach ($s in $stdio) {
        Write-Warning ("pid $($s.ProcessId) is a stdio server owned by a client, not by this " +
            'script. Leaving it alone: stopping it would strip that session of its tools with ' +
            'no reconnect. Its client will pick up the new build on its next start.')
    }

    # The port is read from --port in -ServerArgument, here rather than at the listener wait,
    # because the carry-over needs it before the stop.
    $port = $null
    for ($i = 0; $i -lt $ServerArgument.Count - 1; $i++) {
        if ($ServerArgument[$i] -eq '--port') { $port = [int]$ServerArgument[$i + 1] }
    }

    # Graphs live in the server's memory, so stopping it used to drop every one of them
    # without a word. Saved here, loaded back after the listener wait, reported at the end.
    # Gated on the running server's tool list rather than a switch: server_rotate passes
    # this script a fixed argument list per server, and a server that serves list_graphs is
    # exactly the case where there is something to lose. Only when we are about to stop
    # something -- no HTTP server of ours, no drop.
    $carry = $null
    if ($ours.Count -gt 0 -and $null -ne $port) {
        try { $carry = Save-GraphCarryOver -Port $port }
        catch {
            $carry = [PSCustomObject]@{
                Port = $port; Uri = "http://127.0.0.1:$port/"; Directory = $carryDirectory; Plan = $null; HeldCount = 0
                Saved = @(); Loaded = @(); Failures = @("carry-over aborted before the stop: $($_.Exception.Message)")
                CurrentAfter = $null; Stage = 'listed'; Note = 'the save step threw'
            }
        }
    }

    foreach ($p in $ours) {
        Stop-Process -Id $p.ProcessId -Force
        Write-Host "  stopped pid $($p.ProcessId)" -ForegroundColor DarkGray
    }

    # Started the way Ensure-McpServer.ps1 starts it, logs included, so the two scripts cannot
    # disagree about how a server comes up -- and so a server that dies on start leaves a reason
    # somewhere. Until 2026-09-01 this launch had no redirect, and a death would have been silent.
    $logDir = Join-Path ([System.IO.Path]::GetTempPath()) 'Janet'
    if (-not (Test-Path -LiteralPath $logDir -PathType Container)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }

    $launch = @{
        FilePath               = Join-Path $currentLink $exe.Name
        PassThru               = $true
        WindowStyle            = 'Hidden'
        RedirectStandardOutput = Join-Path $logDir "$ProcessName.out.log"
        RedirectStandardError  = Join-Path $logDir "$ProcessName.err.log"
    }
    if ($ServerArgument.Count -gt 0) { $launch.ArgumentList = $ServerArgument }

    $started = Start-Process @launch

    # Wait until it LISTENS, not merely until it exists. This used to return the moment the
    # process was spawned, and the port stayed empty for up to three seconds after that: a client
    # call inside that window failed with "Unable to connect", the client's reconnect backoff then
    # outlasted a human's patience, and the whole thing read as the new server having died. It had
    # not -- measured 2026-09-01, the rotated pid was serving 16 s later every time. The port is
    # read from --port in -ServerArgument; without one there is nothing to poll, and that is said
    # rather than guessed at.
    if ($null -eq $port) {
        Write-Host "  started pid $($started.Id) from current (no --port in -ServerArgument, so not waiting for it to listen)" -ForegroundColor Green
    }
    else {
        $listening = $false
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 200
            if ($started.HasExited) { break }
            if ((Get-ListenerPid -TcpPort $port) -eq $started.Id) { $listening = $true; break }
        }

        if ($listening) {
            Write-Host "  started pid $($started.Id) from current, listening on $port" -ForegroundColor Green
            if ($null -ne $carry -and $carry.Stage -eq 'saved') {
                try { Restore-GraphCarryOver -State $carry }
                catch {
                    $carry.Failures = @($carry.Failures) + "carry-over aborted during the loads: $($_.Exception.Message)"
                    $carry.Stage = 'loaded'
                }
            }
        }
        elseif ($started.HasExited) {
            if ($null -ne $carry -and $carry.Stage -eq 'saved') {
                Write-Host "  graph carry-over: NOT loaded, the new server died; $(@($carry.Saved).Count) saved graphs are in $carryDirectory" -ForegroundColor Yellow
            }
            throw ("pid $($started.Id) exited (code $($started.ExitCode)) before listening on $port. " +
                "See $logDir\$ProcessName.err.log. $currentLink already points at the new build; " +
                'repoint it at the previous build-* directory to roll back.')
        }
        else {
            Write-Warning ("pid $($started.Id) is alive but nothing owns port $port after 15 s. Clients " +
                "will fail until it listens; see $logDir\$ProcessName.out.log.")
            if ($null -ne $carry -and $carry.Stage -eq 'saved') {
                $carry.Stage = 'not-loaded'
                $carry.Note = "nothing listened on $port within 15 s"
            }
        }
    }

    Write-Host '  attached sessions reconnect on their own (HTTP: five attempts, 1s doubling).' -ForegroundColor DarkGray
}
else {
    Write-Host '  no -ProcessName: the client owns this server. New sessions pick up the new' -ForegroundColor DarkGray
    Write-Host '  build through the junction; running ones keep the build they started with.' -ForegroundColor DarkGray
}

# ---- Prune, skipping anything still in use ---------------------------------

$builds = @(Get-ChildItem -LiteralPath $Root -Directory -Filter 'build-*' | Sort-Object Name -Descending)
$stale = @($builds | Select-Object -Skip $Keep)

foreach ($old in $stale) {
    # Checked BEFORE deleting, never delete-then-catch. Remove-Item -Recurse deletes what it
    # can and only then fails, so a build with a live server loses every file the process has
    # not currently mapped -- measured: 4 of 11, including the .deps.json the runtime needs to
    # resolve an assembly it has not loaded yet. The server survives until it reaches for one
    # of them. Catching the exception afterwards reports "kept" over a directory already
    # gutted, which is the confidently-wrong failure this repo keeps writing rules about.
    $users = Get-ServerProcess -Under $old.FullName
    if ($users.Count -gt 0) {
        Write-Host "  kept $($old.Name) (in use by pid $($users.ProcessId -join ', '))" -ForegroundColor DarkGray
        continue
    }

    try { Remove-Item -LiteralPath $old.FullName -Recurse -Force -ErrorAction Stop }
    catch { Write-Warning "could not prune $($old.Name): $($_.Exception.Message)" }
}

# ---- Refresh global tools built from the same source ------------------------

foreach ($project in $ToolProject) {
    if (-not (Test-Path -LiteralPath $project)) { throw "Tool project not found: $project" }

    $packageDir = Join-Path $Root 'pkg'
    $packLog = & dotnet pack $project -c Release -o $packageDir --nologo -v quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "pack failed for $project :$([Environment]::NewLine)$(($packLog | Select-Object -Last 8) -join [Environment]::NewLine)"
    }

    $packageId = [System.IO.Path]::GetFileNameWithoutExtension($project)

    # Uninstall then install: `dotnet tool update` sees the same version number and does
    # nothing, which is precisely how a stale tool survives a rebuild.
    & dotnet tool uninstall -g $packageId *> $null
    $installLog = & dotnet tool install -g $packageId --add-source $packageDir 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "install failed for $packageId :$([Environment]::NewLine)$(($installLog | Select-Object -Last 8) -join [Environment]::NewLine)"
    }

    Write-Host "  reinstalled global tool $packageId" -ForegroundColor Green
}

Write-Host ''
Write-Host "Client config points at the junction, and never changes:" -ForegroundColor Cyan
Write-Host "  $(Join-Path $currentLink $exe.Name)"
Write-Host ''

# Last, so it is the tail of the log: server_rotate's logTail shows the final 15 lines, and
# a carry-over buried above the prune and junction lines is a drop nobody reads about.
if ($null -ne $carry) {
    $color = if (@($carry.Failures).Count -gt 0 -or $carry.Stage -eq 'not-loaded') { 'Yellow' } else { 'Cyan' }
    foreach ($line in (Format-GraphCarryOverReport -State $carry)) { Write-Host $line -ForegroundColor $color }
}
