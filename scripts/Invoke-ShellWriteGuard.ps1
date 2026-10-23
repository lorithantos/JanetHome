<#
.SYNOPSIS
    PreToolUse hook: refuses a Bash or PowerShell command that writes a file the
    Write|Edit hooks govern, or C# source, inside a git working tree, and says to use
    Edit or Write.

.DESCRIPTION
    Every hook that guards an edit here matches the Write and Edit tools and nothing
    else, so `sed -i ... scripts/Foo.ps1`, a heredoc redirected onto a script, or a
    Set-Content onto research.json passed every one of them -- live-probed 2026-10-23.
    The harness's auto mode tells the model to make small edits with sed and heredocs,
    so the hole was being pointed at, not merely open. Lori decided the same day: a
    hook denies them (thread item 7).

    THE GUARDED SET IS DERIVED FROM THE HOOKS IT PROTECTS, not chosen here -- except C#
    source, which was decided rather than derived:

      research.json, research.candidate.json,   Invoke-EditGuard.ps1 denies Write and
      research.previous.json,                    Edit of these leaf names (its
      research.candidate.base.json               -GuardedFile default).
      *.ps1                                      Invoke-LintHook.ps1 lints every .ps1
                                                 after Write or Edit, and
                                                 Invoke-ResearchGuard.ps1 gates Write of
                                                 a NEW .ps1 into a scripts directory
                                                 (a subset of *.ps1).
      *.cs, *.razor, *.csproj, *.sln, *.slnx     No hook governs an Edit of these; the
                                                 reason is the edit itself (2026-10-23,
                                                 thread item 84, Lori: deny shell writes
                                                 to C# source too). An Edit or Write
                                                 shows the exact change in the session,
                                                 where it can be reviewed, and the code
                                                 graph is rebuilt from that source; a
                                                 sed -i or Set-Content rewrite leaves no
                                                 reviewable change behind. The list is
                                                 $script:SourceExtensions in
                                                 lib\ShellCommand.ps1, the same one the
                                                 graph guard denies shell READS of.

    A shell write to one of those, inside a git working tree, is denied. What counts as
    a write: an output redirection (>, >>, &>, PowerShell's > and 2>), sed -i and
    perl -i, tee and Tee-Object, Set-Content, Add-Content, Clear-Content, Out-File,
    New-Item, cp, mv, copy, move, Copy-Item and Move-Item onto
    the file or into a directory as it, Rename-Item (ren, rni) onto it, and the [IO.File]
    writers (WriteAll*, AppendAll*,
    AppendText, Create, CreateText, OpenWrite, and the destination of Copy, Move and
    Replace). `(Get-Content f) -replace 'a','b' | Set-Content f` is a Set-Content.
    Commands nested in pwsh -Command, bash -c and cmd /c are judged too, and cd is
    followed.

    What passes: git, dotnet and janet -- what they write themselves (git checkout,
    git mv, janet research update, which is the sanctioned way to change research.json),
    though a shell redirect of their output onto a guarded file is still the shell
    writing it; every read; and any write whose target lies outside a git working tree,
    such as a scratchpad. dotnet passes whole, `dotnet new`, `dotnet format`,
    `dotnet add package` and `dotnet sln add` included: each writes .cs, .csproj or
    .sln files by its own rules, as a project tool and not as a text rewrite, and what
    it changes shows up in git diff like any other edit -- denying them would forbid the
    tools that own those files. A target this cannot resolve (a variable, an expression)
    whose text still names a guarded file lets the command run with one line of
    additionalContext; one that names nothing guarded passes silently.

    A separate hook rather than a verdict inside Invoke-GraphGuard.ps1, although both
    read shell commands through scripts\lib\ShellCommand.ps1: the graph guard's rule is
    relabelled ADVISORY whenever razorgraph is switched off (startup-manifest.json's
    mcpDependents) and is disabled by JANET_GRAPH_GUARD=off, and neither has anything to
    do with whether a script may be rewritten by sed.

.PARAMETER InputJson
    Hook payload, for testing. Normally omitted -- the real invocation gets it on stdin.
    Pipe input does not bind to a [string] parameter, so tests must pass -InputJson.

.OUTPUTS
    A PreToolUse deny, a PreToolUse additionalContext note, or nothing.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-ShellWriteGuard.ps1" -InputJson '{"tool_name":"Bash","tool_input":{"command":"sed -i s/a/b/ scripts/Foo.ps1"},"cwd":"C:\\repos\\lorithantos\\JanetHome"}'

.NOTES
    Wired via BOTH .claude\settings.json and ~\.claude\settings.json as a PreToolUse
    hook on Bash|PowerShell, the same two-level pattern as every other guard here.

    $env:JANET_SHELL_WRITE_GUARD = 'off' disables it.

    Exits 0 always -- a hook that crashes must not block unrelated work. Never
    permissionDecision 'allow': it would also skip the permission prompt.
#>
[CmdletBinding()]
param(
    [string]$InputJson
)

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'lib\ShellCommand.ps1')

$script:ResearchLeaves = @('research.json', 'research.candidate.json', 'research.previous.json', 'research.candidate.base.json')
$script:RecordLeaves = @('research.previous.json', 'research.candidate.base.json')
# SourcePrefilter and SourceExtensions (C# source) come from lib\ShellCommand.ps1.
$script:Prefilter = "(?i)research|\.ps1|$script:SourcePrefilter"
$script:GuardedText = "(?i)research[\w.]*\.json|\.ps1\b|$script:SourcePrefilter"
$script:ContentWriters = @('Set-Content', 'Add-Content', 'Clear-Content', 'Out-File', 'ac', 'clc')
$script:PsCopiers = @('Copy-Item', 'Move-Item', 'cpi', 'mi')
$script:Renamers = @('Rename-Item', 'ren', 'rni')
$script:AnyCopiers = @('cp', 'mv', 'copy', 'move')
$script:PsValued = @('Path', 'LiteralPath', 'FilePath', 'PSPath', 'LP', 'Value', 'Encoding', 'Stream', 'Filter', 'Include', 'Exclude',
    'Credential', 'Delimiter', 'Width', 'InputObject', 'ItemType', 'Name', 'Destination', 'ToSession', 'FromSession', 'Variable', 'Target')
$script:PathNames = @('Path', 'LiteralPath', 'FilePath', 'PSPath', 'LP')
# [IO.File] member -> which argument is the file it writes.
$script:FileWriteMembers = @{
    WriteAllText = 0; WriteAllLines = 0; WriteAllBytes = 0; AppendAllText = 0; AppendAllLines = 0
    AppendText = 0; Create = 0; CreateText = 0; OpenWrite = 0; Copy = 1; Move = 1; Replace = 1
}
$script:WorkTrees = @{}

function Get-Prop {
    param($Object, [string]$Name)
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
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

function Format-OneLine {
    param([string]$Text, [int]$Max = 300)
    $flat = ([string]$Text -replace '\s+', ' ').Trim()
    if ($flat.Length -gt $Max) { $flat = $flat.Substring(0, $Max) + '...' }
    return $flat
}

# One write the command makes: the tool that makes it, the word naming the file, and for
# a copy or move the source word, since a destination directory takes the source's name.
function New-WriteTarget {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory object; changes no system state.')]
    param([string]$Tool, $Word, $Source = $null)
    [PSCustomObject]@{ Tool = $Tool; Word = $Word; Source = $Source }
}

# PowerShell-style arguments: named parameters (resolved by unique prefix against the
# writers' parameter names, or the Valued list a caller passes) and positionals.
function Read-PsArgument {
    param($Words, [string[]]$Valued = $script:PsValued)
    $parsed = [PSCustomObject]@{ Named = @{}; Positional = (New-Object System.Collections.Generic.List[object]) }
    for ($at = 0; $at -lt $Words.Count; $at++) {
        $word = $Words[$at]
        if (-not ($word.Bare -and $word.Text -match '^-([A-Za-z]\w*)$')) { $parsed.Positional.Add($word); continue }
        $typed = $Matches[1]
        $name = @($Valued | Where-Object { $_ -eq $typed })
        if ($name.Count -eq 0) { $name = @($Valued | Where-Object { $_.StartsWith($typed, [StringComparison]::OrdinalIgnoreCase) }) }
        if ($name.Count -ne 1) { continue }
        $value = $word.Value
        if ($null -eq $value -and $at + 1 -lt $Words.Count) { $at++; $value = $Words[$at] }
        if ($null -ne $value) { $parsed.Named[$name[0]] = $value }
    }
    return $parsed
}

# POSIX-style arguments: clusters of short flags, a value after a valued flag (attached or
# the next word), --long and --long=value, and -- ending the options. Named is keyed
# case-sensitively: perl -I is not -i. sed's and perl's -i[SUFFIX] is read as a flag
# followed by more flags; no suffix changes which files the command writes.
function Read-PosixArgument {
    param($Words, [string]$Valued = '', [string[]]$ValuedLong = @())
    $parsed = [PSCustomObject]@{
        Named      = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
        Positional = (New-Object System.Collections.Generic.List[object])
    }
    $done = $false
    for ($at = 0; $at -lt $Words.Count; $at++) {
        $word = $Words[$at]
        $text = $word.Text
        if (-not $done -and $word.Bare -and $text -eq '--') { $done = $true; continue }
        if ($done -or -not $word.Bare -or $text -notmatch '^-.' ) { $parsed.Positional.Add($word); continue }
        if ($text.StartsWith('--')) {
            $long = $text.Substring(2)
            $eq = $long.IndexOf('=')
            if ($eq -ge 0) { $parsed.Named[$long.Substring(0, $eq)] = New-ShellWord -Text $long.Substring($eq + 1) -Literal $word.Literal; continue }
            if ($ValuedLong -contains $long -and $at + 1 -lt $Words.Count) { $at++; $parsed.Named[$long] = $Words[$at]; continue }
            $parsed.Named[$long] = New-ShellWord -Text ''
            continue
        }
        $at = Step-PosixCluster -Parsed $parsed -Words $Words -At $at -Valued $Valued
    }
    return $parsed
}

# One cluster of short flags at At; returns the index of the last word it consumed.
function Step-PosixCluster {
    param($Parsed, $Words, [int]$At, [string]$Valued)
    $word = $Words[$At]
    $cluster = $word.Text.Substring(1)
    for ($k = 0; $k -lt $cluster.Length; $k++) {
        $flag = [string]$cluster[$k]
        $rest = $cluster.Substring($k + 1)
        if (-not $Valued.Contains($flag)) { $Parsed.Named[$flag] = New-ShellWord -Text ''; continue }
        if ($rest) { $Parsed.Named[$flag] = New-ShellWord -Text $rest -Literal $word.Literal; return $At }
        if ($At + 1 -lt $Words.Count) { $At++; $Parsed.Named[$flag] = $Words[$At] }
        return $At
    }
    return $At
}

function Get-NamedPath {
    param($Parsed)
    foreach ($name in $script:PathNames) { if ($Parsed.Named.ContainsKey($name)) { return $Parsed.Named[$name] } }
    if ($Parsed.Positional.Count -gt 0) { return $Parsed.Positional[0] }
    return $null
}

# sed -i and perl -i: every file operand, past the script when it was not given with -e.
function Get-InPlaceTarget {
    param([string]$Verb, $Words)
    $found = New-Object System.Collections.Generic.List[object]
    if ($Verb -eq 'sed') { $parsed = Read-PosixArgument -Words $Words -Valued 'efl' -ValuedLong @('expression', 'file', 'line-length') }
    else { $parsed = Read-PosixArgument -Words $Words -Valued 'eEIMm' }
    if (-not ($parsed.Named.ContainsKey('i') -or $parsed.Named.ContainsKey('in-place'))) { return ,$found }
    $scripted = @('e', 'E', 'f', 'expression', 'file' | Where-Object { $parsed.Named.ContainsKey($_) }).Count -gt 0
    $skip = if ($scripted) { 0 } else { 1 }
    for ($k = $skip; $k -lt $parsed.Positional.Count; $k++) { $found.Add((New-WriteTarget -Tool "$Verb -i" -Word $parsed.Positional[$k])) }
    return ,$found
}

# cp, mv, copy, move, Copy-Item, Move-Item: the destination, each paired with a source so
# that a destination directory can take the source's file name.
function Get-CopyTarget {
    param([string]$Verb, $Words, [string]$Shell)
    $found = New-Object System.Collections.Generic.List[object]
    $sources = New-Object System.Collections.Generic.List[object]
    $destination = $null
    if ($script:PsCopiers -contains $Verb -or $Shell -eq 'PowerShell') {
        $parsed = Read-PsArgument $Words
        $destination = if ($parsed.Named.ContainsKey('Destination')) { $parsed.Named['Destination'] } elseif ($parsed.Positional.Count -gt 1) { $parsed.Positional[1] } else { $null }
        $from = Get-NamedPath $parsed
        if ($null -ne $from) { $sources.Add($from) }
    }
    else {
        $parsed = Read-PosixArgument -Words $Words -Valued 'tS' -ValuedLong @('target-directory', 'suffix')
        $positional = $parsed.Positional
        foreach ($key in @('t', 'target-directory')) { if ($parsed.Named.ContainsKey($key)) { $destination = $parsed.Named[$key] } }
        if ($null -eq $destination -and $positional.Count -gt 1) { $destination = $positional[$positional.Count - 1]; $positional = Get-WordRange $positional 0; $positional.RemoveAt($positional.Count - 1) }
        if ($null -ne $destination) { $sources.AddRange($positional) }
    }
    if ($null -eq $destination) { return ,$found }
    if ($sources.Count -eq 0) { $found.Add((New-WriteTarget -Tool $Verb -Word $destination)) }
    foreach ($source in $sources) { $found.Add((New-WriteTarget -Tool $Verb -Word $destination -Source $source)) }
    return ,$found
}

# Rename-Item, ren and rni: the new name, which lands in the directory of -Path unless it
# is a path of its own. -NewName is positional after -Path, so it is the first positional
# when -Path was named and the second when it was not. A -Path the shell would expand
# leaves the directory unknown, so the new name is judged as unresolvable.
function Get-RenameTarget {
    param([string]$Verb, $Words)
    $found = New-Object System.Collections.Generic.List[object]
    $parsed = Read-PsArgument -Words $Words -Valued ($script:PsValued + 'NewName')
    $path = Get-NamedPath $parsed
    $nameAt = if (@($script:PathNames | Where-Object { $parsed.Named.ContainsKey($_) }).Count -gt 0) { 0 } else { 1 }
    $newName = if ($parsed.Named.ContainsKey('NewName')) { $parsed.Named['NewName'] } elseif ($parsed.Positional.Count -gt $nameAt) { $parsed.Positional[$nameAt] } else { $null }
    if ($null -eq $newName) { return ,$found }
    $word = $newName
    $rooted = [System.IO.Path]::IsPathRooted((ConvertFrom-BashPath $newName.Text))
    if ($null -ne $path -and -not $rooted) {
        $parent = Split-Path -Parent $path.Text
        if (-not $path.Literal) { $word = New-ShellWord -Text $newName.Text -Literal $false }
        elseif ($parent) { $word = New-ShellWord -Text (Join-Path $parent $newName.Text) -Literal $newName.Literal }
    }
    $found.Add((New-WriteTarget -Tool $Verb -Word $word))
    return ,$found
}

# The files one command writes by its own verb (redirections are handled by the caller).
function Get-VerbTarget {
    param([string]$Verb, $Words, [string]$Shell)
    $found = New-Object System.Collections.Generic.List[object]
    if ($Verb -in 'sed', 'perl') { return (Get-InPlaceTarget -Verb $Verb -Words $Words) }
    if ($script:PsCopiers -contains $Verb -or $script:AnyCopiers -contains $Verb) { return (Get-CopyTarget -Verb $Verb -Words $Words -Shell $Shell) }
    if ($script:Renamers -contains $Verb) { return (Get-RenameTarget -Verb $Verb -Words $Words) }
    if ($Verb -eq 'tee') {
        $parsed = Read-PosixArgument -Words $Words
        foreach ($word in $parsed.Positional) { $found.Add((New-WriteTarget -Tool 'tee' -Word $word)) }
        return ,$found
    }
    if ($script:ContentWriters -contains $Verb -or $Verb -eq 'Tee-Object') {
        $path = Get-NamedPath (Read-PsArgument $Words)
        if ($null -ne $path) { $found.Add((New-WriteTarget -Tool $Verb -Word $path)) }
        return ,$found
    }
    if ($Verb -in 'New-Item', 'ni') {
        $parsed = Read-PsArgument $Words
        $path = Get-NamedPath $parsed
        if ($parsed.Named.ContainsKey('Name')) {
            $name = $parsed.Named['Name']
            if ($null -eq $path) { $path = $name }
            else { $path = New-ShellWord -Text (Join-Path $path.Text $name.Text) -Literal ($path.Literal -and $name.Literal) }
        }
        if ($null -ne $path) { $found.Add((New-WriteTarget -Tool $Verb -Word $path)) }
    }
    return ,$found
}

# The full paths one target item writes: itself, or for a copy into a directory, that
# directory plus each source's name. Nothing when it is relative to a directory this
# could not follow, is not a legal path, or copies a source it cannot read -- and then
# Unsure hears about it if the text still names a guarded file.
function Resolve-WritePath {
    param($Target, [string]$Text, [string]$BaseDir, $Unsure)
    $paths = New-Object System.Collections.Generic.List[string]
    $path = ConvertFrom-BashPath $Text
    $full = $null
    if ([System.IO.Path]::IsPathRooted($path) -or $BaseDir) {
        try { $full = Resolve-FullPath -Path $path -BaseDir $BaseDir } catch { $full = $null }
    }
    if ($null -eq $full) {
        if ($Text -match $script:GuardedText) { $Unsure.Add($Text) }
        return ,$paths
    }

    $source = $Target.Source
    $intoDirectory = $null -ne $source -and (($Text -match '[\\/]$') -or (Test-Path -LiteralPath $full -PathType Container))
    if (-not $intoDirectory) { $paths.Add($full); return ,$paths }
    if (-not $source.Literal) {
        if ($source.Text -match $script:GuardedText) { $Unsure.Add($source.Text) }
        return ,$paths
    }
    foreach ($item in @($source.Items)) { $paths.Add((Join-Path $full (@($item -split '[\\/]')[-1]))) }
    return ,$paths
}

function Test-GuardedLeaf {
    param([string]$Leaf)
    return ($script:ResearchLeaves -contains $Leaf -or $Leaf -like '*.ps1' -or (Test-SourceLeaf $Leaf))
}

function Test-SourceLeaf {
    param([string]$Leaf)
    return ($script:SourceExtensions -contains [System.IO.Path]::GetExtension($Leaf))
}

function Test-PathInRepo {
    param([string]$FullPath)
    $probe = Split-Path -Parent $FullPath
    while ($probe -and -not (Test-Path -LiteralPath $probe -PathType Container)) { $probe = Split-Path -Parent $probe }
    if (-not $probe) { return $false }
    if (-not $script:WorkTrees.ContainsKey($probe)) { $script:WorkTrees[$probe] = Test-InsideGitWorkTree $probe }
    return $script:WorkTrees[$probe]
}

# Judges one write. Returns the denial's { Tool, Path } when it writes a guarded file
# inside a repo, and $null otherwise; an unresolvable word naming a guarded file is
# added to Unsure.
function Test-WriteTarget {
    param($Target, [string]$BaseDir, $Unsure)
    $word = $Target.Word
    if (-not $word.Literal) {
        if ($word.Text -match $script:GuardedText) { $Unsure.Add($word.Text) }
        return $null
    }
    foreach ($item in @($word.Items)) {
        foreach ($full in (Resolve-WritePath -Target $Target -Text $item -BaseDir $BaseDir -Unsure $Unsure)) {
            if (-not (Test-GuardedLeaf (Split-Path -Leaf $full))) { continue }
            if (Test-PathInRepo $full) { return [PSCustomObject]@{ Tool = $Target.Tool; Path = $full } }
        }
    }
    return $null
}

function Get-NextLocation {
    param($Words, [string]$Location)
    foreach ($word in $Words) {
        if ($word.Bare -and $word.Text -match '^-.' -and $null -eq $word.Value) { continue }
        $operand = if ($null -ne $word.Value) { $word.Value } else { $word }
        if (-not $operand.Literal -or $operand.Text -eq '-') { return $null }
        return (Resolve-ShellDirectory -Text $operand.Text -BaseDir $Location)
    }
    return $HOME
}

# The writes in one segment: its output redirections, then what its verb writes. git,
# dotnet and janet are no writer verb, so what they write themselves passes; a redirect
# of their output is the shell's.
function Get-SegmentWrite {
    param($Segment, [string]$Shell)
    $found = New-Object System.Collections.Generic.List[object]
    foreach ($word in $Segment.OutputFiles) { $found.Add((New-WriteTarget -Tool 'redirection' -Word $word)) }
    $words = $Segment.Words
    $at = Get-VerbIndex $words
    if ($at -lt 0) { return ,$found }
    $verb = Get-VerbName $words[$at].Text
    $byVerb = Get-VerbTarget -Verb $verb -Words (Get-WordRange $words ($at + 1)) -Shell $Shell
    foreach ($target in $byVerb) { $found.Add($target) }
    return ,$found
}

# Write is set to { Tool, Path } when some command writes a guarded file inside a git
# working tree; Unsure names the targets that could not be resolved but name one.
function Get-WriteVerdict {
    param([string]$Command, [string]$Shell, [string]$BaseDir, [int]$Depth = 0)
    $verdict = [PSCustomObject]@{ Write = $null; Unsure = (New-Object System.Collections.Generic.List[string]) }
    $pipelines = if ($Shell -eq 'PowerShell') { Split-PowerShellCommand $Command } else { Split-BashCommand $Command }
    $location = $BaseDir

    foreach ($segments in $pipelines) {
        foreach ($segment in $segments) {
            $words = $segment.Words
            $at = Get-VerbIndex $words
            $verb = if ($at -ge 0) { Get-VerbName $words[$at].Text } else { '' }
            $rest = if ($at -ge 0) { Get-WordRange $words ($at + 1) } else { $null }

            if ($script:LocationVerbs -contains $verb) { $location = Get-NextLocation -Words $rest -Location $location; continue }

            $inner = if ($at -ge 0) { Get-InnerCommand -Verb $verb -Words $rest } else { $null }
            if ($null -ne $inner -and $Depth -lt 3) {
                $nested = Get-WriteVerdict -Command $inner.Text -Shell $inner.Shell -BaseDir $location -Depth ($Depth + 1)
                if ($null -ne $nested.Write) { return $nested }
                $verdict.Unsure.AddRange($nested.Unsure)
            }

            foreach ($target in (Get-SegmentWrite -Segment $segment -Shell $Shell)) {
                $hit = Test-WriteTarget -Target $target -BaseDir $location -Unsure $verdict.Unsure
                if ($null -ne $hit) { $verdict.Write = $hit; return $verdict }
            }
        }
    }

    if ($Shell -eq 'PowerShell') {
        foreach ($call in (Get-FileMemberCall $Command)) {
            if (-not $script:FileWriteMembers.ContainsKey($call.Member)) { continue }
            $index = $script:FileWriteMembers[$call.Member]
            if ($call.Arguments.Count -le $index) { continue }
            $target = New-WriteTarget -Tool "[IO.File]::$($call.Member)" -Word $call.Arguments[$index]
            $hit = Test-WriteTarget -Target $target -BaseDir $BaseDir -Unsure $verdict.Unsure
            if ($null -ne $hit) { $verdict.Write = $hit; return $verdict }
        }
    }
    return $verdict
}

function Get-WriteReason {
    param([string]$ToolName, [string]$Command, $Write)
    $leaf = Split-Path -Leaf $Write.Path
    $shown = Format-OneLine $Command 140

    $why = "Every hook that governs that file matches the Write and Edit tools only, so a shell write routes around all of them -- which is why it is refused here."
    $instead = if ($script:RecordLeaves -contains $leaf) {
        "$leaf is the record the research cutover swap reasons from, and it has no write path at all: Edit and Write are refused on it too. If the live catalog needs a change, change research.json through 'janet research update' or 'janet research add'."
    }
    elseif ($script:ResearchLeaves -contains $leaf) {
        "Change the catalog through 'janet research add' or 'janet research update' (research_add / research_update over MCP, or scripts\Add-ResearchNode.ps1 / Update-ResearchNode.ps1), which validate ids and links and splice into the file's text. Edit and Write are refused on $leaf as well."
    }
    elseif (Test-SourceLeaf $leaf) {
        $why = 'C# source is changed through the Edit and Write tools here, never by a shell rewrite.'
        "Make the change with the Edit or Write tool instead: the edit is then visible in the session as the exact change made, where it can be reviewed, and the code graph razorgraph answers from is rebuilt from that source. A sed, Set-Content or redirect rewrite of C# leaves nothing to review. To rename or move a file, git mv passes; dotnet (new, format, add, sln) passes too."
    }
    else {
        "Make the change with the Edit or Write tool instead, so the hooks fire: the lint hook checks every .ps1 after Write or Edit against notes\powershell-house-rules.md, and the research guard asks the catalog before a NEW script is created."
    }

    return @"
Shell write guard: $ToolName command '$shown' writes $($Write.Path) through $($Write.Tool). $why

$instead

Only shell writes to guarded files inside a git working tree are refused: research.json and its candidate, previous and base copies, *.ps1, and C# source (.cs, .razor, .csproj, .sln, .slnx). git, dotnet and the janet CLI pass, as do reads and any write outside a repo (a scratchpad file, for one). If the harness suggested editing with sed or a heredoc, that advice does not hold here.
"@
}

try {
    if ($env:JANET_SHELL_WRITE_GUARD -eq 'off') { exit 0 }

    $raw = if ($InputJson) { $InputJson } else { [Console]::In.ReadToEnd() }
    if (-not $raw) { exit 0 }

    $payload = $raw | ConvertFrom-Json
    $toolName = [string](Get-Prop $payload 'tool_name')
    if ($toolName -ne 'Bash' -and $toolName -ne 'PowerShell') { exit 0 }

    $command = [string](Get-Prop (Get-Prop $payload 'tool_input') 'command')
    if (-not $command -or $command -notmatch $script:Prefilter) { exit 0 }

    $cwd = if (Get-Prop $payload 'cwd') { [string](Get-Prop $payload 'cwd') }
    elseif ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR }
    else { (Get-Location).Path }

    $verdict = Get-WriteVerdict -Command $command -Shell $toolName -BaseDir $cwd
    if ($null -ne $verdict.Write) {
        Write-Deny (Get-WriteReason -ToolName $toolName -Command $command -Write $verdict.Write)
    }
    elseif ($verdict.Unsure.Count -gt 0) {
        $unsure = Format-OneLine ((@($verdict.Unsure) | Select-Object -Unique) -join ', ') 120
        Write-PassContext "Shell write guard could not resolve where this $toolName command writes ($unsure), so it ran. A shell write to research.json, a .ps1 or C# source inside a repo is refused: use the Edit or Write tool, or janet research for the catalog."
    }
}
catch {
    # A broken guard must fail open, not wedge every shell command in the session -- and say so.
    Write-PassContext "Shell write guard failed ($(Format-OneLine $_.Exception.Message)), so the call ran unguarded."
    exit 0
}

exit 0
