<#
.SYNOPSIS
    Deep-nesting (christmas-tree) detector for PowerShell, by AST — the PS backend
    of the style gate's nesting rule.

.DESCRIPTION
    The PowerShell sibling of RazorGraph's deep_methods: reports every function (and
    top-level script body) whose control flow nests at least MinDepth levels, deepest
    first. Depth is syntactic — how many indentation steps of control flow a reader
    must hold to reach the deepest statement — computed from the language's own
    parser, not regex over text.

    Semantics mirror RazorGraph's NestingDepth where the languages correspond:
    an elseif chain adds no depth (free in PowerShell — elseif clauses hang off the
    same IfStatementAst). One deliberate divergence: script-block literals passed to
    commands (ForEach-Object { }, Where-Object { }, .SetAction handlers) count one
    level, because they indent and pipeline-style trees would otherwise vanish from
    the report. Function definition bodies do not count — the function is the unit
    being measured, not a level inside itself.

    Exit code 1 when findings exist, 0 when clean — usable directly as a gate step.

.PARAMETER Path
    Files or directories. Directories are searched recursively for *.ps1 and *.psm1.

.PARAMETER MinDepth
    Minimum nesting depth to report (default 3).

.PARAMETER Text
    Formatted output instead of JSON.

.OUTPUTS
    JSON: { minDepth, filesScanned, returned, findings[], parseErrors[] }
    Each finding is { file, function, line, depth, deepestLine }; function is
    '<script>' for top-level code. parseErrors are reported, never silently skipped.

.EXAMPLE
    & "$env:JanetBase\scripts\Test-DeepNesting.ps1" -Path "$env:JanetBase\scripts" -MinDepth 3 -Text
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Path,

    [int]$MinDepth = 3,
    [switch]$Text
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- Collect files ----------------------------------------------------------

$files = @()
foreach ($p in $Path) {
    if (-not (Test-Path $p)) { throw "Path not found: $p" }
    $item = Get-Item $p
    if ($item.PSIsContainer) {
        $files += @(Get-ChildItem $p -Recurse -File -Include '*.ps1', '*.psm1')
    }
    else {
        $files += $item
    }
}

# ---- Depth semantics --------------------------------------------------------

# The constructs a reader indents for. IfStatementAst covers elseif for free:
# elseif clauses are entries in the same node's Clauses collection, so a chain
# is one level however long it grows. An explicit `else { if ... }` is a nested
# IfStatementAst and does count — it indents.
function Test-AddsNesting {
    param([System.Management.Automation.Language.Ast]$Node)
    return (
        $Node -is [System.Management.Automation.Language.IfStatementAst] -or
        $Node -is [System.Management.Automation.Language.ForEachStatementAst] -or
        $Node -is [System.Management.Automation.Language.ForStatementAst] -or
        $Node -is [System.Management.Automation.Language.WhileStatementAst] -or
        $Node -is [System.Management.Automation.Language.DoWhileStatementAst] -or
        $Node -is [System.Management.Automation.Language.DoUntilStatementAst] -or
        $Node -is [System.Management.Automation.Language.SwitchStatementAst] -or
        $Node -is [System.Management.Automation.Language.TryStatementAst] -or
        $Node -is [System.Management.Automation.Language.TrapStatementAst] -or
        $Node -is [System.Management.Automation.Language.ScriptBlockExpressionAst]
    )
}

# Innermost enclosing function, or $null for top-level code. The walk stops at
# the first function boundary so nested functions measure themselves, exactly as
# methods do in the C# backend.
function Find-Container {
    param([System.Management.Automation.Language.Ast]$Node)
    for ($a = $Node.Parent; $null -ne $a; $a = $a.Parent) {
        if ($a -is [System.Management.Automation.Language.FunctionDefinitionAst]) { return $a }
    }
    return $null
}

$findings = @()
$parseErrors = @()

foreach ($file in $files) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)

    # A file the parser rejects is a finding for some other gate, but silence
    # here would make this one claim more coverage than it delivered.
    if ($errors -and $errors.Count -gt 0) {
        $parseErrors += [PSCustomObject]@{
            file = $file.FullName
            message = $errors[0].Message
            line = $errors[0].Extent.StartLineNumber
        }
        continue
    }

    $nestingNodes = $ast.FindAll({ Test-AddsNesting $args[0] }, $true)

    # depth of a nesting node = itself plus every nesting ancestor inside the
    # same container; the container's report is the max over its nodes.
    $byContainer = @{}
    foreach ($node in $nestingNodes) {
        $depth = 1
        $container = $null
        for ($a = $node.Parent; $null -ne $a; $a = $a.Parent) {
            if ($a -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $container = $a; break }
            if (Test-AddsNesting $a) { $depth++ }
        }

        $name = if ($container) { $container.Name } else { '<script>' }
        $line = if ($container) { $container.Extent.StartLineNumber } else { 1 }
        $key = "$($file.FullName)::$name::$line"

        if (-not $byContainer.ContainsKey($key) -or $byContainer[$key].depth -lt $depth) {
            $byContainer[$key] = [PSCustomObject]@{
                file = $file.FullName
                function = $name
                line = $line
                depth = $depth
                deepestLine = $node.Extent.StartLineNumber
            }
        }
    }

    $findings += @($byContainer.Values | Where-Object { $_.depth -ge $MinDepth })
}

$findings = @($findings | Sort-Object -Property @{ Expression = 'depth'; Descending = $true }, file, line)

$result = [PSCustomObject]@{
    minDepth = $MinDepth
    filesScanned = $files.Count
    returned = $findings.Count
    findings = @($findings)
    parseErrors = @($parseErrors)
}

if ($Text) {
    Write-Host "$($findings.Count) function(s) at nesting depth >= $MinDepth across $($files.Count) file(s):"
    foreach ($f in $findings) {
        Write-Host ("  depth {0}: {1} ({2}:{3}, deepest at line {4})" -f $f.depth, $f.function, $f.file, $f.line, $f.deepestLine)
    }
    foreach ($e in $parseErrors) { Write-Warning "parse error, not scanned: $($e.file):$($e.line) $($e.message)" }
}
else {
    $result | ConvertTo-Json -Depth 5 -Compress
}

if ($findings.Count -gt 0 -or $parseErrors.Count -gt 0) { exit 1 }
exit 0
