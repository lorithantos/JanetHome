#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\Invoke-GraphGuard.ps1 -- what it refuses, what it lets
    through, and the false positives that were found by being bitten.

.DESCRIPTION
    The guard denies a text search over C# source until the session has asked the
    RazorGraph code graph. It had no tests until 2026-09-06, and on that day it fired
    three times on commands that were not code searches at all: a `git status`, a
    `git diff` piped into Select-String, and a byte-level encoding check. Each denial
    named a graph query that could not have answered the question, because the graph
    models one built state of the source and indexes neither history nor bytes.

    So these tests exist to hold the BOUNDARY rather than the behaviour. The deny cases
    are the ones the guard was written for and are pinned so a future exemption cannot
    quietly widen into them; the pass cases are the ones it got wrong.

    Each test drives the script with -InputJson and its own transcript under TestDrive.
    Pipe input does not bind to a [string] parameter, which is why the script documents
    -InputJson for exactly this.

    A deny is a JSON envelope carrying permissionDecision 'deny'; a pass is SILENCE and
    exit 0. Asserting on emptiness rather than on an exit code is deliberate: the script
    fails open in every error path, so exit 0 alone would pass even when the guard has
    crashed, and a test that cannot tell working from broken is worse than none.
#>

BeforeAll {
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:guard = Join-Path $script:repoRoot 'scripts\Invoke-GraphGuard.ps1'

    # A transcript the guard reads as "the graph has not been asked recently". Its shape
    # is the harness's: one JSON object per line, tool calls inside an assistant message.
    $script:coldTranscript = Join-Path $TestDrive 'cold.jsonl'
    '{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read"}]}}' |
        Set-Content -LiteralPath $script:coldTranscript -Encoding utf8

    # The same, with a graph call in it. Any mcp__razorgraph__ name clears the guard.
    $script:warmTranscript = Join-Path $TestDrive 'warm.jsonl'
    '{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__razorgraph__find_nodes"}]}}' |
        Set-Content -LiteralPath $script:warmTranscript -Encoding utf8

    # A project directory holding a solution, since a repo with no .slnx has no graph to
    # ask and the guard fails open there. A stub file is enough -- the guard only needs
    # the name, to derive a graphId for the denial text.
    $script:projectDir = Join-Path $TestDrive 'repo'
    New-Item -ItemType Directory -Path $script:projectDir | Out-Null
    Set-Content -LiteralPath (Join-Path $script:projectDir 'Stub.slnx') -Value '<Solution />' -Encoding utf8

    function Invoke-Guard {
        <#
            Runs the guard on one command and answers 'deny' or 'pass'.
            CLAUDE_PROJECT_DIR is set per call rather than in BeforeAll: the real hook
            environment may already carry one, and a test that inherits it would resolve
            a different solution and stop testing what it says it tests.
        #>
        [CmdletBinding()]
        param(
            [string]$Command,
            [hashtable]$GrepInput,
            [string]$ToolName = 'PowerShell',
            [string]$Transcript = $script:coldTranscript
        )

        $toolInput = if ($GrepInput) { $GrepInput } else { @{ command = $Command } }
        $payload = @{
            tool_name       = $ToolName
            tool_input      = $toolInput
            transcript_path = $Transcript
        } | ConvertTo-Json -Compress -Depth 6

        $previous = $env:CLAUDE_PROJECT_DIR
        $env:CLAUDE_PROJECT_DIR = $script:projectDir
        try {
            $out = & $script:guard -InputJson $payload 2>&1 | Out-String
        }
        finally {
            $env:CLAUDE_PROJECT_DIR = $previous
        }

        if ($out -match '"permissionDecision"\s*:\s*"deny"') { return 'deny' }
        return 'pass'
    }
}

Describe 'Invoke-GraphGuard' {

    Context 'git is not a code search' {
        # All three of these were denied on 2026-09-06. The graph models one built state
        # of the source: it cannot answer what changed, who changed it, or when, so a
        # denial here sends the caller to a tool that has no answer to give.

        It 'lets a git diff piped into a search go through' {
            # The exact command that was refused: a version constant, read out of a diff.
            Invoke-Guard -Command 'git -C C:\repos\RazorGraphTool diff -- src/RazorGraph.Core/Serialization/GraphFormat.cs | Select-String "1.3"' |
                Should -Be 'pass'
        }

        It 'lets a git status beside a C# path go through' {
            # Every case in this context carries a search tool ON PURPOSE. Written
            # without one, the command passes whether the exemption exists or not --
            # the shell trigger needs a search tool AND a C# path -- so the test would
            # be green for a reason unrelated to what it claims to check. Found by
            # mutation on 2026-09-06: removing the exemption failed one test of three,
            # and the two survivors were this shape.
            Invoke-Guard -Command 'git status --short; git diff -- src/Foo.cs | findstr Bar' |
                Should -Be 'pass'
        }

        It 'lets the git log pickaxe search history' {
            # -S searches COMMITS for a string. The nearest graph query answers a
            # different question about a different point in time.
            Invoke-Guard -Command 'git log -S"WavePlan" -- src/Nav.Worlds/*.cs | Select-String commit' |
                Should -Be 'pass'
        }

        It 'still refuses git grep, which searches the working tree like grep' {
            # The exception that keeps the exemption honest: same question, different
            # spelling. If this ever passes, the exemption has swallowed the guard.
            Invoke-Guard -Command 'git grep -n "WavePlan" -- "*.cs"' | Should -Be 'deny'
        }
    }

    Context 'the searches it was written for' {
        # Regression pins. These are the 2026-09-05 incident's own shapes, and no
        # exemption added later may quietly cover them.

        It 'refuses ripgrep over a src tree' {
            Invoke-Guard -Command 'rg "WavePlan" src/' | Should -Be 'deny'
        }

        It 'refuses Select-String over a .cs file' {
            Invoke-Guard -Command 'Select-String -Path src\Foo.cs -Pattern Bar' | Should -Be 'deny'
        }

        It 'refuses the Grep tool narrowed to a C# glob' {
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; glob = '*.cs' } |
                Should -Be 'deny'
        }

        It 'lets a search for another kind of file through' {
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; glob = '*.ps1' } |
                Should -Be 'pass'
        }
    }

    Context 'clearance and the escape hatch' {

        It 'passes once a razorgraph call is in the recent transcript' {
            Invoke-Guard -Command 'rg "WavePlan" src/' -Transcript $script:warmTranscript |
                Should -Be 'pass'
        }

        It 'passes everything when JANET_GRAPH_GUARD is off' {
            $previous = $env:JANET_GRAPH_GUARD
            $env:JANET_GRAPH_GUARD = 'off'
            try {
                Invoke-Guard -Command 'rg "WavePlan" src/' | Should -Be 'pass'
            }
            finally {
                $env:JANET_GRAPH_GUARD = $previous
            }
        }

        It 'fails open when the transcript does not exist' {
            # A guard that cannot read the session must not wedge it.
            Invoke-Guard -Command 'rg "WavePlan" src/' -Transcript (Join-Path $TestDrive 'absent.jsonl') |
                Should -Be 'pass'
        }
    }

    Context 'known false positive, pinned so a fix is deliberate' {

        It 'still refuses a byte-level encoding check over C# sources' {
            # FOUND 2026-09-06 AND NOT FIXED. The question is which files contain a
            # non-ASCII byte -- the graph indexes neither bytes nor line endings, so this
            # denial is wrong in the same way the git ones were. It is left standing
            # because the shell trigger ignores the search PATTERN entirely, and reading
            # a pattern out of arbitrary shell text is the kind of heuristic that rots;
            # the Grep tool's own trigger already does better by testing whether the
            # pattern is identifier-shaped.
            #
            # If you fix it, this test SHOULD fail. Change it to 'pass' then, rather than
            # deleting it -- the case is worth keeping either way.
            Invoke-Guard -Command 'Get-ChildItem src -Filter *.cs | Select-String -Pattern ([char]0x2014)' |
                Should -Be 'deny'
        }
    }
}
