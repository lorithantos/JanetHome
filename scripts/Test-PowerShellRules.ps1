<#
.SYNOPSIS
    Lints .ps1 files against the PowerShell house rules that are mechanically checkable.

.DESCRIPTION
    Enforces the subset of notes\powershell-house-rules.md that a machine can verify:
    the 'return @()' unroll trap, $null comparison order, and truncating file
    creation. Every file is also parsed, which catches syntax errors that make a whole
    script unloadable rather than just one line wrong.

    Rules that need judgment -- probe optional properties, catch in startup paths --
    stay in the note. The point of this script is that the checkable ones do not rely
    on anyone remembering to reread it.

    Standalone by design: no git, no PSScriptAnalyzer, no Pester. It has to run in a
    bare checkout or it will not get run.

.PARAMETER Path
    Files or directories to check. Defaults to the repo's scripts directory.

.PARAMETER Target
    'Ps7' (default) checks only the edition-independent rules -- the ones 7 does not
    rescue you from, which is most of them. 'Ps51' additionally flags 7-only syntax
    and encoding names, for scripts that must still run under Windows PowerShell 5.1
    (the shipped-in-Windows default, and therefore whatever a shared script lands on).

.PARAMETER ExcludeRule
    Rule ids to skip, e.g. 'NullComparison'.

.PARAMETER Text
    Formatted output instead of JSON. Findings are JSON by default: they are meant to be
    acted on, and file/line/rule are more useful structured than aligned in columns.

.PARAMETER Pretty
    Indent the JSON.

.OUTPUTS
    JSON: { filesChecked, target, engine, violations, findings[] } where each finding is
    { file, line, rule, text, message }.
    Exit code 0 when clean, 1 when any violation is found -- in both output modes.

.EXAMPLE
    & "$env:JanetBase\scripts\Test-PowerShellRules.ps1"

.EXAMPLE
    & "$env:JanetBase\scripts\Test-PowerShellRules.ps1" -Path .\scripts\New-TextFile.ps1 -Json

.EXAMPLE
    & "$env:JanetBase\scripts\Test-PowerShellRules.ps1" -Target Ps51
    Adds the backward-compatibility checks, for scripts that may run on 5.1.
#>
[CmdletBinding()]
param(
    [string[]]$Path,
    [ValidateSet('Ps7', 'Ps51')]
    [string]$Target = 'Ps7',
    [string[]]$ExcludeRule = @(),
    [switch]$Text,
    [switch]$Pretty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Path) { $Path = @((Join-Path $repoRoot 'scripts')) }

# Each rule is a regex plus the failure it prevents. 'skipComments' drops matches
# inside comment/doc-comment lines, which keeps rules from firing on their own
# examples or on a comment explaining the trap.
#
# 'target' is the edition the rule protects. Ps51 rules are backward-compatibility
# checks -- 7 accepts the syntax fine. Everything unmarked is edition-independent:
# unrolling, comparison semantics, and -Force truncation are core behavior that
# upgrading to 7 does not rescue you from.
$rules = @(
    @{
        id      = 'ReturnEmptyArray'
        # Not anchored to end-of-line: 'if (...) { return @() }' is the same bug and
        # the anchored version silently missed it.
        pattern = '\breturn\s+@\(\s*\)'
        message = "'return @()' unrolls to nothing; caller gets `$null and .Count throws under StrictMode. Use 'return ,@()' and assign the result."
        skipComments = $true
    },
    @{
        id      = 'NullComparison'
        pattern = '\$\w+(\.\w+)*\s+-(eq|ne)\s+\$null\b'
        message = 'Put $null on the left: $x -eq $null returns an array when $x is an array.'
        skipComments = $true
    },
    @{
        id      = 'TruncatingNewItem'
        pattern = 'New-Item\b(?=[^\r\n]*-ItemType\s+File)(?=[^\r\n]*-Force)'
        message = 'New-Item -ItemType File -Force truncates an existing file. Guard with Test-Path first.'
        skipComments = $true
    },
    @{
        id      = 'Ps7Encoding'
        target  = 'Ps51'
        pattern = '-Encoding\s+(utf8NoBOM|utf8BOM|ansi|oem)\b'
        message = "PowerShell 7-only -Encoding value; on 5.1 it throws after the script may have already printed success. Use [System.IO.File]::WriteAllText with UTF8Encoding(`$false)."
        skipComments = $true
    },
    @{
        id      = 'ChainOperator'
        target  = 'Ps51'
        pattern = '(?<![&|])(\&\&|\|\|)(?![&|])'
        message = 'Pipeline chain operators are 7+; in 5.1 this is a parser error that makes the entire file unloadable. Use ";" with an explicit if ($?).'
        skipComments = $true
    }
)

$activeRules = @(
    $rules |
        Where-Object { $ExcludeRule -notcontains $_.id } |
        Where-Object {
            # Unmarked rules always apply; Ps51 rules only when asked for.
            -not $_.ContainsKey('target') -or $_.target -eq $Target
        }
)

# ---- Collect files ---------------------------------------------------------

$files = @()
foreach ($p in $Path) {
    if (-not (Test-Path $p)) { throw "Path not found: $p" }
    if (Test-Path $p -PathType Container) {
        $files += @(Get-ChildItem -Path $p -Filter *.ps1 -Recurse -File)
    }
    else {
        $files += @(Get-Item $p)
    }
}
$files = @($files | Sort-Object FullName -Unique)

if ($files.Count -eq 0) {
    Write-Host 'No .ps1 files to check.' -ForegroundColor Yellow
    exit 0
}

# ---- Check -----------------------------------------------------------------

$findings = @()

foreach ($file in $files) {
    $relative = $file.FullName.Replace("$repoRoot\", '')
    # @() is load-bearing: Get-Content returns a bare String for a single-line file,
    # and .Count on a String throws under StrictMode -- the linter used to crash on
    # any one-line script.
    $lines = @(Get-Content $file.FullName)

    # Parse first. A parser error means every regex finding below is noise.
    # Tokens are captured too: they are the only reliable way to find comments,
    # since a line inside a <# #> block starts with ordinary prose.
    $parseErrors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $file.FullName, [ref]$tokens, [ref]$parseErrors)

    if ($parseErrors -and $parseErrors.Count -gt 0) {
        foreach ($pe in $parseErrors) {
            $findings += [PSCustomObject]@{
                file = $relative; line = $pe.Extent.StartLineNumber
                rule = 'ParseError'; text = $pe.Extent.Text
                message = $pe.Message
            }
        }
        continue
    }

    # A hit inside a string literal is never real code -- it is a message, an example,
    # or this linter's own rule table describing the pattern it looks for. Collect
    # string extents from the AST and suppress matches that fall inside them, rather
    # than trying to write regexes that cannot match themselves.
    $stringSpans = @{}   # line number -> list of @{ start; end } 1-based column ranges
    # QUOTED strings only. PowerShell parses barewords as StringConstantExpressionAst
    # too -- command names and unquoted arguments included -- so suppressing every
    # "string" silently killed whole rules: 'Set-Content -Encoding utf8NoBOM' was
    # invisible because both 'Set-Content' and 'utf8NoBOM' are string constants.
    $stringAsts = $ast.FindAll({
            ($args[0] -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
             $args[0] -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) -and
            $args[0].Extent.Text -match '^[''"]'
        }, $true)

    $suppressExtents = @($stringAsts | ForEach-Object { $_.Extent })

    # Comment tokens cover both '# line' and multi-line '<# ... #>' blocks. The old
    # line-prefix heuristic only caught lines that began with # or . or <#, so prose
    # inside a doc-comment was treated as live code -- which is how this linter came
    # to flag its own .DESCRIPTION.
    if ($tokens) {
        $suppressExtents += @(
            $tokens |
                Where-Object { $_.Kind -eq [System.Management.Automation.Language.TokenKind]::Comment } |
                ForEach-Object { $_.Extent }
        )
    }

    foreach ($ext in $suppressExtents) {
        for ($ln = $ext.StartLineNumber; $ln -le $ext.EndLineNumber; $ln++) {
            if (-not $stringSpans.ContainsKey($ln)) { $stringSpans[$ln] = @() }
            $start = if ($ln -eq $ext.StartLineNumber) { $ext.StartColumnNumber } else { 1 }
            $end = if ($ln -eq $ext.EndLineNumber) { $ext.EndColumnNumber } else { [int]::MaxValue }
            $stringSpans[$ln] += @{ start = $start; end = $end }
        }
    }

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $lineNo = $i + 1
        $trimmed = $line.TrimStart()
        $isComment = $trimmed.StartsWith('#') -or $trimmed.StartsWith('.') -or $trimmed.StartsWith('<#')
        $spans = if ($stringSpans.ContainsKey($lineNo)) { @($stringSpans[$lineNo]) } else { @() }

        foreach ($rule in $activeRules) {
            if ($rule.skipComments -and $isComment) { continue }

            foreach ($m in [regex]::Matches($line, $rule.pattern)) {
                $matchStart = $m.Index + 1
                $matchEnd = $m.Index + $m.Length

                $inString = $false
                foreach ($span in $spans) {
                    if ($matchStart -ge $span.start -and $matchEnd -le $span.end) { $inString = $true; break }
                }
                if ($inString) { continue }

                $findings += [PSCustomObject]@{
                    file = $relative; line = $lineNo
                    rule = $rule.id; text = $trimmed
                    message = $rule.message
                }
            }
        }
    }
}

# ---- Report ----------------------------------------------------------------

if (-not $Text) {
    # Wrapped rather than a bare array: the run's context (what was checked, against
    # which target, on which engine) is what makes a clean result meaningful.
    $result = [PSCustomObject]@{
        filesChecked = $files.Count
        target       = $Target
        engine       = $PSVersionTable.PSVersion.ToString()
        violations   = $findings.Count
        findings     = @($findings)
    }
    if ($Pretty) { $result | ConvertTo-Json -Depth 4 }
    else { $result | ConvertTo-Json -Depth 4 -Compress }
    if ($findings.Count -gt 0) { exit 1 }
    exit 0
}

Write-Host ''
Write-Host "PowerShell house rules -- $($files.Count) file$(if ($files.Count -ne 1) {'s'}) checked, target $Target, engine $($PSVersionTable.PSVersion)" -ForegroundColor Cyan

if ($findings.Count -eq 0) {
    Write-Host '  clean' -ForegroundColor Green
    Write-Host ''
    exit 0
}

foreach ($g in ($findings | Group-Object file | Sort-Object Name)) {
    Write-Host ''
    Write-Host "  $($g.Name)" -ForegroundColor Yellow
    foreach ($f in ($g.Group | Sort-Object line)) {
        Write-Host "    line $($f.line)  [$($f.rule)]"
        Write-Host "      $($f.text)" -ForegroundColor DarkGray
        Write-Host "      $($f.message)" -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host "$($findings.Count) violation$(if ($findings.Count -ne 1) {'s'}). See notes\powershell-house-rules.md" -ForegroundColor Red
Write-Host ''
exit 1
