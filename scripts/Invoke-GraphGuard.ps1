<#
.SYNOPSIS
    PreToolUse hook: answers a Grep inside a git working tree through razorgraph's grep
    tool, and refuses a shell text search over C# source with that call ready to paste.

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
      Bash/PowerShell fires when the command runs grep, rg, Select-String, sls or
                      findstr AND names .cs or a src\ path in the same command, in a
                      project holding a solution -- EXCEPT a git command, which reads
                      history and change text the graph does not index. `git grep` is
                      still gated, being an ordinary working-tree search wearing git's
                      name. The denial carries a paste-ready mcp__razorgraph__grep call.
                      Shell searches are never executed here.
      anything else   passes.

    WHEN RAZORGRAPH CANNOT ANSWER. Unreachable (connection refused or timed out): run
    Ensure-RazorGraphServer.ps1 and retry once. If the server is not running and cannot
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
    Starts razorgraph when it is down. Returns Ensure-RazorGraphServer.ps1's JSON
    envelope. Injectable for the same reason.

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
        & (Join-Path $PSScriptRoot 'Ensure-RazorGraphServer.ps1')
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

# Shallowest solution under the project dir. The shell trigger is about C# source, and a
# project with no solution holds none worth refusing a search over.
function Get-SolutionFile {
    param([string]$Directory)

    if (-not $Directory -or -not (Test-Path -LiteralPath $Directory)) { return $null }

    foreach ($filter in @('*.slnx', '*.sln')) {
        $hit = Get-ChildItem -LiteralPath $Directory -Filter $filter -File -Recurse -Depth 2 -ErrorAction SilentlyContinue |
            Sort-Object { $_.FullName.Length } |
            Select-Object -First 1
        if ($null -ne $hit) { return $hit }
    }
    return $null
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
        return [PSCustomObject]@{ Up = $false; Message = "Ensure-RazorGraphServer failed: $($_.Exception.Message)" }
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

# The shell trigger: a text-search command that names C# source in the same breath.
function Get-ShellTrigger {
    param([string]$ToolName, $ToolInput)

    $command = [string](Get-Prop $ToolInput 'command')
    if (-not $command) { return $null }

    # GIT IS NOT A CODE SEARCH. A diff, a log, a blame, a show or a status reads
    # HISTORY and CHANGE TEXT, and the graph indexes neither -- it models one built
    # state of the source, so "what changed" and "who touched this" are questions it
    # cannot answer at all. Denying them sends the caller to a tool that has no answer.
    # Three false positives on 2026-09-06 were this: `git status --short` beside a
    # `git diff -- src/X.cs | Select-String` in one command line, refused because the
    # text mentioned a search tool and a .cs path.
    #
    # `git grep` is the exception and stays gated: it searches the working tree exactly
    # as grep does, so it is the same question in a different spelling.
    #
    # KNOWN HOLE, left open deliberately: a compound command that runs git AND a real
    # source grep -- `git status; rg Foo src` -- is exempted by this. Closing it means
    # parsing shell to decide which pipeline each search sits in, which is fragile in a
    # hook that must be fast and legible, and the guard is about defaults rather than
    # defence. Prefer the false negative: this guard's own risk is being clicked past.
    if ($command -match '\bgit\b' -and $command -notmatch '\bgit\s+grep\b') { return $null }

    if ($command -notmatch '\b(grep|rg|Select-String|sls|findstr)\b') { return $null }
    if ($command -notmatch '\.cs\b' -and $command -notmatch 'src[\\/]') { return $null }

    $shown = $command -replace '\s+', ' '
    if ($shown.Length -gt 140) { $shown = $shown.Substring(0, 140) + '...' }
    return "$ToolName command '$shown'"
}

# Reads pattern, folder, glob and case out of a shell search ONLY when the command is one
# plain rg, grep or Select-String with no pipe, redirection, variable or flag this does not
# know. Anything else returns $null and the denial shows placeholders instead.
function Read-ShellSearch {
    param([string]$Command, [string]$BaseDir)

    if ($Command -match '[|;&()$`<>{}\r\n]') { return $null }

    $tokens = @([regex]::Matches($Command.Trim(), '"([^"]*)"|''([^'']*)''|(\S+)') | ForEach-Object {
        [PSCustomObject]@{ Text = ($_.Groups[1].Value + $_.Groups[2].Value + $_.Groups[3].Value); Bare = $_.Groups[3].Success }
    })
    if ($tokens.Count -lt 3) { return $null }

    $verb = $tokens[0].Text
    $pattern = $null; $path = $null
    if ($verb -ceq 'rg' -or $verb -ceq 'grep') {
        $ignoreCase = $false
        $positional = New-Object System.Collections.Generic.List[string]
        foreach ($token in $tokens[1..($tokens.Count - 1)]) {
            if ($token.Bare -and $token.Text.StartsWith('-')) {
                if (@('-i', '--ignore-case') -ccontains $token.Text) { $ignoreCase = $true }
                elseif (@('-n', '--line-number', '-r', '-R', '--recursive', '-H') -cnotcontains $token.Text) { return $null }
            }
            else { $positional.Add($token.Text) }
        }
        if ($positional.Count -ne 2) { return $null }
        $pattern = $positional[0]; $path = $positional[1]
    }
    elseif ($verb -eq 'Select-String' -or $verb -eq 'sls') {
        # Select-String is case-INsensitive unless told otherwise.
        $ignoreCase = $true
        for ($i = 1; $i -lt $tokens.Count; $i++) {
            $name = $tokens[$i].Text
            if (-not $tokens[$i].Bare) { return $null }
            if ($name -eq '-CaseSensitive') { $ignoreCase = $false; continue }
            if ($i + 1 -ge $tokens.Count) { return $null }
            if ($name -eq '-Pattern') { $pattern = $tokens[$i + 1].Text }
            elseif ($name -eq '-Path' -or $name -eq '-LiteralPath') { $path = $tokens[$i + 1].Text }
            else { return $null }
            $i++
        }
        if (-not $pattern -or -not $path) { return $null }
    }
    else { return $null }

    $full = Resolve-FullPath -Path $path -BaseDir $BaseDir
    $leaf = Split-Path -Leaf $full.TrimEnd('\', '/')
    $glob = $null
    if ($leaf -match '[*?]') {
        if (-not (Test-PlainGlob $leaf)) { return $null }
        $folder = Split-Path -Parent $full
        $glob = $leaf
        if (-not (Test-Path -LiteralPath $folder -PathType Container)) { return $null }
    }
    elseif (Test-Path -LiteralPath $full -PathType Container) { $folder = $full.TrimEnd('\', '/') }
    elseif (Test-Path -LiteralPath $full -PathType Leaf) { $folder = Split-Path -Parent $full; $glob = $leaf }
    else { return $null }

    $arguments = [ordered]@{ pattern = $pattern; folder = $folder }
    if ($glob) { $arguments['glob'] = $glob }
    $arguments['ignoreCase'] = $ignoreCase
    return $arguments
}

function Get-ShellReason {
    param([string]$Fired, [string]$Command, [string]$BaseDir)

    $arguments = Read-ShellSearch -Command $Command -BaseDir $BaseDir
    $fill = ''
    if ($null -eq $arguments) {
        $arguments = [ordered]@{ pattern = '<.NET regex>'; folder = '<absolute folder>'; ignoreCase = '<true|false>' }
        $fill = "`nThis hook reads the pattern and folder out of a command only when it is one plain rg, grep or Select-String; this one is not, so fill in pattern, folder and ignoreCase (a JSON true or false). Add glob, a file-name wildcard such as *.cs, to narrow by file name."
    }

    return @"
Graph guard: $Fired is a text search over C# source. Run it through razorgraph instead -- one call returns the graph's name and literal hits and the disk's text hits together, with no line reported twice. A Grep inside a repo is answered that way automatically.

ToolSearch("select:mcp__razorgraph__grep")
$(Format-Call -Arguments $arguments -Offset 0 -DiskOffset 0)$fill

Git history and diffs pass this guard. For a repo with no C#, set JANET_GRAPH_GUARD=off.
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
        $fired = Get-ShellTrigger -ToolName $toolName -ToolInput $toolInput
        if (-not $fired) { exit 0 }

        # No solution means no C# graph worth sending the search to.
        $projectDir = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { $cwd }
        if ($null -eq (Get-SolutionFile $projectDir)) { exit 0 }

        Write-Deny (Get-ShellReason -Fired $fired -Command ([string](Get-Prop $toolInput 'command')) -BaseDir $cwd)
    }
}
catch {
    # A broken guard must fail open, not wedge every search in the session -- and say so.
    Write-PassContext "Graph guard failed ($(Format-OneLine $_.Exception.Message)), so the call ran unguarded."
    exit 0
}

exit 0
