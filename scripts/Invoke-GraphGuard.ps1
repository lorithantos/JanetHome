<#
.SYNOPSIS
    PreToolUse hook: answers a Grep inside a git working tree through razorgraph's grep
    tool, and refuses a Bash or PowerShell search of files inside a git working tree with
    that call ready to paste.

.DESCRIPTION
    Graph first, at the controller's own keyboard. Invoke-DelegationGuard.ps1 gates the
    prompt a subagent is handed; this gates the search the session itself is about to run.
    It was written on 2026-09-05, after four greps for callers, implementers and blast
    radius ran against C# in one session while two prose rules and a skill said "graph
    first". The incident is on note.subagent-delegation.

    UNTIL 2026-10-11 IT DENIED AND ADVISED, and agents cleared it with one throwaway
    razorgraph call, which bought 12 tool calls of clearance, and then grepped anyway. A
    gate whose price is one call is a toll. So the Grep tool is no longer refused: it is
    ANSWERED. razorgraph's grep returns the graph's name and literal hits plus a full
    regex scan of the disk with the graph's lines removed, and this hook runs that call
    and hands the results back inside the denial. The agent gets its answer in the same
    step, from the graph, and there is nothing left to clear.

      Grep            inside a git working tree, ANY file type: answered by
                      mcp__razorgraph__grep. Inputs are mapped faithfully or not at
                      all -- multiline, context lines (-A/-B/-C/context), offset, a glob
                      with a slash, ** or braces, a type with no glob in the table, or a
                      parameter this hook does not know all let the Grep through, since
                      razorgraph cannot do them and a wrong answer is worse than grep.
                      Outside any git working tree it passes untouched.
      Bash/PowerShell denied when a search in the command -- grep, egrep, fgrep, rg, ag,
                      ack, git grep, Select-String/sls, findstr, find -exec grep, xargs
                      grep -- reads FILES inside a git working tree, any file type
                      ("PowerShell and bash shouldn't be doing searches", Lori,
                      2026-10-12; until then only a command naming .cs or src\ was).
                      What a search reads: its file operands, a file fed in with <, the
                      files an upstream cat/head/Get-Content (or for Select-String,
                      Get-ChildItem) reads, a find's paths, or for a recursive search
                      with no operand its directory. A search of another command's
                      output (git diff | grep, dotnet test | Select-String) passes, as
                      does one whose files all lie outside any git working tree. An
                      operand this cannot resolve (a variable, an expression) lets the
                      command run with one line of additionalContext. The denial
                      carries a paste-ready mcp__razorgraph__grep call. Shell searches
                      are never executed here.
      anything else   passes.

    WHEN RAZORGRAPH CANNOT ANSWER. Unreachable (connection refused or timed out): run
    startup-manifest.json's razorGraphServer entry (Ensure-McpServer.ps1 with razorgraph's
    args) and retry once. If the server is not running and cannot
    be started, the hook runs the search ITSELF -- git's file list, the same file-name
    glob, binaries and files over 2 MB skipped, .NET regex with a 1-second match timeout
    -- and denies with those hits, saying the graph was down. Every other failure (isError,
    an invalid regex, an exception) lets the original Grep through with one line of
    additionalContext saying why. Never permissionDecision 'allow': it would also bypass
    the permission prompt, silently pre-approving the call (see the delegation guard).

.PARAMETER InputJson
    Hook payload, for testing. Normally omitted -- the real invocation gets it on stdin.
    Pipe input does not bind to a [string] parameter, so tests must pass -InputJson.

.PARAMETER McpCall
    Runs razorgraph's grep. Takes the argument hashtable and returns Invoke-McpTool.ps1's
    JSON envelope. Injectable so tests never need a live server.

.PARAMETER EnsureServer
    Starts razorgraph when it is down. Returns Ensure-McpServer.ps1's JSON envelope, run
    through the manifest's razorGraphServer entry. Injectable for the same reason.

.OUTPUTS
    A PreToolUse deny carrying the answer or the remediation, a PreToolUse
    additionalContext note, or nothing when the call is fine.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-GraphGuard.ps1" -InputJson '{"tool_name":"Grep","tool_input":{"pattern":"ThreadStore","path":"C:\\repos\\lorithantos\\JanetHome\\src"},"cwd":"C:\\repos\\lorithantos\\JanetHome"}'

.NOTES
    Wired via BOTH .claude\settings.json and ~\.claude\settings.json as a PreToolUse
    hook on Grep|Bash|PowerShell. The project-level file arms it when the project dir
    is JanetHome; the user-level file arms it by absolute path everywhere else.

    $env:JANET_GRAPH_GUARD = 'off' disables it.

    Exits 0 always -- a hook that crashes must not block unrelated work.
#>
[CmdletBinding()]
param(
    [string]$InputJson,

    [scriptblock]$McpCall = {
        param([hashtable]$Arguments)
        # Per HTTP request, and Invoke-McpTool makes three: the hook timeout in both
        # settings files is sized against that, not against 10.
        & (Join-Path $PSScriptRoot 'Invoke-McpTool.ps1') -Tool grep -Arguments $Arguments -TimeoutSec 10
    },

    [scriptblock]$EnsureServer = {
        # The startup manifest's razorgraph entry, run with its own args: since
        # 2026-10-22 that entry is the only place razorgraph's launch defaults live.
        . (Join-Path $PSScriptRoot 'lib\RunArguments.ps1')
        Invoke-ManifestRunEntry -RepoRoot (Split-Path $PSScriptRoot -Parent) -CaptureAs 'razorGraphServer'
    }
)

Set-StrictMode -Version Latest

$script:ReasonCap = 12000
$script:DefaultLimit = 50
$script:ListLimit = 1000
$script:ScanBudgetSec = 8
$script:Unreachable = '(?i)cannot reach|timed out|timeout|refused|No connection could be made|canceled|cancelled'

# ripgrep type -> razorgraph glob. razorgraph takes ONE file-name wildcard, so a type is
# mapped to its primary extension; anything not listed lets the Grep through.
$script:TypeGlobs = @{
    cs = '*.cs'; js = '*.js'; ts = '*.ts'; py = '*.py'; json = '*.json'; md = '*.md'
    ps = '*.ps1'; ps1 = '*.ps1'; go = '*.go'; rust = '*.rs'; java = '*.java'
    html = '*.html'; css = '*.css'; xml = '*.xml'; toml = '*.toml'; lua = '*.lua'
    sql = '*.sql'; txt = '*.txt'
}

$script:KnownGrepKeys = @(
    'pattern', 'path', 'glob', 'type', 'output_mode', '-i', '-n',
    '-A', '-B', '-C', 'context', 'head_limit', 'offset', 'multiline')

function Get-Prop {
    param($Object, [string]$Name)
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
}

# A property as an array. Returns a comma-wrapped array: assign it bare.
function Get-List {
    param($Object, [string]$Name)
    $value = Get-Prop $Object $Name
    if ($null -eq $value) { return ,@() }
    return ,@($value)
}

function Format-OneLine {
    param([string]$Text, [int]$Max = 300)
    $flat = ([string]$Text -replace '\s+', ' ').Trim()
    if ($flat.Length -gt $Max) { $flat = $flat.Substring(0, $Max) + '...' }
    return $flat
}

# ConvertFrom-Json turns an ISO date into a DateTime on 7 and leaves a string on 5.1.
function Format-Stamp {
    param($Value)
    if ($Value -is [datetime]) { return $Value.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ') }
    return [string]$Value
}

function Write-Deny {
    param([string]$Reason)
    [PSCustomObject]@{
        hookSpecificOutput = [PSCustomObject]@{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'deny'
            permissionDecisionReason = $Reason
        }
    } | ConvertTo-Json -Depth 4 -Compress
}

# Lets the call through and says why. NO permissionDecision: 'allow' would carry the text
# and also skip the permission prompt for this call.
function Write-PassContext {
    param([string]$Message)
    [PSCustomObject]@{
        hookSpecificOutput = [PSCustomObject]@{
            hookEventName     = 'PreToolUse'
            additionalContext = $Message
        }
    } | ConvertTo-Json -Depth 4 -Compress
}

function Test-InsideGitWorkTree {
    param([string]$Directory)
    $answer = & git -C $Directory rev-parse --is-inside-work-tree 2>$null
    return ($LASTEXITCODE -eq 0 -and ([string]$answer).Trim() -eq 'true')
}

# razorgraph's glob matches the file NAME only, so a glob carrying a directory, a **, a
# brace set, a class or a negation cannot be passed on without changing its meaning.
function Test-PlainGlob {
    param([string]$Glob)
    return ($Glob -notmatch '[\\/{}\[\]!]' -and $Glob -notmatch '\*\*')
}

function Resolve-FullPath {
    param([string]$Path, [string]$BaseDir)
    if (-not [System.IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $BaseDir $Path }
    return [System.IO.Path]::GetFullPath($Path)
}

# Maps a Grep tool_input onto razorgraph grep arguments, or says why it cannot.
# Pass set means the Grep goes through; Announce means say so in additionalContext.
function Get-GrepRequest {
    param($ToolInput, [string]$BaseDir)

    $request = [PSCustomObject]@{
        Pass = $null; Announce = $false; Arguments = $null
        Mode = $null; HeadLimit = 0; Folder = $null
    }

    $pattern = [string](Get-Prop $ToolInput 'pattern')
    if (-not $pattern) { $request.Pass = 'no pattern'; return $request }

    $unknown = @($ToolInput.PSObject.Properties.Name | Where-Object { $script:KnownGrepKeys -notcontains $_ })
    if ($unknown.Count -gt 0) {
        $request.Pass = "Grep parameter(s) this hook cannot map: $($unknown -join ', ')"
        $request.Announce = $true
        return $request
    }

    if ((Get-Prop $ToolInput 'multiline') -eq $true) { $request.Pass = 'multiline'; return $request }
    foreach ($key in @('-A', '-B', '-C', 'context')) {
        $value = Get-Prop $ToolInput $key
        if ($null -ne $value -and [int]$value -gt 0) { $request.Pass = "context lines ($key)"; return $request }
    }
    $skip = Get-Prop $ToolInput 'offset'
    if ($null -ne $skip -and [int]$skip -gt 0) { $request.Pass = 'offset'; return $request }

    # Grep's own default is files_with_matches.
    $mode = [string](Get-Prop $ToolInput 'output_mode')
    if (-not $mode) { $mode = 'files_with_matches' }
    if (@('content', 'files_with_matches', 'count') -notcontains $mode) { $request.Pass = "output_mode $mode"; return $request }

    $glob = [string](Get-Prop $ToolInput 'glob')
    if ($glob -and -not (Test-PlainGlob $glob)) { $request.Pass = "glob $glob is not a plain file-name wildcard"; return $request }

    $type = [string](Get-Prop $ToolInput 'type')
    if ($type) {
        if ($glob) { $request.Pass = 'glob and type together'; return $request }
        if (-not $script:TypeGlobs.ContainsKey($type)) { $request.Pass = "type $type has no glob mapping"; return $request }
        $glob = $script:TypeGlobs[$type]
    }

    $path = [string](Get-Prop $ToolInput 'path')
    $target = Resolve-FullPath -Path $(if ($path) { $path } else { $BaseDir }) -BaseDir $BaseDir
    if (Test-Path -LiteralPath $target -PathType Container) {
        $folder = $target
    }
    elseif (Test-Path -LiteralPath $target -PathType Leaf) {
        if ($glob) { $request.Pass = 'a file path with a glob or type'; return $request }
        $folder = Split-Path -Parent $target
        $glob = Split-Path -Leaf $target
    }
    else {
        $request.Pass = 'path does not exist'
        return $request
    }

    if (-not (Test-InsideGitWorkTree $folder)) { $request.Pass = 'outside any git working tree'; return $request }

    $headLimit = Get-Prop $ToolInput 'head_limit'
    $limit = $script:ListLimit
    if ($mode -eq 'content') {
        $limit = $script:DefaultLimit
        if ($null -ne $headLimit) { $limit = if ([int]$headLimit -gt 0) { [int]$headLimit } else { $script:ListLimit } }
    }

    # Grep is case-SENSITIVE unless -i; razorgraph defaults the other way, so always say.
    $arguments = [ordered]@{
        pattern    = $pattern
        folder     = $folder
        ignoreCase = ((Get-Prop $ToolInput '-i') -eq $true)
        limit      = $limit
    }
    if ($glob) { $arguments['glob'] = $glob }

    $request.Arguments = $arguments
    $request.Mode = $mode
    $request.HeadLimit = if ($null -ne $headLimit) { [int]$headLimit } else { 0 }
    $request.Folder = $folder
    return $request
}

# Runs razorgraph grep. Status is ok, unreachable (refused or timed out) or failed.
function Invoke-RazorGrep {
    param([scriptblock]$Call, $Arguments)

    $hashArgs = @{}
    foreach ($key in $Arguments.Keys) { $hashArgs[$key] = $Arguments[$key] }

    try {
        $text = & $Call $hashArgs | Out-String
    }
    catch {
        $message = $_.Exception.Message
        $status = if ($message -match $script:Unreachable) { 'unreachable' } else { 'failed' }
        return [PSCustomObject]@{ Status = $status; Result = $null; Message = $message }
    }

    if ([string]::IsNullOrWhiteSpace($text)) {
        return [PSCustomObject]@{ Status = 'failed'; Result = $null; Message = 'no output from the MCP call' }
    }

    $envelope = $text | ConvertFrom-Json
    if ((Get-Prop $envelope 'ok') -ne $true) {
        # 'error' is the transport or protocol failing; 'result' carries a tool's isError text.
        $failure = [string](Get-Prop $envelope 'error')
        if ($failure) {
            $status = if ($failure -match $script:Unreachable) { 'unreachable' } else { 'failed' }
            return [PSCustomObject]@{ Status = $status; Result = $null; Message = $failure }
        }
        return [PSCustomObject]@{ Status = 'failed'; Result = $null; Message = [string](Get-Prop $envelope 'result') }
    }

    $result = Get-Prop $envelope 'result'
    if ($null -eq (Get-Prop $result 'disk')) {
        return [PSCustomObject]@{ Status = 'failed'; Result = $null; Message = 'the answer had no disk section' }
    }
    return [PSCustomObject]@{ Status = 'ok'; Result = $result; Message = $null }
}

# Up is true when a server is listening: ok, or running but stale.
function Invoke-EnsureServer {
    param([scriptblock]$Call)

    try {
        $text = & $Call | Out-String
        $envelope = $text | ConvertFrom-Json
    }
    catch {
        return [PSCustomObject]@{ Up = $false; Message = "starting razorgraph failed: $($_.Exception.Message)" }
    }

    $up = ((Get-Prop $envelope 'ok') -eq $true) -or ((Get-Prop $envelope 'stale') -eq $true)
    $failure = [string](Get-Prop $envelope 'error')
    $message = if ($failure) { $failure } else { "state $(Get-Prop $envelope 'state'): $(Get-Prop $envelope 'note')" }
    return [PSCustomObject]@{ Up = $up; Message = $message }
}

# The hook's own search, for when razorgraph is down and will not start. Mirrors
# razorgraph's disk section as closely as is cheap, and returns a result of that shape.
# Throws on an invalid regex; the caller lets the Grep through.
function Invoke-LocalScan {
    param($Arguments)

    $options = [System.Text.RegularExpressions.RegexOptions]::CultureInvariant
    if ($Arguments['ignoreCase']) { $options = $options -bor [System.Text.RegularExpressions.RegexOptions]::IgnoreCase }
    $regex = New-Object System.Text.RegularExpressions.Regex($Arguments['pattern'], $options, [TimeSpan]::FromSeconds(1))

    $folder = $Arguments['folder']
    $matcher = $null
    if ($Arguments.Contains('glob')) {
        $matcher = New-Object System.Management.Automation.WildcardPattern($Arguments['glob'], 'IgnoreCase')
    }

    # git writes UTF-8 names; read them as UTF-8 and put the console back after.
    $previousEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
        $listing = & git -C $folder ls-files --cached --others --exclude-standard -z 2>$null
    }
    finally {
        [Console]::OutputEncoding = $previousEncoding
    }
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed in $folder" }
    $names = @((@($listing) -join "`n") -split "`0" | Where-Object { $_ })

    $hits = New-Object System.Collections.Generic.List[object]
    $timedOut = New-Object System.Collections.Generic.List[string]
    $total = 0; $scanned = 0; $skipped = 0; $stopped = $null
    $clock = [System.Diagnostics.Stopwatch]::StartNew()

    foreach ($name in $names) {
        if ($clock.Elapsed.TotalSeconds -gt $script:ScanBudgetSec) {
            $stopped = "stopped after $($script:ScanBudgetSec)s with $scanned files read; totals are partial"
            break
        }
        if ($null -ne $matcher -and -not $matcher.IsMatch([System.IO.Path]::GetFileName($name))) { continue }

        $full = Join-Path $folder $name
        $info = New-Object System.IO.FileInfo($full)
        if (-not $info.Exists) { continue }
        if ($info.Length -gt 2MB) { $skipped++; continue }

        $bytes = [System.IO.File]::ReadAllBytes($full)
        $probe = [Math]::Min($bytes.Length, 8192)
        if ($probe -gt 0 -and [Array]::IndexOf($bytes, [byte]0, 0, $probe) -ge 0) { $skipped++; continue }
        $scanned++

        $reader = New-Object System.IO.StreamReader((New-Object System.IO.MemoryStream(, $bytes)), [System.Text.Encoding]::UTF8, $true)
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }

        $shown = $name -replace '/', '\'
        $lines = $text -split "`n"
        try {
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $line = $lines[$i].TrimEnd("`r")
                if (-not $regex.IsMatch($line)) { continue }
                $total++
                if ($hits.Count -lt $Arguments['limit']) {
                    $trimmed = $line.Trim()
                    if ($trimmed.Length -gt 200) { $trimmed = $trimmed.Substring(0, 200) }
                    $hits.Add([PSCustomObject]@{ path = $shown; line = $i + 1; text = $trimmed })
                }
            }
        }
        catch [System.Text.RegularExpressions.RegexMatchTimeoutException] {
            $timedOut.Add($shown)
        }
    }

    return [PSCustomObject]@{
        folder = $folder
        graph  = [PSCustomObject]@{ reason = 'razorgraph is down'; hits = @() }
        disk   = [PSCustomObject]@{
            returned       = $hits.Count
            totalMatches   = $total
            truncated      = ($total -gt $hits.Count)
            filesScanned   = $scanned
            filesSkipped   = $skipped
            alreadyInGraph = 0
            timedOutFiles  = $timedOut.ToArray()
            stoppedEarly   = $stopped
            hits           = $hits.ToArray()
        }
    }
}

function Format-GraphHit {
    param($Hit)
    $name = Format-OneLine -Text ([string](Get-Prop $Hit 'name')) -Max 120
    $line = Get-Prop $Hit 'line'
    $where = "$(Get-Prop $Hit 'path'):$(if ($null -ne $line) { $line } else { '?' })"
    $text = "$where  $(Get-Prop $Hit 'nodeType') $name  ($(Get-Prop $Hit 'nodeId'))"
    $from = [string](Get-Prop $Hit 'fromId')
    if ($from -and $from -ne [string](Get-Prop $Hit 'nodeId')) { $text += " from $from" }
    return $text
}

function Format-DiskHit {
    param($Hit)
    return "$(Get-Prop $Hit 'path'):$(Get-Prop $Hit 'line'): $(Get-Prop $Hit 'text')"
}

function Format-Call {
    param($Arguments, [long]$Offset, [long]$DiskOffset)
    $call = [ordered]@{}
    foreach ($key in $Arguments.Keys) { $call[$key] = $Arguments[$key] }
    if ($Offset -gt 0) { $call['offset'] = $Offset }
    if ($DiskOffset -gt 0) { $call['diskOffset'] = $DiskOffset }
    return 'mcp__razorgraph__grep ' + ([PSCustomObject]$call | ConvertTo-Json -Compress)
}

# Renders an answer -- razorgraph's, or the local scan's when DownNote is set -- capped
# at ReasonCap characters, trimming hits and saying how many.
function Format-GrepAnswer {
    param($Request, $Result, [string]$DownNote)

    $arguments = $Request.Arguments
    $graph = Get-Prop $Result 'graph'
    $disk = Get-Prop $Result 'disk'
    $graphHits = Get-List $graph 'hits'
    $diskHits = Get-List $disk 'hits'
    $graphId = [string](Get-Prop $graph 'graphId')
    $local = [bool]$DownNote

    $graphTotal = [long](Get-Prop $graph 'totalMatches')
    if ($graphTotal -lt $graphHits.Count) { $graphTotal = $graphHits.Count }
    $diskTotal = [long](Get-Prop $disk 'totalMatches')
    $graphTruncated = ((Get-Prop $graph 'truncated') -eq $true)
    $diskTruncated = ((Get-Prop $disk 'truncated') -eq $true)

    $head = New-Object System.Collections.Generic.List[string]
    if ($local) { $head.Add($DownNote) }
    else { $head.Add('Answered by mcp__razorgraph__grep in place of Grep -- these are the results, not a refusal; do not rerun the Grep.') }

    $search = "Search: pattern '$($arguments['pattern'])', ignoreCase=$($arguments['ignoreCase'].ToString().ToLowerInvariant())"
    if ($arguments.Contains('glob')) { $search += ", glob '$($arguments['glob'])'" }
    $head.Add("$search, output_mode $($Request.Mode)")
    $head.Add("Folder: $($Request.Folder) (paths are relative to it)")
    if (-not $local) {
        if ($graphId) { $head.Add("Graph: $graphId, built $(Format-Stamp (Get-Prop $graph 'builtAt'))") }
        else { $head.Add("Graph: none -- $(Get-Prop $graph 'reason'). Text hits only.") }
    }

    $diskLine = "$diskTotal disk match(es) in $(Get-Prop $disk 'filesScanned') files scanned"
    $already = [long](Get-Prop $disk 'alreadyInGraph')
    if (-not $local) { $diskLine += ", $already already reported by the graph" }
    $skippedFiles = [long](Get-Prop $disk 'filesSkipped')
    if ($skippedFiles -gt 0) { $diskLine += ", $skippedFiles skipped (binary or over 2 MB)" }
    $notes = New-Object System.Collections.Generic.List[string]
    $timedOutFiles = Get-List $disk 'timedOutFiles'
    if ($timedOutFiles.Count -gt 0) { $notes.Add("The pattern timed out in $($timedOutFiles -join ', '); their remaining matches are missing.") }
    $stopped = [string](Get-Prop $disk 'stoppedEarly')
    if ($stopped) { $notes.Add("The scan $stopped.") }

    # Sections and their candidate lines, in order; trimming eats from the end.
    $sections = New-Object System.Collections.Generic.List[object]
    $listed = $false
    if ($Request.Mode -eq 'content') {
        if ($graphId) {
            $sections.Add([PSCustomObject]@{
                Title = { param($shown) "Graph hits (names and literals): $shown of $graphTotal" }.GetNewClosure()
                Lines = @($graphHits | ForEach-Object { '  ' + (Format-GraphHit $_) })
                Graph = $true
            })
        }
        $sections.Add([PSCustomObject]@{
            Title = { param($shown) "Disk hits: $shown shown; $diskLine" }.GetNewClosure()
            Lines = @($diskHits | ForEach-Object { '  ' + (Format-DiskHit $_) })
            Graph = $false
        })
    }
    else {
        $listed = $true
        $perFile = [ordered]@{}
        foreach ($hit in @($graphHits) + @($diskHits)) {
            $path = [string](Get-Prop $hit 'path')
            if (-not $perFile.Contains($path)) { $perFile[$path] = New-Object System.Collections.Generic.HashSet[string] }
            [void]$perFile[$path].Add([string](Get-Prop $hit 'line'))
        }
        $paths = @($perFile.Keys | Sort-Object)
        $entries = @($paths | ForEach-Object { if ($Request.Mode -eq 'count') { "  ${_}:$($perFile[$_].Count)" } else { "  $_" } })
        if ($Request.HeadLimit -gt 0 -and $entries.Count -gt $Request.HeadLimit) {
            $notes.Add("head_limit $($Request.HeadLimit): $($entries.Count - $Request.HeadLimit) more file(s) not shown.")
            $entries = @($entries[0..($Request.HeadLimit - 1)])
        }
        $what = if ($Request.Mode -eq 'count') { 'Matching lines per file' } else { 'Files with matches' }
        $graphPart = if ($graphId) { "$graphTotal graph match(es), " } else { '' }
        $sections.Add([PSCustomObject]@{
            Title = { param($shown) "${what}: $shown of $($paths.Count) file(s), from $graphPart$diskLine" }.GetNewClosure()
            Lines = $entries
            Graph = $false
        })
        if ($graphTruncated -or $diskTruncated) {
            $notes.Add("PARTIAL: only the first $($arguments['limit']) matches per section were read (graph $($graphHits.Count) of $graphTotal, disk $($diskHits.Count) of $diskTotal), so this list is incomplete.")
        }
    }

    # Budget: everything but the hit lines, with room for the trim note and the call.
    $fixed = ($head -join "`n").Length + ($notes -join "`n").Length + 400
    foreach ($section in $sections) { $fixed += (& $section.Title 99999).Length + 1 }
    $fixed += (Format-Call -Arguments $arguments -Offset 99999 -DiskOffset 99999).Length
    $budget = $script:ReasonCap - $fixed

    $body = New-Object System.Collections.Generic.List[string]
    $trimmed = 0
    $shownGraph = 0; $shownDisk = 0
    foreach ($section in $sections) {
        $kept = New-Object System.Collections.Generic.List[string]
        foreach ($line in $section.Lines) {
            if ($trimmed -eq 0 -and $budget - ($line.Length + 1) -ge 0) {
                $kept.Add($line); $budget -= ($line.Length + 1)
            }
            else { $trimmed++ }
        }
        if ($section.Graph) { $shownGraph = $kept.Count } else { $shownDisk = $kept.Count }
        $body.Add((& $section.Title $kept.Count))
        foreach ($line in $kept) { $body.Add($line) }
    }
    if ($trimmed -gt 0) {
        $unit = if ($listed) { 'file line(s)' } else { 'hit(s)' }
        $notes.Add("$trimmed $unit trimmed to fit the $($script:ReasonCap)-character cap.")
    }

    # Continuation: exact offsets past what was SHOWN, or past the end when a section is done.
    $tail = New-Object System.Collections.Generic.List[string]
    foreach ($note in $notes) { $tail.Add($note) }
    if ($local) {
        if ($diskTruncated -or $trimmed -gt 0) { $tail.Add('razorgraph is down, so there is no continuation call: narrow the pattern, path or glob to see the rest.') }
    }
    else {
        if ($listed) {
            $graphMore = $graphTruncated; $diskMore = $diskTruncated
            $nextGraph = if ($graphMore) { [long](Get-Prop $graph 'nextOffset') } else { $graphTotal }
            $nextDisk = if ($diskMore) { [long](Get-Prop $disk 'nextOffset') } else { $diskTotal }
        }
        else {
            $graphMore = ($graphId -and ($graphTruncated -or $shownGraph -lt $graphHits.Count))
            $diskMore = ($diskTruncated -or $shownDisk -lt $diskHits.Count)
            $nextGraph = if ($graphMore) { $shownGraph } else { $graphTotal }
            $nextDisk = if ($diskMore) { $shownDisk } else { $diskTotal }
        }
        if (-not $graphId) { $nextGraph = 0 }
        if ($graphMore -or $diskMore) {
            $tail.Add('More: ' + (Format-Call -Arguments $arguments -Offset $nextGraph -DiskOffset $nextDisk))
        }
    }

    $reason = (@($head) + @($body) + @($tail)) -join "`n"
    if ($reason.Length -gt $script:ReasonCap) {
        $reason = $reason.Substring(0, $script:ReasonCap - 60) + "`n[cut at the $($script:ReasonCap)-character cap]"
    }
    return $reason
}

# ---- The shell trigger ----------------------------------------------------------------
#
# "PowerShell and bash shouldn't be doing searches." (Lori, 2026-10-12.) A Bash or
# PowerShell search whose INPUT is files inside a git working tree is denied, whatever
# the file type. Until that day only a command naming .cs or a src\ path was, with every
# command that mentioned git exempted wholesale.
#
# The command is split into pipelines and commands -- PowerShell by its own parser, Bash
# by the small tokenizer below -- and each search command is asked one question: what
# does it read? Its file operands; a file fed in with <; the files the nearest upstream
# cat, head or Get-Content reads (for Select-String, also Get-ChildItem's items); the
# paths a find hands to -exec or xargs; or, for a search that recurses when given no
# operand, the directory it runs in. A search of another command's output -- git diff |
# grep, dotnet test | Select-String -- reads no files and passes. That is also what
# keeps git honest now: git diff, log, show and blame are not searches, git log --grep
# and -S are flags of a command that is not one, and only `git grep` is.
#
# The search is never RUN. Reading pattern and folder out of arbitrary shell text is a
# heuristic that rots, so this only redirects: the denial carries the razorgraph call
# with what it could read, and a placeholder for the pattern when it could not. An
# operand it cannot resolve -- a variable, an expression -- is not guessed at: when that
# is all that stands between a command and a denial, the command runs with one line of
# additionalContext saying so.

$script:ShellPrefilter = '\b(grep|egrep|fgrep|rg|ag|ack|Select-String|sls|findstr)\b'
$script:GrepVerbs = @('grep', 'egrep', 'fgrep', 'rg', 'ag', 'ack')
$script:ContentReaders = @('cat', 'type', 'Get-Content', 'gc', 'head', 'tail', 'less', 'more', 'tac')
$script:ItemListers = @('Get-ChildItem', 'gci', 'ls', 'dir', 'Get-Item', 'gi')
$script:PassThrough = @('Where-Object', 'where', '?', 'Select-Object', 'select', 'Sort-Object', 'sort', 'uniq', 'Out-String', 'tee', 'Tee-Object')
$script:CommandPrefixes = @('sudo', 'command', 'builtin', 'time', 'nice', 'nohup', 'exec', 'if', 'then', 'else', 'elif', 'do', 'while', 'until', '!')
$script:LocationVerbs = @('cd', 'chdir', 'pushd', 'Set-Location', 'sl', 'Push-Location')
$script:ReaderValued = @('Encoding', 'TotalCount', 'Tail', 'Head', 'First', 'Last', 'ReadCount', 'Delimiter', 'Filter', 'Include', 'Exclude', 'Depth', 'Attributes', 'Stream', 'lines', 'bytes')
$script:PathParameters = @('Path', 'LiteralPath', 'PSPath', 'LP')
$script:SlsValued = @('Pattern', 'Path', 'LiteralPath', 'InputObject', 'Context', 'Encoding', 'Include', 'Exclude', 'Culture')
$script:SlsSwitches = @('SimpleMatch', 'CaseSensitive', 'AllMatches', 'NotMatch', 'List', 'Quiet', 'Raw', 'NoEmphasis')
$script:WorkTrees = @{}

# Per tool: short options whose value follows (case-sensitive), long options whose value
# is the next word when not given with =, and options that list files instead of
# searching them (a listing is not a text search).
$script:GrepOptions = @{
    grep = @{ Short = 'efmABCdD'; Lists = @()
        Long = @('regexp', 'file', 'max-count', 'after-context', 'before-context', 'context', 'include', 'exclude', 'exclude-dir', 'exclude-from', 'label', 'devices', 'directories', 'binary-files', 'group-separator') }
    rg   = @{ Short = 'efgtTmABCjMErd'; Lists = @('files', 'type-list')
        Long = @('regexp', 'file', 'glob', 'iglob', 'type', 'type-not', 'max-count', 'after-context', 'before-context', 'context', 'threads', 'max-columns', 'encoding', 'replace', 'max-depth', 'max-filesize', 'sort', 'sortr', 'colors', 'type-add', 'type-clear', 'pre', 'pre-glob', 'engine', 'path-separator', 'context-separator', 'ignore-file') }
    ag   = @{ Short = 'ABCGmg'; Lists = @('g')
        Long = @('ignore', 'ignore-dir', 'file-search-regex', 'depth', 'max-count', 'after', 'before', 'context', 'pager') }
    ack  = @{ Short = 'ABCm'; Lists = @('f', 'g')
        Long = @('type', 'ignore-dir', 'ignore-file', 'match', 'max-count', 'after-context', 'before-context', 'context', 'output') }
    git  = @{ Short = 'efmABCO'; Lists = @()
        Long = @('max-depth', 'threads', 'open-files-in-pager') }
}

# One command-line word. Literal is false when the shell would expand it (a variable, a
# subexpression), so it cannot be resolved here. Bare is false when it starts quoted,
# so it cannot be an option. Items holds a PowerShell array argument's elements.
function New-ShellWord {
    param([string]$Text, [bool]$Literal = $true, [bool]$Bare = $true, [string[]]$Items)
    $elements = if ($null -ne $Items -and $Items.Count -gt 0) { $Items } else { @($Text) }
    [PSCustomObject]@{ Text = $Text; Literal = $Literal; Bare = $Bare; Items = $elements; Value = $null }
}

function New-ShellSegment {
    [PSCustomObject]@{
        Words      = New-Object System.Collections.Generic.List[object]
        InputFiles = New-Object System.Collections.Generic.List[object]
    }
}

function Get-WordRange {
    param($Words, [int]$From)
    $range = New-Object System.Collections.Generic.List[object]
    for ($at = $From; $at -lt $Words.Count; $at++) { $range.Add($Words[$at]) }
    return ,$range
}

function ConvertFrom-ArgumentAst {
    param($Node)
    if ($Node -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
        return (New-ShellWord -Text $Node.Value -Bare ($Node.StringConstantType -eq 'BareWord'))
    }
    if ($Node -is [System.Management.Automation.Language.ConstantExpressionAst]) {
        return (New-ShellWord -Text ([string]$Node.Value))
    }
    if ($Node -is [System.Management.Automation.Language.ArrayLiteralAst]) {
        $parts = New-Object System.Collections.Generic.List[string]
        $constant = $true
        foreach ($part in $Node.Elements) {
            if ($part -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $parts.Add($part.Value) }
            else { $constant = $false }
        }
        if ($constant) { return (New-ShellWord -Text ($parts -join ',') -Bare $false -Items $parts.ToArray()) }
    }
    return (New-ShellWord -Text $Node.Extent.Text -Literal $false -Bare $false)
}

# A pipeline element as a segment. `(Get-Content f) | ...` stands for its last command.
function ConvertFrom-PipelineElement {
    param($Element)
    $segment = New-ShellSegment
    $command = $Element
    if ($Element -is [System.Management.Automation.Language.CommandExpressionAst] -and
        $Element.Expression -is [System.Management.Automation.Language.ParenExpressionAst] -and
        $Element.Expression.Pipeline -is [System.Management.Automation.Language.PipelineAst]) {
        $inner = @($Element.Expression.Pipeline.PipelineElements)
        $command = $inner[$inner.Count - 1]
    }
    if ($command -isnot [System.Management.Automation.Language.CommandAst]) { return $segment }

    foreach ($node in $command.CommandElements) {
        if ($node -is [System.Management.Automation.Language.CommandParameterAst]) {
            $word = New-ShellWord -Text ('-' + $node.ParameterName)
            if ($null -ne $node.Argument) { $word.Value = ConvertFrom-ArgumentAst $node.Argument }
            $segment.Words.Add($word)
        }
        else { $segment.Words.Add((ConvertFrom-ArgumentAst $node)) }
    }
    return $segment
}

# Every pipeline in a PowerShell command, script blocks and subexpressions included.
function Split-PowerShellCommand {
    param([string]$Text)
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors)
    $pipelines = New-Object System.Collections.Generic.List[object]
    $found = $ast.FindAll({ param($candidate) $candidate -is [System.Management.Automation.Language.PipelineAst] }, $true)
    foreach ($pipelineAst in $found) {
        $segments = New-Object System.Collections.Generic.List[object]
        foreach ($element in $pipelineAst.PipelineElements) { $segments.Add((ConvertFrom-PipelineElement $element)) }
        $pipelines.Add($segments)
    }
    return ,$pipelines
}

function New-BashState {
    [PSCustomObject]@{
        Pipelines = New-Object System.Collections.Generic.List[object]
        Segments  = New-Object System.Collections.Generic.List[object]
        Segment   = New-ShellSegment
        Word      = New-Object System.Text.StringBuilder
        InWord    = $false
        Literal   = $true
        Bare      = $true
        Redirect  = $null
        Heredocs  = New-Object System.Collections.Generic.List[string]
    }
}

function Complete-BashWord {
    param($State)
    if (-not $State.InWord) { return }
    $text = $State.Word.ToString()
    $redirect = $State.Redirect
    $wasLiteral = $State.Literal
    $wasBare = $State.Bare
    [void]$State.Word.Clear()
    $State.InWord = $false
    $State.Literal = $true
    $State.Bare = $true
    $State.Redirect = $null

    if ($redirect -eq 'heredoc') { $State.Heredocs.Add($text); return }
    if ($redirect -eq 'in') { $State.Segment.InputFiles.Add((New-ShellWord -Text $text -Literal $wasLiteral -Bare $false)); return }
    if ($redirect) { return }
    # A brace group's braces are words of their own in bash, and they bound commands.
    if ($wasBare -and ($text -eq '{' -or $text -eq '}')) { Complete-BashPipeline $State; return }
    $State.Segment.Words.Add((New-ShellWord -Text $text -Literal $wasLiteral -Bare $wasBare))
}

function Complete-BashSegment {
    param($State)
    Complete-BashWord $State
    if ($State.Segment.Words.Count -gt 0 -or $State.Segment.InputFiles.Count -gt 0) { $State.Segments.Add($State.Segment) }
    $State.Segment = New-ShellSegment
}

function Complete-BashPipeline {
    param($State)
    Complete-BashSegment $State
    if ($State.Segments.Count -gt 0) { $State.Pipelines.Add($State.Segments) }
    $State.Segments = New-Object System.Collections.Generic.List[object]
}

# One redirection operator at Index ('<' or '>'), returning the index after it: an input
# file, an output target, a heredoc delimiter or a descriptor duplication, recorded on
# State. Split out of Split-BashCommand to keep its nesting within the gate's limit.
function Step-BashRedirect {
    param($State, [string]$Text, [int]$Index)
    $n = $Text.Length
    $i = $Index
    $c = $Text[$i]
    $next = if ($i + 1 -lt $n) { $Text[$i + 1] } else { [char]0 }

    # A file-descriptor number before the operator belongs to it, not to a word.
    if ($State.InWord -and $State.Word.ToString() -match '^\d+$') { [void]$State.Word.Clear(); $State.InWord = $false }
    Complete-BashWord $State

    if ($c -eq '<') {
        if ($next -eq '(') { Complete-BashPipeline $State; return $i + 2 }
        if ($next -ne '<') { $State.Redirect = 'in'; return $i + 1 }
        $i += 2
        if ($i -lt $n -and $Text[$i] -eq '<') { $State.Redirect = 'out'; return $i + 1 }
        $State.Redirect = 'heredoc'
        if ($i -lt $n -and $Text[$i] -eq '-') { $i++ }
        return $i
    }

    $i++
    if ($i -lt $n -and ($Text[$i] -eq '>' -or $Text[$i] -eq '|')) { $i++ }
    if ($i -lt $n -and $Text[$i] -eq '&') {
        $i++
        while ($i -lt $n -and ([string]$Text[$i]) -match '[\d-]') { $i++ }
        return $i
    }
    $State.Redirect = 'out'
    return $i
}

# Bash words, commands and pipelines: quotes, escapes, $(...) and backticks kept inside
# their word, redirections set aside (an input file kept), heredoc bodies skipped as
# data. Not a shell: enough to say which command each word belongs to.
function Split-BashCommand {
    param([string]$Text)
    $state = New-BashState
    $n = $Text.Length
    $i = 0
    while ($i -lt $n) {
        $c = $Text[$i]
        $next = if ($i + 1 -lt $n) { $Text[$i + 1] } else { [char]0 }

        if ($c -eq "`n") {
            Complete-BashPipeline $state
            $i++
            foreach ($delimiter in @($state.Heredocs)) {
                while ($i -lt $n) {
                    $end = $Text.IndexOf("`n", $i)
                    if ($end -lt 0) { $end = $n }
                    $line = $Text.Substring($i, $end - $i).Trim()
                    $i = [Math]::Min($end + 1, $n)
                    if ($line -eq $delimiter) { break }
                }
            }
            $state.Heredocs.Clear()
            continue
        }
        if ([char]::IsWhiteSpace($c)) { Complete-BashWord $state; $i++; continue }
        if ($c -eq '#' -and -not $state.InWord) {
            while ($i -lt $n -and $Text[$i] -ne "`n") { $i++ }
            continue
        }
        if ($c -eq "'") {
            if (-not $state.InWord) { $state.Bare = $false }
            $state.InWord = $true
            $close = $Text.IndexOf("'", $i + 1)
            if ($close -lt 0) { $close = $n }
            [void]$state.Word.Append($Text.Substring($i + 1, $close - $i - 1))
            $i = $close + 1
            continue
        }
        if ($c -eq '"') {
            if (-not $state.InWord) { $state.Bare = $false }
            $state.InWord = $true
            $i++
            while ($i -lt $n -and $Text[$i] -ne '"') {
                if ($Text[$i] -eq '\' -and $i + 1 -lt $n -and '$`"\'.IndexOf($Text[$i + 1]) -ge 0) { $i++ }
                elseif ($Text[$i] -eq '$' -or $Text[$i] -eq '`') { $state.Literal = $false }
                [void]$state.Word.Append($Text[$i])
                $i++
            }
            $i++
            continue
        }
        if ($c -eq '\') {
            if ($next -ne "`n") {
                $state.InWord = $true
                if ($i + 1 -lt $n) { [void]$state.Word.Append($next) }
            }
            $i += 2
            continue
        }
        if ($c -eq '$' -and ($next -eq '(' -or $next -eq '{')) {
            $state.InWord = $true
            $state.Literal = $false
            $open = $next
            $shut = if ($next -eq '(') { ')' } else { '}' }
            [void]$state.Word.Append($c)
            $i++
            $depth = 0
            do {
                $ch = $Text[$i]
                if ($ch -eq $open) { $depth++ } elseif ($ch -eq $shut) { $depth-- }
                [void]$state.Word.Append($ch)
                $i++
            } while ($i -lt $n -and $depth -gt 0)
            continue
        }
        if ($c -eq '`') {
            $state.InWord = $true
            $state.Literal = $false
            $close = $Text.IndexOf('`', $i + 1)
            if ($close -lt 0) { $close = $n - 1 }
            [void]$state.Word.Append($Text.Substring($i, $close - $i + 1))
            $i = $close + 1
            continue
        }
        if ($c -eq '$') { $state.Literal = $false }
        if ($c -eq '|') {
            if ($next -eq '|') { Complete-BashPipeline $state; $i += 2 }
            else { Complete-BashSegment $state; $i += $(if ($next -eq '&') { 2 } else { 1 }) }
            continue
        }
        if ($c -eq '&') {
            if ($next -eq '&') { Complete-BashPipeline $state; $i += 2; continue }
            if ($next -eq '>') {
                Complete-BashWord $state
                $state.Redirect = 'out'
                $i += 2
                if ($i -lt $n -and $Text[$i] -eq '>') { $i++ }
                continue
            }
            Complete-BashPipeline $state
            $i++
            continue
        }
        if ($c -eq ';' -or $c -eq '(' -or $c -eq ')') { Complete-BashPipeline $state; $i++; continue }
        if ($c -eq '<' -or $c -eq '>') { $i = Step-BashRedirect -State $state -Text $Text -Index $i; continue }
        $state.InWord = $true
        [void]$state.Word.Append($c)
        $i++
    }
    Complete-BashPipeline $state
    return ,$state.Pipelines
}

function Get-VerbName {
    param([string]$Text)
    $leaf = @($Text -split '[\\/]')[-1]
    return ($leaf -replace '(?i)\.exe$', '')
}

# Where the command word sits, past VAR=x assignments and prefixes such as sudo or then.
function Get-VerbIndex {
    param($Words)
    for ($at = 0; $at -lt $Words.Count; $at++) {
        $text = $Words[$at].Text
        if ($Words[$at].Bare -and $text -match '^[A-Za-z_][A-Za-z0-9_]*=') { continue }
        $name = Get-VerbName $text
        if ($script:CommandPrefixes -contains $name) { continue }
        if ($name -eq 'env' -and $at + 1 -lt $Words.Count -and $Words[$at + 1].Text -match '^[A-Za-z_][A-Za-z0-9_]*=') { continue }
        return $at
    }
    return -1
}

function New-SearchDescriptor {
    param([string]$Tool)
    [PSCustomObject]@{
        Tool       = $Tool
        Patterns   = New-Object System.Collections.Generic.List[object]
        IgnoreCase = $false
        Fixed      = $false
        Operands   = New-Object System.Collections.Generic.List[object]
        NoOperand  = 'stdin'
        Glob       = $null
        Base       = $null
        Unsure     = $null
    }
}

function Set-GrepOption {
    param($Search, [string]$Name, $Value, [string]$Kind)
    if ($Name -ceq 'e' -or $Name -ceq 'regexp') { $Search.Patterns.Add($Value) }
    elseif ($Name -ceq 'f' -or $Name -ceq 'file') { $Search.Patterns.Add($null) }
    elseif ($Name -ceq 'i' -or $Name -ceq 'ignore-case' -or ($Kind -eq 'grep' -and $Name -ceq 'y')) { $Search.IgnoreCase = $true }
    elseif ($Name -ceq 'F' -or $Name -ceq 'fixed-strings' -or $Name -ceq 'literal' -or ($Name -ceq 'Q' -and $Kind -in 'ag', 'ack')) { $Search.Fixed = $true }
    elseif ($Kind -eq 'grep' -and ($Name -ceq 'r' -or $Name -ceq 'R' -or $Name -clike '*recursive')) { $Search.NoOperand = 'cwd' }
    elseif ($Name -ceq 'include' -or $Name -ceq 'glob' -or $Name -ceq 'iglob' -or ($Kind -eq 'rg' -and $Name -ceq 'g')) {
        if ($null -ne $Value -and $Value.Literal -and (Test-PlainGlob $Value.Text)) { $Search.Glob = $Value.Text }
    }
    elseif ($Kind -eq 'rg' -and ($Name -ceq 't' -or $Name -ceq 'type')) {
        if ($null -ne $Value -and $script:TypeGlobs.ContainsKey($Value.Text)) { $Search.Glob = $script:TypeGlobs[$Value.Text] }
    }
}

# grep, egrep, fgrep, rg, ag, ack and git grep: options, pattern, file operands.
function Read-GrepFamily {
    param($Words, [string]$Kind, [string]$Tool)
    $options = $script:GrepOptions[$Kind]
    $search = New-SearchDescriptor $Tool
    $search.NoOperand = if ($Kind -eq 'grep') { 'stdin' } else { 'cwd' }
    $search.Fixed = ($Tool -eq 'fgrep')
    $positional = New-Object System.Collections.Generic.List[object]
    $done = $false
    for ($at = 0; $at -lt $Words.Count; $at++) {
        $word = $Words[$at]
        $text = $word.Text
        if (-not $done -and $word.Bare -and $text -eq '--') { $done = $true; continue }
        if ($done -or -not $word.Bare -or $text -notmatch '^-.') { $positional.Add($word); continue }

        if ($text.StartsWith('--')) {
            $name = $text.Substring(2)
            $value = $null
            $eq = $name.IndexOf('=')
            if ($eq -ge 0) {
                $value = New-ShellWord -Text $name.Substring($eq + 1) -Literal $word.Literal
                $name = $name.Substring(0, $eq)
            }
            elseif ($options.Long -contains $name -and $at + 1 -lt $Words.Count) { $at++; $value = $Words[$at] }
            if ($options.Lists -contains $name) { return $null }
            Set-GrepOption -Search $search -Name $name -Value $value -Kind $Kind
            continue
        }

        $cluster = $text.Substring(1)
        for ($k = 0; $k -lt $cluster.Length; $k++) {
            $ch = [string]$cluster[$k]
            if ($options.Lists -ccontains $ch) { return $null }
            if (-not $options.Short.Contains($ch)) { Set-GrepOption -Search $search -Name $ch -Value $null -Kind $Kind; continue }
            $rest = $cluster.Substring($k + 1)
            $value = $null
            if ($rest) { $value = New-ShellWord -Text $rest -Literal $word.Literal }
            elseif ($at + 1 -lt $Words.Count) { $at++; $value = $Words[$at] }
            Set-GrepOption -Search $search -Name $ch -Value $value -Kind $Kind
            break
        }
    }

    if ($search.Patterns.Count -eq 0) {
        if ($positional.Count -eq 0) { return $null }
        $search.Patterns.Add($positional[0])
        $positional.RemoveAt(0)
    }
    foreach ($operand in $positional) { $search.Operands.Add($operand) }
    return $search
}

# A grep-family command inside find -exec or xargs.
function Read-InnerSearch {
    param($Words)
    $at = Get-VerbIndex $Words
    if ($at -lt 0) { return $null }
    $verb = Get-VerbName $Words[$at].Text
    if ($script:GrepVerbs -notcontains $verb) { return $null }
    $kind = if ($verb -in 'egrep', 'fgrep') { 'grep' } else { $verb.ToLowerInvariant() }
    return (Read-GrepFamily -Words (Get-WordRange $Words ($at + 1)) -Kind $kind -Tool $verb)
}

# Only `git grep` searches; git diff, log, show and blame read history, and log's --grep
# and -S are flags of a command that is not a search.
function Read-GitGrep {
    param($Words, [string]$BaseDir)
    $at = 0
    $gitDir = $null
    while ($at -lt $Words.Count) {
        $text = $Words[$at].Text
        if ($text -ceq '-C') {
            if ($at + 1 -lt $Words.Count) { $gitDir = $Words[$at + 1] }
            $at += 2
            continue
        }
        if ($text -ceq '-c' -or $text -ceq '--git-dir' -or $text -ceq '--work-tree' -or $text -ceq '--namespace') { $at += 2; continue }
        if ($Words[$at].Bare -and $text.StartsWith('-')) { $at++; continue }
        break
    }
    if ($at -ge $Words.Count -or $Words[$at].Text -cne 'grep') { return $null }

    $search = Read-GrepFamily -Words (Get-WordRange $Words ($at + 1)) -Kind 'git' -Tool 'git grep'
    if ($null -eq $search -or $null -eq $gitDir) { return $search }
    $dir = if ($gitDir.Literal) { Resolve-ShellDirectory -Text $gitDir.Text -BaseDir $BaseDir } else { $null }
    if ($dir) { $search.Base = $dir } else { $search.Unsure = "git -C $($gitDir.Text)" }
    return $search
}

function Resolve-SlsParameter {
    param([string]$Name)
    if ($Name -eq 'LP' -or $Name -eq 'PSPath') { return 'LiteralPath' }
    $all = @($script:SlsValued + $script:SlsSwitches)
    $exact = @($all | Where-Object { $_ -eq $Name })
    if ($exact.Count -eq 1) { return $exact[0] }
    $prefixed = @($all | Where-Object { $_.StartsWith($Name, [StringComparison]::OrdinalIgnoreCase) })
    if ($prefixed.Count -eq 1) { return $prefixed[0] }
    return $null
}

function Read-SelectString {
    param($Words, [string]$Tool)
    $search = New-SearchDescriptor $Tool
    $search.IgnoreCase = $true
    $bound = @{}
    $positional = New-Object System.Collections.Generic.List[object]
    for ($at = 0; $at -lt $Words.Count; $at++) {
        $word = $Words[$at]
        if (-not ($word.Bare -and $word.Text -match '^-[A-Za-z]')) { $positional.Add($word); continue }
        $name = Resolve-SlsParameter $word.Text.Substring(1)
        if ($null -eq $name) { continue }
        if ($null -ne $word.Value) { $bound[$name] = $word.Value }
        elseif ($script:SlsValued -contains $name) {
            if ($at + 1 -lt $Words.Count) { $at++; $bound[$name] = $Words[$at] }
        }
        else { $bound[$name] = New-ShellWord -Text '$true' }
    }
    if (-not $bound.ContainsKey('Pattern') -and $positional.Count -gt 0) { $bound['Pattern'] = $positional[0]; $positional.RemoveAt(0) }
    $hasPath = $bound.ContainsKey('Path') -or $bound.ContainsKey('LiteralPath')
    if (-not $hasPath -and $positional.Count -gt 0) { $bound['Path'] = $positional[0]; $hasPath = $true }
    if (-not $bound.ContainsKey('Pattern')) { return $null }
    if ($bound.ContainsKey('InputObject') -and -not $hasPath) { return $null }

    $search.Patterns.Add($bound['Pattern'])
    if ($bound.ContainsKey('CaseSensitive')) { $search.IgnoreCase = ($bound['CaseSensitive'].Text -eq '$false') }
    if ($bound.ContainsKey('SimpleMatch')) { $search.Fixed = ($bound['SimpleMatch'].Text -ne '$false') }
    foreach ($key in @('Path', 'LiteralPath')) { if ($bound.ContainsKey($key)) { $search.Operands.Add($bound[$key]) } }
    if ($bound.ContainsKey('Include')) {
        $include = $bound['Include']
        if ($include.Literal -and @($include.Items).Count -eq 1 -and (Test-PlainGlob $include.Text)) { $search.Glob = $include.Text }
    }
    return $search
}

function Read-Findstr {
    param($Words)
    $search = New-SearchDescriptor 'findstr'
    $positional = New-Object System.Collections.Generic.List[object]
    $literal = $false
    foreach ($word in $Words) {
        if (-not ($word.Bare -and $word.Text -match '^[/-]([A-Za-z]+)(?::(.*))?$')) { $positional.Add($word); continue }
        $flags = $Matches[1].ToLowerInvariant()
        if ($Matches.ContainsKey(2)) {
            $key = $flags[$flags.Length - 1]
            $flags = $flags.Substring(0, $flags.Length - 1)
            $value = $Matches[2].Trim('"')
            if ($key -eq 'c') { $search.Patterns.Add((New-ShellWord -Text $value -Literal $word.Literal)); $literal = $true }
            elseif ($key -eq 'g') { $search.Patterns.Add($null) }
            elseif ($key -eq 'f') { $search.Unsure = "findstr $($word.Text)" }
            elseif ($key -eq 'd') {
                foreach ($dir in ($value -split ';')) { if ($dir) { $search.Operands.Add((New-ShellWord -Text $dir -Literal $word.Literal)) } }
            }
        }
        if ($flags.Contains('l')) { $literal = $true }
        if ($flags.Contains('r')) { $literal = $false }
        if ($flags.Contains('i')) { $search.IgnoreCase = $true }
    }
    if ($search.Patterns.Count -eq 0) {
        if ($positional.Count -eq 0) { return $null }
        $first = $positional[0]
        $positional.RemoveAt(0)
        # Space-separated strings are alternatives; not worth translating.
        if ($first.Text.Contains(' ')) { $search.Patterns.Add($null) } else { $search.Patterns.Add($first) }
    }
    foreach ($operand in $positional) { $search.Operands.Add($operand) }
    $search.Fixed = $literal
    return $search
}

# find's starting points (default .) and a plain -name glob.
function Get-FindStarts {
    param($Words)
    $found = [PSCustomObject]@{ Words = (New-Object System.Collections.Generic.List[object]); Glob = $null; Unsure = $null; MustExist = $false }
    $at = 0
    while ($at -lt $Words.Count -and -not ($Words[$at].Bare -and $Words[$at].Text -match '^[-(!]')) { $found.Words.Add($Words[$at]); $at++ }
    if ($found.Words.Count -eq 0) { $found.Words.Add((New-ShellWord -Text '.')) }
    for (; $at + 1 -lt $Words.Count; $at++) {
        if (($Words[$at].Text -ceq '-name' -or $Words[$at].Text -ceq '-iname') -and (Test-PlainGlob $Words[$at + 1].Text)) { $found.Glob = $Words[$at + 1].Text }
    }
    return $found
}

function Read-FindExec {
    param($Words)
    $exec = -1
    for ($at = 0; $at -lt $Words.Count; $at++) {
        if ($Words[$at].Text -in '-exec', '-execdir', '-ok', '-okdir') { $exec = $at; break }
    }
    if ($exec -lt 0) { return $null }
    $inner = New-Object System.Collections.Generic.List[object]
    for ($at = $exec + 1; $at -lt $Words.Count; $at++) {
        $text = $Words[$at].Text
        if ($text -eq ';' -or $text -eq '+') { break }
        if ($text -ne '{}') { $inner.Add($Words[$at]) }
    }
    $search = Read-InnerSearch $inner
    if ($null -eq $search) { return $null }
    $starts = Get-FindStarts (Get-WordRange $Words 0)
    $search.Operands.Clear()
    $search.Operands.AddRange($starts.Words)
    if (-not $search.Glob) { $search.Glob = $starts.Glob }
    return $search
}

function Read-Xargs {
    param($Words)
    $at = 0
    while ($at -lt $Words.Count -and $Words[$at].Bare -and $Words[$at].Text.StartsWith('-')) {
        if ($Words[$at].Text -cin '-n', '-I', '-P', '-d', '-L', '-s', '-E', '-a') { $at++ }
        $at++
    }
    $search = Read-InnerSearch (Get-WordRange $Words $at)
    if ($null -ne $search -and $search.Operands.Count -eq 0) { $search.NoOperand = 'xargs' }
    return $search
}

function Read-SearchSegment {
    param([string]$Verb, $Words, [string]$BaseDir)
    if ($Verb -eq 'git') { return (Read-GitGrep -Words $Words -BaseDir $BaseDir) }
    if ($Verb -in 'Select-String', 'sls') { return (Read-SelectString -Words $Words -Tool $Verb) }
    if ($Verb -eq 'findstr') { return (Read-Findstr $Words) }
    if ($Verb -eq 'find') { return (Read-FindExec $Words) }
    if ($Verb -eq 'xargs') { return (Read-Xargs $Words) }
    if ($script:GrepVerbs -contains $Verb) {
        $kind = if ($Verb -in 'egrep', 'fgrep') { 'grep' } else { $Verb.ToLowerInvariant() }
        return (Read-GrepFamily -Words $Words -Kind $kind -Tool $Verb)
    }
    return $null
}

# The path operands of a reader or lister: positionals and -Path/-LiteralPath, with a
# plain -Filter or -Include kept as a glob.
function Get-ReaderOperands {
    param($Words, [string]$Verb)
    $found = [PSCustomObject]@{ Words = (New-Object System.Collections.Generic.List[object]); Glob = $null; Unsure = $null; MustExist = $true }
    $valued = @($script:ReaderValued)
    if ($Verb -in 'head', 'tail') { $valued += @('n', 'c') }
    for ($at = 0; $at -lt $Words.Count; $at++) {
        $word = $Words[$at]
        if (-not ($word.Bare -and $word.Text -match '^--?([A-Za-z][\w-]*)')) { $found.Words.Add($word); continue }
        $name = $Matches[1]
        $isPath = $script:PathParameters -contains $name
        $value = $word.Value
        if ($null -eq $value -and ($isPath -or $valued -contains $name) -and -not $word.Text.Contains('=') -and $at + 1 -lt $Words.Count) {
            $at++
            $value = $Words[$at]
        }
        if ($null -eq $value) { continue }
        if ($isPath) { $found.Words.Add($value) }
        elseif ($name -in 'Filter', 'Include' -and $value.Literal -and @($value.Items).Count -eq 1 -and (Test-PlainGlob $value.Text)) { $found.Glob = $value.Text }
    }
    return $found
}

# What a search with no operand of its own is fed: the files the nearest upstream command
# reads, past filters that only pass lines through, or nothing when that command is not
# a file reader (git diff, dotnet test, env).
function Get-UpstreamFiles {
    param($Segments, [int]$Index, $Search)
    $nothing = [PSCustomObject]@{ Words = (New-Object System.Collections.Generic.List[object]); Glob = $null; Unsure = $null; MustExist = $false }
    for ($j = $Index - 1; $j -ge 0; $j--) {
        $words = $Segments[$j].Words
        $at = Get-VerbIndex $words
        if ($at -lt 0) { return $nothing }
        $verb = Get-VerbName $words[$at].Text
        $rest = Get-WordRange $words ($at + 1)
        if ($script:PassThrough -contains $verb) { continue }
        if ($Search.NoOperand -eq 'xargs') {
            if ($verb -eq 'find') { return (Get-FindStarts $rest) }
            $nothing.Unsure = "xargs $($Search.Tool) fed by $verb"
            return $nothing
        }
        if ($script:ContentReaders -contains $verb) {
            $operands = Get-ReaderOperands -Words $rest -Verb $verb
            if ($operands.Words.Count -eq 0) { continue }
            return $operands
        }
        if ($script:ItemListers -contains $verb -and $Search.Tool -in 'Select-String', 'sls') {
            $operands = Get-ReaderOperands -Words $rest -Verb $verb
            if ($operands.Words.Count -eq 0) { $operands.Words.Add((New-ShellWord -Text '.')) }
            return $operands
        }
        return $nothing
    }
    return $nothing
}

function Get-SearchSources {
    param($Search, $Segments, [int]$Index)
    $sources = [PSCustomObject]@{ Words = (New-Object System.Collections.Generic.List[object]); Glob = $null; Unsure = $null; MustExist = $false }
    if ($Search.Operands.Count -gt 0) { $sources.Words.AddRange($Search.Operands); return $sources }
    $inputs = $Segments[$Index].InputFiles
    if ($inputs.Count -gt 0) { $sources.Words.AddRange($inputs); return $sources }
    if ($Index -gt 0) { return (Get-UpstreamFiles -Segments $Segments -Index $Index -Search $Search) }
    if ($Search.NoOperand -eq 'cwd') { $sources.Words.Add((New-ShellWord -Text '.')) }
    return $sources
}

# Git Bash spells C:\x as /c/x.
function ConvertFrom-BashPath {
    param([string]$Text)
    if ($Text -match '^/([A-Za-z])(/|$)') { return ($Matches[1].ToUpperInvariant() + ':\' + $Text.Substring(2).TrimStart('/')) }
    if ($Text -eq '~' -or $Text -match '^~[\\/]') { return ($HOME + $Text.Substring(1)) }
    return $Text
}

function Resolve-ShellDirectory {
    param([string]$Text, [string]$BaseDir)
    $path = ConvertFrom-BashPath $Text
    if (-not [System.IO.Path]::IsPathRooted($path) -and -not $BaseDir) { return $null }
    return (Resolve-FullPath -Path $path -BaseDir $BaseDir)
}

# A path operand as razorgraph's folder plus glob, and the nearest existing directory to
# ask git about. $null when it is relative to a directory this could not follow.
function Resolve-ShellTarget {
    param([string]$Text, [string]$BaseDir)
    $path = ConvertFrom-BashPath $Text
    if (-not [System.IO.Path]::IsPathRooted($path) -and -not $BaseDir) { return $null }

    $glob = $null
    $parts = @($path -split '[\\/]')
    $wild = -1
    for ($k = 0; $k -lt $parts.Count; $k++) { if ($parts[$k] -match '[*?\[]') { $wild = $k; break } }
    if ($wild -ge 0) {
        $leaf = $parts[$parts.Count - 1]
        $between = @(if ($parts.Count - 1 -gt $wild) { $parts[$wild..($parts.Count - 2)] })
        if ((Test-PlainGlob $leaf) -and @($between | Where-Object { $_ -ne '**' }).Count -eq 0) { $glob = $leaf }
        $path = if ($wild -eq 0) { '.' } else { ($parts[0..($wild - 1)] -join '\') + '\' }
    }

    $full = Resolve-FullPath -Path $path -BaseDir $BaseDir
    $exists = $true
    if ($wild -ge 0) { $folder = $full; $exists = Test-Path -LiteralPath $full -PathType Container }
    elseif (Test-Path -LiteralPath $full -PathType Container) { $folder = $full }
    elseif (Test-Path -LiteralPath $full -PathType Leaf) {
        $folder = Split-Path -Parent $full
        $name = Split-Path -Leaf $full
        if (Test-PlainGlob $name) { $glob = $name }
    }
    else { $folder = $full; $exists = $false }

    $folder = $folder.TrimEnd('\', '/')
    if ($folder -match '^[A-Za-z]:$') { $folder += '\' }
    $probe = $folder
    while ($probe -and -not (Test-Path -LiteralPath $probe -PathType Container)) { $probe = Split-Path -Parent $probe }
    if (-not $exists) { $folder = $probe }
    return [PSCustomObject]@{ Folder = $folder; Glob = $glob; Exists = $exists; Probe = $probe }
}

function Test-ShellTargetInRepo {
    param($Target)
    if (-not $Target.Probe) { return $false }
    if (-not $script:WorkTrees.ContainsKey($Target.Probe)) { $script:WorkTrees[$Target.Probe] = Test-InsideGitWorkTree $Target.Probe }
    return $script:WorkTrees[$Target.Probe]
}

# pwsh -Command "...", bash -c "..." and cmd /c ...: the text they run.
function Get-InnerCommand {
    param([string]$Verb, $Words)
    if ($Verb -in 'pwsh', 'powershell') { $shell = 'PowerShell'; $flag = '^-(c|co|com|comm|comma|comman|command)$' }
    elseif ($Verb -in 'bash', 'sh', 'zsh') { $shell = 'Bash'; $flag = '^-[a-z]*c[a-z]*$' }
    elseif ($Verb -eq 'cmd') { $shell = 'Bash'; $flag = '^/[ck]$' }
    else { return $null }
    for ($at = 0; $at -lt $Words.Count; $at++) {
        if (-not ($Words[$at].Bare -and $Words[$at].Text -match $flag)) { continue }
        $parts = @(for ($k = $at + 1; $k -lt $Words.Count; $k++) { $Words[$k].Text })
        if ($Verb -in 'bash', 'sh', 'zsh') { $parts = @($parts | Select-Object -First 1) }
        if ($parts.Count -eq 0) { return $null }
        return [PSCustomObject]@{ Text = ($parts -join ' '); Shell = $shell }
    }
    return $null
}

# Search is set (with Target and Glob) when some search reads files inside a git working
# tree; Unsure names the operands that could not be resolved.
function Get-ShellVerdict {
    param([string]$Command, [string]$Shell, [string]$BaseDir, [int]$Depth = 0)
    $verdict = [PSCustomObject]@{ Search = $null; Target = $null; Glob = $null; Unsure = (New-Object System.Collections.Generic.List[string]) }
    $pipelines = if ($Shell -eq 'PowerShell') { Split-PowerShellCommand $Command } else { Split-BashCommand $Command }
    $location = $BaseDir

    foreach ($segments in $pipelines) {
        for ($index = 0; $index -lt $segments.Count; $index++) {
            $words = $segments[$index].Words
            $at = Get-VerbIndex $words
            if ($at -lt 0) { continue }
            $verb = Get-VerbName $words[$at].Text
            $rest = Get-WordRange $words ($at + 1)

            if ($script:LocationVerbs -contains $verb) {
                $operands = (Get-ReaderOperands -Words $rest -Verb $verb).Words
                $location = if ($operands.Count -eq 0) { $HOME }
                elseif ($operands[0].Literal -and $operands[0].Text -ne '-') { Resolve-ShellDirectory -Text $operands[0].Text -BaseDir $location }
                else { $null }
                continue
            }

            $inner = Get-InnerCommand -Verb $verb -Words $rest
            if ($null -ne $inner) {
                if ($Depth -lt 3) {
                    $nested = Get-ShellVerdict -Command $inner.Text -Shell $inner.Shell -BaseDir $location -Depth ($Depth + 1)
                    if ($null -ne $nested.Search) { return $nested }
                    $verdict.Unsure.AddRange($nested.Unsure)
                }
                continue
            }

            $search = Read-SearchSegment -Verb $verb -Words $rest -BaseDir $location
            if ($null -eq $search) { continue }
            if ($search.Unsure) { $verdict.Unsure.Add($search.Unsure); continue }

            $sources = Get-SearchSources -Search $search -Segments $segments -Index $index
            if ($sources.Unsure) { $verdict.Unsure.Add($sources.Unsure); continue }
            if (-not $search.Glob) { $search.Glob = $sources.Glob }
            $searchBase = if ($search.Base) { $search.Base } else { $location }
            $itemCount = 0
            foreach ($word in $sources.Words) { $itemCount += @($word.Items).Count }

            foreach ($word in $sources.Words) {
                if (-not $word.Literal) { $verdict.Unsure.Add($word.Text); continue }
                foreach ($item in @($word.Items)) {
                    if ($item -eq '-' -or $item -eq '{}') { continue }
                    $target = Resolve-ShellTarget -Text $item -BaseDir $searchBase
                    if ($null -eq $target) { $verdict.Unsure.Add($item); continue }
                    if ($sources.MustExist -and -not $target.Exists) { continue }
                    if (-not (Test-ShellTargetInRepo $target)) { continue }
                    $verdict.Search = $search
                    $verdict.Target = $target
                    $verdict.Glob = if ($itemCount -eq 1 -and $target.Glob) { $target.Glob } else { $search.Glob }
                    return $verdict
                }
            }
        }
    }
    return $verdict
}

# The search pattern as a .NET regex, or $null when it cannot be read off the command.
function Get-SearchPattern {
    param($Search)
    if ($Search.Patterns.Count -ne 1) { return $null }
    $word = $Search.Patterns[0]
    if ($null -eq $word -or -not $word.Literal) { return $null }
    $items = @($word.Items | ForEach-Object { if ($Search.Fixed) { [regex]::Escape($_) } else { $_ } })
    if ($items.Count -eq 1) { return $items[0] }
    return (($items | ForEach-Object { "(?:$_)" }) -join '|')
}

function Get-ShellReason {
    param([string]$ToolName, [string]$Command, $Verdict)

    $search = $Verdict.Search
    $pattern = Get-SearchPattern $search
    $arguments = [ordered]@{
        pattern = $(if ($null -ne $pattern) { $pattern } else { '<.NET regex>' })
        folder  = $Verdict.Target.Folder
    }
    if ($Verdict.Glob) { $arguments['glob'] = $Verdict.Glob }
    $arguments['ignoreCase'] = [bool]$search.IgnoreCase

    $fill = ''
    if ($null -eq $pattern) {
        $fill = "`nThis hook could not read the search pattern out of the command, so fill in pattern, a .NET regex. The folder, glob and ignoreCase are what the command would have searched."
    }

    $shown = $Command -replace '\s+', ' '
    if ($shown.Length -gt 140) { $shown = $shown.Substring(0, 140) + '...' }

    return @"
Graph guard: $ToolName command '$shown' runs $($search.Tool) over files inside a git working tree, and Bash and PowerShell do not search files here. Run it through razorgraph instead -- one call returns the graph's name and literal hits and the disk's text hits together, with no line reported twice.

ToolSearch("select:mcp__razorgraph__grep")
$(Format-Call -Arguments $arguments -Offset 0 -DiskOffset 0)$fill

The Grep tool is also available: inside a repo it is answered through razorgraph's grep in the same step. A search that reads only another command's output (git diff | grep, dotnet test | Select-String) passes, and so does one whose files all lie outside any git working tree.
"@
}

try {
    if ($env:JANET_GRAPH_GUARD -eq 'off') { exit 0 }

    $raw = if ($InputJson) { $InputJson } else { [Console]::In.ReadToEnd() }
    if (-not $raw) { exit 0 }

    $payload = $raw | ConvertFrom-Json
    $toolName = [string](Get-Prop $payload 'tool_name')
    $toolInput = Get-Prop $payload 'tool_input'

    $cwd = if (Get-Prop $payload 'cwd') { [string](Get-Prop $payload 'cwd') }
    elseif ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR }
    else { (Get-Location).Path }

    if ($toolName -eq 'Grep') {
        $request = Get-GrepRequest -ToolInput $toolInput -BaseDir $cwd
        if ($request.Pass) {
            if ($request.Announce) { Write-PassContext "Graph guard let this Grep through unanswered: $($request.Pass)." }
            exit 0
        }

        $answer = Invoke-RazorGrep -Call $McpCall -Arguments $request.Arguments
        if ($answer.Status -eq 'unreachable') {
            $ensure = Invoke-EnsureServer -Call $EnsureServer
            $downWhy = Format-OneLine $ensure.Message
            if ($ensure.Up) {
                $answer = Invoke-RazorGrep -Call $McpCall -Arguments $request.Arguments
                $downWhy = "Ensure reported $downWhy, but the retry still could not connect: $(Format-OneLine $answer.Message)"
            }

            if (-not $ensure.Up -or $answer.Status -eq 'unreachable') {
                try {
                    $scan = Invoke-LocalScan -Arguments $request.Arguments
                }
                catch {
                    Write-PassContext "Graph guard: razorgraph was down and this hook's own scan failed ($(Format-OneLine $_.Exception.Message)), so the Grep ran as Grep."
                    exit 0
                }
                $note = "razorgraph was down and could not be started ($downWhy). These results come from this hook's own scan of the files, so there is no graph section."
                Write-Deny (Format-GrepAnswer -Request $request -Result $scan -DownNote $note)
                exit 0
            }
        }

        if ($answer.Status -ne 'ok') {
            Write-PassContext "Graph guard: razorgraph could not answer this Grep ($(Format-OneLine $answer.Message)), so it ran as Grep."
            exit 0
        }

        Write-Deny (Format-GrepAnswer -Request $request -Result $answer.Result)
        exit 0
    }

    if ($toolName -eq 'Bash' -or $toolName -eq 'PowerShell') {
        $command = [string](Get-Prop $toolInput 'command')
        if (-not $command -or $command -notmatch $script:ShellPrefilter) { exit 0 }

        $verdict = Get-ShellVerdict -Command $command -Shell $toolName -BaseDir $cwd
        if ($null -ne $verdict.Search) {
            Write-Deny (Get-ShellReason -ToolName $toolName -Command $command -Verdict $verdict)
        }
        elseif ($verdict.Unsure.Count -gt 0) {
            $unsure = Format-OneLine ((@($verdict.Unsure) | Select-Object -Unique) -join ', ') 120
            Write-PassContext "Graph guard could not tell which files this $toolName search reads ($unsure), so it ran. Bash and PowerShell searches of files inside a repo are denied: search with mcp__razorgraph__grep, or with the Grep tool, which is answered through razorgraph."
        }
    }
}
catch {
    # A broken guard must fail open, not wedge every search in the session -- and say so.
    Write-PassContext "Graph guard failed ($(Format-OneLine $_.Exception.Message)), so the call ran unguarded."
    exit 0
}

exit 0
