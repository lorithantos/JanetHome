<#
.SYNOPSIS
    Split a Bash or PowerShell command line into pipelines, commands and words, so a hook
    can ask what each command reads or writes.

.DESCRIPTION
    Shared by the two hooks that judge shell commands:

      - Invoke-GraphGuard.ps1 asks what a search (or a reader of source) READS.
      - Invoke-ShellWriteGuard.ps1 asks what a command WRITES.

    Moved here out of Invoke-GraphGuard.ps1 on 2026-10-23, unchanged except that a
    segment now keeps its output redirection targets (OutputFiles) as well as its input
    files, and PowerShell's static [IO.File] member calls can be listed. Two copies of a
    tokenizer drift; one copy is what both hooks are tested against.

    PowerShell is split by its own parser (System.Management.Automation.Language); Bash by
    the small tokenizer below -- quotes, escapes, $(...) and backticks kept inside their
    word, redirections set aside, heredoc bodies skipped as data. Not a shell: enough to
    say which command each word belongs to.

    Dot-source this file. It defines functions and four script-scope tables
    ($script:CommandPrefixes, $script:LocationVerbs, $script:SourceExtensions and
    $script:SourcePrefilter) in the caller's script scope.
#>

$script:CommandPrefixes = @('sudo', 'command', 'builtin', 'time', 'nice', 'nohup', 'exec', 'if', 'then', 'else', 'elif', 'do', 'while', 'until', '!')
$script:LocationVerbs = @('cd', 'chdir', 'pushd', 'Set-Location', 'sl', 'Push-Location')

# C# source, as both hooks mean it: Invoke-GraphGuard.ps1 denies a shell READ of these
# inside a repo, Invoke-ShellWriteGuard.ps1 a shell WRITE. One list, so the two cannot
# disagree about what source is. SourcePrefilter is the same set as a regex over text.
$script:SourceExtensions = @('.cs', '.razor', '.csproj', '.sln', '.slnx')
$script:SourcePrefilter = '(?i)\.(' + (@($script:SourceExtensions | ForEach-Object { [regex]::Escape($_.Substring(1)) }) -join '|') + ')\b'

function Resolve-FullPath {
    param([string]$Path, [string]$BaseDir)
    if (-not [System.IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $BaseDir $Path }
    return [System.IO.Path]::GetFullPath($Path)
}

function Test-InsideGitWorkTree {
    param([string]$Directory)
    $answer = & git -C $Directory rev-parse --is-inside-work-tree 2>$null
    return ($LASTEXITCODE -eq 0 -and ([string]$answer).Trim() -eq 'true')
}

# One command-line word. Literal is false when the shell would expand it (a variable, a
# subexpression), so it cannot be resolved here. Bare is false when it starts quoted,
# so it cannot be an option. Items holds a PowerShell array argument's elements.
function New-ShellWord {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory object; changes no system state.')]
    param([string]$Text, [bool]$Literal = $true, [bool]$Bare = $true, [string[]]$Items)
    $elements = if ($null -ne $Items -and $Items.Count -gt 0) { $Items } else { @($Text) }
    [PSCustomObject]@{ Text = $Text; Literal = $Literal; Bare = $Bare; Items = $elements; Value = $null }
}

# InputFiles: files fed in with <. OutputFiles: targets of >, >> and their kin.
function New-ShellSegment {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory object; changes no system state.')]
    param()
    [PSCustomObject]@{
        Words       = New-Object System.Collections.Generic.List[object]
        InputFiles  = New-Object System.Collections.Generic.List[object]
        OutputFiles = New-Object System.Collections.Generic.List[object]
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
# File redirections (> and >>, any stream) are kept as OutputFiles; 2>&1 is not a file.
function ConvertFrom-PipelineElement {
    param($Element)
    $segment = New-ShellSegment
    foreach ($redirection in @($Element.Redirections)) {
        if ($redirection -is [System.Management.Automation.Language.FileRedirectionAst]) {
            $segment.OutputFiles.Add((ConvertFrom-ArgumentAst $redirection.Location))
        }
    }
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

# Static member calls on [System.IO.File] in a PowerShell command -- [IO.File]::WriteAllText(
# 'x', $y) and the like -- as { Member, Arguments } with each argument a shell word. These
# read and write files without any command word, so a pipeline walk never sees them.
function Get-FileMemberCall {
    param([string]$Text)
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors)
    $calls = New-Object System.Collections.Generic.List[object]
    $found = $ast.FindAll({ param($candidate) $candidate -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true)
    foreach ($call in $found) {
        if (-not $call.Static -or $call.Expression -isnot [System.Management.Automation.Language.TypeExpressionAst]) { continue }
        if ($call.Expression.TypeName.FullName -notmatch '^(System\.)?IO\.File$') { continue }
        if ($call.Member -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) { continue }
        $arguments = New-Object System.Collections.Generic.List[object]
        foreach ($argument in @($call.Arguments)) { if ($null -ne $argument) { $arguments.Add((ConvertFrom-ArgumentAst $argument)) } }
        $calls.Add([PSCustomObject]@{ Member = $call.Member.Value; Arguments = $arguments })
    }
    return ,$calls
}

function New-BashState {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory object; changes no system state.')]
    param()
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
    if ($redirect -eq 'out') { $State.Segment.OutputFiles.Add((New-ShellWord -Text $text -Literal $wasLiteral -Bare $false)); return }
    if ($redirect) { return }
    # A brace group's braces are words of their own in bash, and they bound commands.
    if ($wasBare -and ($text -eq '{' -or $text -eq '}')) { Complete-BashPipeline $State; return }
    $State.Segment.Words.Add((New-ShellWord -Text $text -Literal $wasLiteral -Bare $wasBare))
}

function Complete-BashSegment {
    param($State)
    Complete-BashWord $State
    $segment = $State.Segment
    if ($segment.Words.Count -gt 0 -or $segment.InputFiles.Count -gt 0 -or $segment.OutputFiles.Count -gt 0) { $State.Segments.Add($segment) }
    $State.Segment = New-ShellSegment
}

function Complete-BashPipeline {
    param($State)
    Complete-BashSegment $State
    if ($State.Segments.Count -gt 0) { $State.Pipelines.Add($State.Segments) }
    $State.Segments = New-Object System.Collections.Generic.List[object]
}

# One redirection operator at Index ('<' or '>'), returning the index after it: an input
# file, an output target, a heredoc delimiter, a here-string or a descriptor duplication,
# recorded on State. Split out of Split-BashCommand to keep its nesting within the gate's
# limit.
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
        if ($i -lt $n -and $Text[$i] -eq '<') { $State.Redirect = 'herestring'; return $i + 1 }
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
# their word, redirections set aside (input and output files kept), heredoc bodies
# skipped as data. Not a shell: enough to say which command each word belongs to.
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
