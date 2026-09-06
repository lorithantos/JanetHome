<#
.SYNOPSIS
    PreToolUse hook: refuses a text search over C# source until the session has asked
    the RazorGraph code graph.

.DESCRIPTION
    Graph first, at the controller's own keyboard. Invoke-DelegationGuard.ps1 gates the
    prompt a subagent is handed; this gates the grep the session itself is about to run.
    Same failure, one layer up: on 2026-09-05 four greps for callers, implementers and
    blast radius ran against C# in one session while two prose rules and a skill said
    "graph first", and the Roslyn graph server that answers those questions correctly
    -- grep cannot see a call through an interface, a generic, or a partial class --
    sat idle on port 7718. The incident is on note.subagent-delegation.

    An advisory rule was in force and was skipped four times. That is the argument for
    a gate, and it is the argument the research guard makes in its own comments: an
    agent does not skip a step from fatigue, it skips from rationalisation, and "I only
    need one name" is the rationalisation.

    Deliberately narrow, because a guard that fires on everything gets clicked past:

      Grep            fires when the search is aimed at C# -- glob or type naming .cs,
                      a path ending in .cs or carrying a src\ segment -- or when there
                      is no narrowing to a non-C# kind and the pattern is a bare
                      identifier, which is the shape of "who calls Foo". A pattern with
                      regex metacharacters, or a glob naming ps1/json/md, passes.
      Bash/PowerShell fires when the command runs grep, rg, Select-String, sls or
                      findstr AND names .cs or a src\ path in the same command.
      anything else   passes.

    Clearance is per session, read from the transcript the harness names in the
    payload: if any of the last 12 tool_use blocks is an mcp__razorgraph__ call, the
    session is using the graph and this grep is a spot-check, so it passes. The
    research guard's shared temp trace lets one session clear another's guard; this
    one must not, which is why the transcript and not a trace file is the record.

    The denial does the work. It names the first query, with the identifier taken
    from the pattern and the graphId derived from the solution file on disk, so the
    fix is a paste rather than a scolding.

    Fails OPEN: no transcript, an unreadable one, no solution file under the project
    dir, or any exception -- exit 0 and let the call through. A broken guard must not
    wedge the session, and a repo with no .slnx has no graph to ask.

.PARAMETER InputJson
    Hook payload, for testing. Normally omitted -- the real invocation gets it on stdin.
    Pipe input does not bind to a [string] parameter, so tests must pass -InputJson.

.OUTPUTS
    A PreToolUse deny decision, or nothing when the call is fine.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-GraphGuard.ps1" -InputJson '{"tool_name":"Grep","tool_input":{"pattern":"ThreadStore"},"transcript_path":"C:\\t.jsonl"}'

.NOTES
    Wired via BOTH .claude\settings.json and ~\.claude\settings.json as a PreToolUse
    hook on Grep|Bash|PowerShell. The project-level file arms it when the project dir
    is JanetHome; the user-level file arms it by absolute path everywhere else.

    $env:JANET_GRAPH_GUARD = 'off' disables it, for a repo with no C# in it.

    Exits 0 always -- a hook that crashes must not block unrelated work.
#>
[CmdletBinding()]
param(
    [string]$InputJson
)

Set-StrictMode -Version Latest

$script:ClearanceWindow = 12

function Get-Prop {
    param($Object, [string]$Name)
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
}

# Name the real solution file, so the pasted block is correct rather than a template.
# Shallowest match wins: a repo's own .slnx sits above any sample or fixture one.
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

# A glob or ripgrep type that names C#.
function Test-CSharpKind {
    param([string]$Glob, [string]$Type)

    if ($Type -and ($Type -eq 'cs' -or $Type -eq 'csharp')) { return $true }
    if ($Glob -and ($Glob -match '\.cs\b' -or $Glob -match '\{[^}]*\bcs\b')) { return $true }
    return $false
}

# A path that only occurs in a C# repo: a .cs file, or a src\ segment.
function Test-CSharpPath {
    param([string]$Path)

    if (-not $Path) { return $false }
    if ($Path -match '\.cs$') { return $true }
    if ($Path -match '(^|[\\/])src([\\/]|$)') { return $true }
    return $false
}

# A directory that holds no C# is not a C# search, whatever the pattern looks like.
# Bounded probe: a few levels, first hit wins.
function Test-DirectoryHasCSharp {
    param([string]$Directory)

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return $true }
    $hit = Get-ChildItem -LiteralPath $Directory -Filter '*.cs' -File -Recurse -Depth 3 -ErrorAction SilentlyContinue |
        Select-Object -First 1
    return ($null -ne $hit)
}

# The Grep tool's trigger. Returns a one-line description of what fired, or $null.
function Get-GrepTrigger {
    param($ToolInput)

    $pattern = [string](Get-Prop $ToolInput 'pattern')
    $glob = [string](Get-Prop $ToolInput 'glob')
    $type = [string](Get-Prop $ToolInput 'type')
    $path = [string](Get-Prop $ToolInput 'path')

    if (-not $pattern) { return $null }

    $where = @()
    if ($glob) { $where += "glob $glob" }
    if ($type) { $where += "type $type" }
    if ($path) { $where += "path $path" }
    $whereText = if ($where.Count -gt 0) { ' (' + ($where -join ', ') + ')' } else { '' }
    $fired = "Grep pattern '$pattern'$whereText"

    if (Test-CSharpKind -Glob $glob -Type $type) { return $fired }
    if (Test-CSharpPath $path) { return $fired }

    # A glob or type naming something else is a search for something else.
    if ($glob -or $type) { return $null }

    # A path to a file of another kind, or a directory with no C# in it, likewise.
    if ($path) {
        $ext = [System.IO.Path]::GetExtension($path)
        if ($ext -and $ext -ne '.cs') { return $null }
        if (-not (Test-DirectoryHasCSharp $path)) { return $null }
    }

    # No narrowing, and the pattern is the shape of "who uses Foo".
    if ($pattern -match '^[A-Za-z_][A-Za-z0-9_.]*$') { return $fired }
    return $null
}

# The shell trigger: a text-search command that names C# source in the same breath.
function Get-ShellTrigger {
    param([string]$ToolName, $ToolInput)

    $command = [string](Get-Prop $ToolInput 'command')
    if (-not $command) { return $null }
    if ($command -notmatch '\b(grep|rg|Select-String|sls|findstr)\b') { return $null }
    if ($command -notmatch '\.cs\b' -and $command -notmatch 'src[\\/]') { return $null }

    $shown = $command -replace '\s+', ' '
    if ($shown.Length -gt 140) { $shown = $shown.Substring(0, 140) + '...' }
    return "$ToolName command '$shown'"
}

# Names of the last N tool_use blocks in the session transcript, newest first.
# Any line that fails to parse is skipped. Returns a comma-wrapped array.
function Get-RecentToolNames {
    param([string]$TranscriptPath, [int]$Count)

    $names = New-Object System.Collections.Generic.List[string]
    if (-not $TranscriptPath -or -not (Test-Path -LiteralPath $TranscriptPath -PathType Leaf)) { return ,@() }

    # Shared read: the harness holds the file open for append.
    $stream = [System.IO.File]::Open($TranscriptPath, 'Open', 'Read', 'ReadWrite')
    try {
        $reader = New-Object System.IO.StreamReader($stream)
        $text = $reader.ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }

    $lines = @($text -split "`n")
    for ($i = $lines.Count - 1; $i -ge 0 -and $names.Count -lt $Count; $i--) {
        $line = $lines[$i].Trim()
        if (-not $line) { continue }

        try { $entry = $line | ConvertFrom-Json } catch { continue }
        if ((Get-Prop $entry 'type') -ne 'assistant') { continue }

        $content = Get-Prop (Get-Prop $entry 'message') 'content'
        if ($null -eq $content -or $content -is [string]) { continue }

        # Blocks within one message are in order; walk them newest first too.
        $blocks = @($content)
        for ($b = $blocks.Count - 1; $b -ge 0 -and $names.Count -lt $Count; $b--) {
            $block = $blocks[$b]
            if ((Get-Prop $block 'type') -ne 'tool_use') { continue }
            $name = [string](Get-Prop $block 'name')
            if ($name) { $names.Add($name) }
        }
    }

    return ,@($names.ToArray())
}

# The symbol to look up first: a bare identifier as given, else the last PascalCase
# token in the text, else nothing.
function Get-QueryIdentifier {
    param([string]$Text)

    if ($Text -match '^[A-Za-z_][A-Za-z0-9_.]*$') { return $Text }
    $tokens = [regex]::Matches($Text, '\b[A-Z][A-Za-z0-9]*[a-z][A-Za-z0-9]*\b')
    if ($tokens.Count -gt 0) { return $tokens[$tokens.Count - 1].Value }
    return $null
}

try {
    if ($env:JANET_GRAPH_GUARD -eq 'off') { exit 0 }

    $raw = if ($InputJson) { $InputJson } else { [Console]::In.ReadToEnd() }
    if (-not $raw) { exit 0 }

    $payload = $raw | ConvertFrom-Json
    $toolName = [string](Get-Prop $payload 'tool_name')
    $toolInput = Get-Prop $payload 'tool_input'

    $fired = $null
    $searchText = $null
    switch ($toolName) {
        'Grep' {
            $fired = Get-GrepTrigger $toolInput
            $searchText = [string](Get-Prop $toolInput 'pattern')
        }
        { $_ -eq 'Bash' -or $_ -eq 'PowerShell' } {
            $fired = Get-ShellTrigger -ToolName $toolName -ToolInput $toolInput
            $searchText = [string](Get-Prop $toolInput 'command')
        }
    }
    if (-not $fired) { exit 0 }

    # Per-session clearance: the graph has been asked recently, so this is a spot-check.
    # No transcript, or one that cannot be read, fails open.
    $transcriptPath = [string](Get-Prop $payload 'transcript_path')
    if (-not $transcriptPath) { exit 0 }
    if (-not (Test-Path -LiteralPath $transcriptPath -PathType Leaf)) { exit 0 }

    $recent = Get-RecentToolNames -TranscriptPath $transcriptPath -Count $script:ClearanceWindow
    foreach ($name in $recent) {
        if ($name -like 'mcp__razorgraph__*') { exit 0 }
    }

    # No solution means no graph, and then grep is the legitimate tool.
    $projectDir = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR }
    elseif (Get-Prop $payload 'cwd') { [string](Get-Prop $payload 'cwd') }
    else { (Get-Location).Path }
    $solution = Get-SolutionFile $projectDir
    if ($null -eq $solution) { exit 0 }

    $graphId = [System.IO.Path]::GetFileNameWithoutExtension($solution.Name).ToLowerInvariant()
    $graphBlock = @"
Get a graph: call list_graphs; use graphId "$graphId" (built from $($solution.FullName)).
If it is not listed: load_graph path=<saved .json> if one exists, else
build_solution path=$($solution.FullName) graphId=$graphId (slow: a full Roslyn compile).
"@

    $identifier = Get-QueryIdentifier $searchText
    $firstQuery = if ($identifier) {
        "find_nodes graphId=`"$graphId`" nameContains=`"$identifier`""
    }
    else {
        "find_nodes graphId=`"$graphId`" nameContains=`"<the symbol you are looking for>`""
    }

    $reason = @"
Graph guard: $fired is a text search over C# source, and no mcp__razorgraph__ call appears in the last $script:ClearanceWindow tool calls of this session. The Roslyn graph answers callers, implementers and blast radius correctly; grep cannot see a call through an interface, a generic or a partial class.

Load the tools: ToolSearch("select:mcp__razorgraph__list_graphs,mcp__razorgraph__find_nodes,mcp__razorgraph__get_node,mcp__razorgraph__find_path,mcp__razorgraph__research")
$graphBlock
First query:  $firstQuery
Then callers: get_node graphId="$graphId" id=<id from find_nodes> edges=incoming edgeType=Calls
Pass graphId on every call. Ids are exact: m:Type.Name(paramTypes). Check 'truncated' in every result.

Text search is second, for two things: (a) spot-checking a claim the graph made, and (b) text the graph does not index -- prose, JSON, .ps1, config, string literals. One razorgraph call clears this guard for the next $script:ClearanceWindow tool calls. For a repo with no C#, set JANET_GRAPH_GUARD=off.
"@

    [PSCustomObject]@{
        hookSpecificOutput = [PSCustomObject]@{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'deny'
            permissionDecisionReason = $reason
        }
    } | ConvertTo-Json -Depth 4 -Compress
}
catch {
    # A broken guard must fail open, not wedge every search in the session.
    exit 0
}

exit 0
