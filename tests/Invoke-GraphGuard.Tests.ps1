#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\Invoke-GraphGuard.ps1 -- what it answers, what it refuses,
    what it lets through, and the false positives that were found by being bitten.

.DESCRIPTION
    Since 2026-10-11 the guard ANSWERS a Grep inside a git working tree through
    razorgraph's grep and hands the results back inside the denial. Since 2026-10-12 it
    refuses ANY Bash or PowerShell search that reads files inside a git working tree with
    that call ready to paste; before then only a search over C# was. Before that it denied both until
    the session had made any razorgraph call, and agents cleared it with one throwaway
    call and grepped anyway.

    The shell half had no tests until 2026-09-06, and on that day it fired three times
    on commands that were not code searches at all: a `git status`, a `git diff` piped
    into Select-String, and a byte-level encoding check. Those cases hold the BOUNDARY,
    and are kept as they were.

    Each test drives the script with -InputJson. Pipe input does not bind to a [string]
    parameter, which is why the script documents -InputJson for exactly this. The MCP
    call and the server start are injected (-McpCall, -EnsureServer), so no test needs
    a live razorgraph server, and every call is recorded so a test can assert what was
    ASKED as well as what came back.

    A deny is a JSON envelope carrying permissionDecision 'deny'; a pass is SILENCE or
    an additionalContext note and exit 0. Asserting on the envelope rather than on an
    exit code is deliberate: the script fails open in every error path, so exit 0 alone
    would pass even when the guard has crashed.
#>

BeforeAll {
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:guard = Join-Path $script:repoRoot 'scripts\Invoke-GraphGuard.ps1'

    # The transcript is no longer read. These remain so the tests can show that a graph
    # call in it no longer buys clearance.
    $script:coldTranscript = Join-Path $TestDrive 'cold.jsonl'
    '{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read"}]}}' |
        Set-Content -LiteralPath $script:coldTranscript -Encoding utf8
    $script:warmTranscript = Join-Path $TestDrive 'warm.jsonl'
    '{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__razorgraph__find_nodes"}]}}' |
        Set-Content -LiteralPath $script:warmTranscript -Encoding utf8

    # A git working tree. Its solution is a leftover: the shell trigger needed one until
    # 2026-10-12. TestDrive sits under %TEMP%, outside any repo.
    $script:projectDir = Join-Path $TestDrive 'repo'
    New-Item -ItemType Directory -Path (Join-Path $script:projectDir 'src') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $script:projectDir 'Stub.slnx') -Value '<Solution />' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $script:projectDir 'src\Foo.cs') -Value 'class WavePlan { }' -Encoding utf8
    & git -C $script:projectDir init -q

    # A folder outside any git working tree.
    $script:plainDir = Join-Path $TestDrive 'plain'
    New-Item -ItemType Directory -Path $script:plainDir | Out-Null

    # A repo for the hook's own scan: a case pair in a .cs, the same word in a .ps1, and
    # a binary that would match if it were read.
    $script:scanRepo = Join-Path $TestDrive 'scan'
    New-Item -ItemType Directory -Path $script:scanRepo | Out-Null
    Set-Content -LiteralPath (Join-Path $script:scanRepo 'a.cs') -Value @('// header', 'var p = new WavePlan();', 'var q = waveplan;') -Encoding utf8
    Set-Content -LiteralPath (Join-Path $script:scanRepo 'b.ps1') -Value '$x = "WavePlan"' -Encoding utf8
    [System.IO.File]::WriteAllBytes((Join-Path $script:scanRepo 'c.dat'),
        [byte[]](@(0x57, 0x61, 0x76, 0x65, 0x50, 0x6C, 0x61, 0x6E, 0x00, 0x0A)))
    & git -C $script:scanRepo init -q

    function New-GrepEnvelope {
        <#
            A razorgraph grep answer wrapped the way Invoke-McpTool.ps1 returns it.
            Hits are hashtables of the real shape; totals default to the hit counts.
        #>
        [CmdletBinding()]
        param(
            [object[]]$GraphHits = @(),
            [object[]]$DiskHits = @(),
            [int]$GraphTotal = -1,
            [int]$DiskTotal = -1,
            [switch]$NoGraph
        )
        if ($GraphTotal -lt 0) { $GraphTotal = $GraphHits.Count }
        if ($DiskTotal -lt 0) { $DiskTotal = $DiskHits.Count }

        $graph = if ($NoGraph) { @{ reason = 'no loaded graph covers folder'; hits = @() } }
        else {
            @{
                graphId = 'stub'; builtAt = '2026-10-11T08:00:00Z'; reason = 'covers folder'
                returned = $GraphHits.Count; totalMatches = $GraphTotal; offset = 0
                truncated = ($GraphTotal -gt $GraphHits.Count); nextOffset = $GraphHits.Count
                hits = $GraphHits
            }
        }
        $disk = @{
            returned = $DiskHits.Count; totalMatches = $DiskTotal; offset = 0
            truncated = ($DiskTotal -gt $DiskHits.Count); nextOffset = $DiskHits.Count
            filesScanned = 7; filesSkipped = 0; alreadyInGraph = 1; hits = $DiskHits
        }
        return (@{ ok = $true; tool = 'grep'; result = @{ graph = $graph; disk = $disk } } | ConvertTo-Json -Depth 10 -Compress)
    }

    $script:sampleAnswer = New-GrepEnvelope `
        -GraphHits @(@{ nodeId = 'type:Nav.WavePlan'; nodeType = 'Class'; name = 'WavePlan'; matchedOn = 'name'; path = 'src\Foo.cs'; line = 12 }) `
        -DiskHits @(
            @{ path = 'src\Foo.cs'; line = 30; text = 'return new WavePlan();' },
            @{ path = 'src\Bar.cs'; line = 40; text = 'var plan = new WavePlan();' },
            @{ path = 'src\Bar.cs'; line = 41; text = 'plan = WavePlan.Empty;' })

    # Replies are dequeued per call: a string is the envelope, a scriptblock is run (so
    # it can throw). An empty queue answers with the sample.
    $global:GraphGuardMcp = [PSCustomObject]@{
        Calls   = New-Object System.Collections.Generic.List[hashtable]
        Replies = New-Object System.Collections.Generic.Queue[object]
        Default = $script:sampleAnswer
    }
    $global:GraphGuardEnsure = [PSCustomObject]@{ Calls = 0; Reply = '{"ok":false,"state":"no-server-binary","error":"no binary"}' }

    $script:fakeMcp = {
        param([hashtable]$Arguments)
        $global:GraphGuardMcp.Calls.Add($Arguments)
        if ($global:GraphGuardMcp.Replies.Count -eq 0) { return $global:GraphGuardMcp.Default }
        $reply = $global:GraphGuardMcp.Replies.Dequeue()
        if ($reply -is [scriptblock]) { return (& $reply) }
        return $reply
    }
    $script:fakeEnsure = {
        $global:GraphGuardEnsure.Calls++
        return $global:GraphGuardEnsure.Reply
    }

    function Invoke-GuardRaw {
        <#
            Runs the guard once and returns its parsed hookSpecificOutput, or $null for
            silence. CLAUDE_PROJECT_DIR is set per call: the real hook environment may
            already carry one, and a test that inherited it would stop testing what it
            says it tests.
        #>
        [CmdletBinding()]
        param(
            [string]$Command,
            [hashtable]$GrepInput,
            [string]$ToolName = 'PowerShell',
            [string]$Transcript = $script:coldTranscript,
            [string]$Cwd
        )

        $toolInput = if ($GrepInput) { $GrepInput } else { @{ command = $Command } }
        $payload = @{
            tool_name       = $ToolName
            tool_input      = $toolInput
            transcript_path = $Transcript
        }
        if ($Cwd) { $payload['cwd'] = $Cwd }
        $json = $payload | ConvertTo-Json -Compress -Depth 6

        $previous = $env:CLAUDE_PROJECT_DIR
        $env:CLAUDE_PROJECT_DIR = $script:projectDir
        try {
            $out = & $script:guard -InputJson $json -McpCall $script:fakeMcp -EnsureServer $script:fakeEnsure 2>&1 | Out-String
        }
        finally {
            $env:CLAUDE_PROJECT_DIR = $previous
        }

        if ([string]::IsNullOrWhiteSpace($out)) { return $null }
        return ($out | ConvertFrom-Json).hookSpecificOutput
    }

    function Invoke-Guard {
        # Answers 'deny' or 'pass'.
        [CmdletBinding()]
        param([string]$Command, [hashtable]$GrepInput, [string]$ToolName = 'PowerShell',
            [string]$Transcript = $script:coldTranscript, [string]$Cwd)

        $output = Invoke-GuardRaw @PSBoundParameters
        if ($null -ne $output -and $output.PSObject.Properties.Name -contains 'permissionDecision' -and
            $output.permissionDecision -eq 'deny') { return 'deny' }
        return 'pass'
    }

    function Get-GuardReason {
        <#
            The denial's REASON text, decoded. Invoke-Guard throws the reason away, which
            is how the reason drifted on 2026-09-14 with every test green. The answer is
            the whole product of this hook, so it needs pinning like any other output.
        #>
        [CmdletBinding()]
        param([string]$Command, [hashtable]$GrepInput, [string]$ToolName = 'PowerShell',
            [string]$Transcript = $script:coldTranscript, [string]$Cwd)

        $output = Invoke-GuardRaw @PSBoundParameters
        if ($null -eq $output -or $output.PSObject.Properties.Name -notcontains 'permissionDecisionReason') { return $null }
        return $output.permissionDecisionReason
    }

    function Assert-FailedOpen {
        # A pass that says why: additionalContext, and NO permissionDecision at all --
        # 'allow' would also skip the permission prompt.
        param($Output, [string]$Mentions)
        $Output | Should -Not -BeNullOrEmpty
        $Output.PSObject.Properties.Name | Should -Not -Contain 'permissionDecision'
        $Output.additionalContext | Should -Match $Mentions
        ($Output.additionalContext -split "`n").Count | Should -Be 1
    }
}

Describe 'Invoke-GraphGuard' {

    BeforeEach {
        $global:GraphGuardMcp.Calls.Clear()
        $global:GraphGuardMcp.Replies.Clear()
        $global:GraphGuardEnsure.Calls = 0
        $global:GraphGuardEnsure.Reply = '{"ok":false,"state":"no-server-binary","error":"no binary"}'
    }

    AfterAll {
        Remove-Variable -Name GraphGuardMcp, GraphGuardEnsure -Scope Global -ErrorAction SilentlyContinue
    }

    Context 'git is not a code search' {
        # All three of these were denied on 2026-09-06. The graph models one built state
        # of the source: it cannot answer what changed, who changed it, or when, so a
        # denial here sends the caller to a tool that has no answer to give. Since
        # 2026-10-12 they pass because each search reads git's OUTPUT rather than files,
        # not because the command mentions git.

        It 'lets a git diff piped into a search go through' {
            # The exact command that was refused: a version constant, read out of a diff.
            Invoke-Guard -Command 'git -C C:\repos\RazorGraphTool diff -- src/RazorGraph.Core/Serialization/GraphFormat.cs | Select-String "1.3"' |
                Should -Be 'pass'
        }

        It 'lets a git status beside a C# path go through' {
            # Every case in this context carries a search tool ON PURPOSE. Written
            # without one, the command passes whether the exemption exists or not --
            # the shell trigger needs a search -- so the test would
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
            # Now refused by ANSWERING: the denial carries razorgraph's results.
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; glob = '*.cs' } |
                Should -Be 'deny'
        }

        It 'answers a search for another kind of file too' {
            # Was a pass until 2026-10-11, when the Grep trigger stopped being about C#:
            # any Grep inside a repo is answered, whatever it searches.
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; glob = '*.ps1' } |
                Should -Be 'deny'
            $global:GraphGuardMcp.Calls[0]['glob'] | Should -Be '*.ps1'
        }
    }

    Context 'clearance is gone, the escape hatch stays' {

        It 'refuses a shell search even with a razorgraph call in the recent transcript' {
            # Was a pass: one razorgraph call bought 12 tool calls of grep, and agents
            # bought it with a throwaway call.
            Invoke-Guard -Command 'rg "WavePlan" src/' -Transcript $script:warmTranscript |
                Should -Be 'deny'
        }

        It 'answers a Grep even with a razorgraph call in the recent transcript' {
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan' } -Transcript $script:warmTranscript |
                Should -Be 'deny'
        }

        It 'does not need a transcript at all' {
            # Was 'fails open when the transcript does not exist'. Nothing reads it now,
            # so its absence is no longer a reason to let a search through.
            Invoke-Guard -Command 'rg "WavePlan" src/' -Transcript (Join-Path $TestDrive 'absent.jsonl') |
                Should -Be 'deny'
        }

        It 'passes everything when JANET_GRAPH_GUARD is off' {
            $previous = $env:JANET_GRAPH_GUARD
            $env:JANET_GRAPH_GUARD = 'off'
            try {
                Invoke-Guard -Command 'rg "WavePlan" src/' | Should -Be 'pass'
                Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan' } | Should -Be 'pass'
                $global:GraphGuardMcp.Calls.Count | Should -Be 0
            }
            finally {
                $env:JANET_GRAPH_GUARD = $previous
            }
        }
    }

    Context 'a former false positive, now the rule' {

        It 'refuses a character check over C# sources, which razorgraph grep answers' {
            # FOUND 2026-09-06 and pinned as a false positive: the question is which
            # files hold a non-ASCII character, and the graph indexes no bytes. Since
            # 2026-10-12 shell searches of files are denied whatever they look for, and
            # this one has an answer: razorgraph grep's disk section is a .NET regex
            # over every line, so a pattern such as [^\x00-\x7F] finds it. Raw bytes and line
            # endings are still beyond it, and a [System.IO.File]::ReadAllBytes check
            # is not a search, so it passes.
            Invoke-Guard -Command 'Get-ChildItem src -Filter *.cs | Select-String -Pattern ([char]0x2014)' |
                Should -Be 'deny'
        }
    }

    Context 'a Grep inside a repo is answered by razorgraph' {

        It 'denies with the graph and disk hits rendered' {
            $reason = Get-GuardReason -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; output_mode = 'content' }

            ($reason -split "`n")[0] | Should -Match '^Answered by mcp__razorgraph__grep in place of Grep'
            $reason | Should -Match ([regex]::Escape('Graph: stub, built 2026-10-11T08:00:00Z'))
            $reason | Should -Match ([regex]::Escape('src\Foo.cs:12  Class WavePlan  (type:Nav.WavePlan)'))
            $reason | Should -Match ([regex]::Escape('src\Bar.cs:40: var plan = new WavePlan();'))
            $reason | Should -Match '1 already reported by the graph'
            # Nothing was truncated, so there is nothing to continue.
            $reason | Should -Not -Match 'More: '
        }

        It 'passes ignoreCase=false unless -i, and true with it' {
            # Grep is case-sensitive by default and razorgraph is not, so the hook must
            # say which every time.
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan' } | Out-Null
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; '-i' = $true } | Out-Null

            $global:GraphGuardMcp.Calls.Count | Should -Be 2
            $global:GraphGuardMcp.Calls[0]['ignoreCase'] | Should -BeExactly $false
            $global:GraphGuardMcp.Calls[1]['ignoreCase'] | Should -BeExactly $true
        }

        It 'turns a file path into its folder plus a glob of its name' {
            Invoke-Guard -ToolName 'Grep' -GrepInput @{
                pattern = 'WavePlan'; path = (Join-Path $script:projectDir 'src\Foo.cs')
            } | Should -Be 'deny'

            $global:GraphGuardMcp.Calls[0]['folder'] | Should -Be (Join-Path $script:projectDir 'src')
            $global:GraphGuardMcp.Calls[0]['glob'] | Should -Be 'Foo.cs'
        }

        It 'searches the folder a Grep names, and the payload cwd when it names none' {
            # Replaces the 2026-09-14 solution-resolution pins: the tree searched is the
            # tree named, never the session's project.
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; path = $script:scanRepo } | Out-Null
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan' } -Cwd $script:scanRepo | Out-Null

            $global:GraphGuardMcp.Calls[0]['folder'] | Should -Be $script:scanRepo
            $global:GraphGuardMcp.Calls[1]['folder'] | Should -Be $script:scanRepo
        }

        It 'maps a known type to a glob' {
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; type = 'cs' } | Should -Be 'deny'
            $global:GraphGuardMcp.Calls[0]['glob'] | Should -Be '*.cs'
        }

        It 'lets an unknown type through without asking' {
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; type = 'cobol' } | Should -Be 'pass'
            $global:GraphGuardMcp.Calls.Count | Should -Be 0
        }

        It 'lets a glob razorgraph cannot match through: <glob>' -ForEach @(
            @{ glob = '**/*.cs' }, @{ glob = 'src/*.cs' }, @{ glob = '*.{cs,ps1}' }
        ) {
            # razorgraph's glob matches the file NAME only.
            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; glob = $glob } | Should -Be 'pass'
            $global:GraphGuardMcp.Calls.Count | Should -Be 0
        }

        It 'lets <name> through, which razorgraph cannot do' -ForEach @(
            @{ name = 'multiline'; extra = @{ multiline = $true } },
            @{ name = '-C'; extra = @{ '-C' = 2 } },
            @{ name = '-A'; extra = @{ '-A' = 1 } },
            @{ name = 'context'; extra = @{ context = 3 } }
        ) {
            $grepInput = @{ pattern = 'WavePlan'; output_mode = 'content' } + $extra
            Invoke-Guard -ToolName 'Grep' -GrepInput $grepInput | Should -Be 'pass'
            $global:GraphGuardMcp.Calls.Count | Should -Be 0
        }

        It 'passes a path outside any git working tree untouched' {
            Invoke-GuardRaw -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; path = $script:plainDir } |
                Should -BeNullOrEmpty
            $global:GraphGuardMcp.Calls.Count | Should -Be 0
        }
    }

    Context 'output modes and the size cap' {

        It 'gives distinct paths for files_with_matches, the Grep default' {
            $reason = Get-GuardReason -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan' }

            $global:GraphGuardMcp.Calls[0]['limit'] | Should -Be 1000
            $lines = @($reason -split "`n" | Where-Object { $_ -match '^  ' } | ForEach-Object { $_.Trim() })
            $lines | Should -Be @('src\Bar.cs', 'src\Foo.cs')
            $reason | Should -Not -Match 'PARTIAL'
        }

        It 'gives per-file line counts for count, across both sections' {
            $reason = Get-GuardReason -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; output_mode = 'count' }

            # Foo.cs: graph line 12 plus disk line 30. Bar.cs: disk lines 40 and 41.
            $reason | Should -Match ([regex]::Escape('  src\Bar.cs:2'))
            $reason | Should -Match ([regex]::Escape('  src\Foo.cs:2'))
        }

        It 'says a file list is partial when a section was truncated' {
            $global:GraphGuardMcp.Replies.Enqueue((New-GrepEnvelope -NoGraph `
                -DiskHits @(@{ path = 'a.cs'; line = 1; text = 'WavePlan' }) -DiskTotal 1500))

            $reason = Get-GuardReason -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan' }

            $reason | Should -Match 'PARTIAL: only the first 1000 matches per section'
            $reason | Should -Match '"diskOffset":1\}'
        }

        It 'trims to the cap, says how many, and continues from the last line shown' {
            $many = 1..400 | ForEach-Object { @{ path = "src\File$_.cs"; line = $_; text = ('x' * 100) + ' WavePlan' } }
            $global:GraphGuardMcp.Replies.Enqueue((New-GrepEnvelope -NoGraph -DiskHits $many))

            $reason = Get-GuardReason -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; output_mode = 'content'; head_limit = 400 }

            $reason.Length | Should -BeLessOrEqual 12000
            $reason | Should -Match '(\d+) hit\(s\) trimmed to fit the 12000-character cap'
            $trimmedCount = [int]([regex]::Match($reason, '(\d+) hit\(s\) trimmed').Groups[1].Value)
            $shown = 400 - $trimmedCount
            @($reason -split "`n" | Where-Object { $_ -match '^  src\\File' }).Count | Should -Be $shown
            $reason | Should -Match ('"diskOffset":' + $shown + '\}')
        }
    }

    Context 'when razorgraph cannot answer' {

        It 'fails open on <name>, without starting the server' -ForEach @(
            @{ name = 'isError'; reply = '{"ok":false,"tool":"grep","result":"An error occurred invoking ''grep'': folder is odd"}'; mentions = 'folder is odd' },
            @{ name = 'an invalid regex'; reply = '{"ok":false,"tool":"grep","result":"An error occurred invoking ''grep'': pattern is not a valid .NET regular expression"}'; mentions = 'not a valid .NET regular expression' },
            @{ name = 'a protocol error'; reply = '{"ok":false,"tool":"grep","error":"Unknown tool: grep","code":-32602}'; mentions = 'Unknown tool' },
            @{ name = 'an exception in the call'; reply = { throw 'boom in the call' }; mentions = 'boom in the call' }
        ) {
            $global:GraphGuardMcp.Replies.Enqueue($reply)

            $output = Invoke-GuardRaw -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan' }

            Assert-FailedOpen -Output $output -Mentions $mentions
            $global:GraphGuardEnsure.Calls | Should -Be 0
            $output.additionalContext | Should -Not -Match 'own scan'
        }

        It 'starts the server when unreachable and renders the retry from razorgraph' {
            $global:GraphGuardMcp.Replies.Enqueue('{"ok":false,"tool":"grep","error":"cannot reach the MCP server: No connection could be made"}')
            $global:GraphGuardEnsure.Reply = '{"ok":true,"state":"started","error":null}'

            $reason = Get-GuardReason -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; output_mode = 'content' }

            $global:GraphGuardEnsure.Calls | Should -Be 1
            $global:GraphGuardMcp.Calls.Count | Should -Be 2
            ($reason -split "`n")[0] | Should -Match '^Answered by mcp__razorgraph__grep'
            $reason | Should -Match ([regex]::Escape('src\Foo.cs:12  Class WavePlan'))
            $reason | Should -Not -Match 'own scan'
        }

        It 'treats a timeout as unreachable too' {
            $global:GraphGuardMcp.Replies.Enqueue({ throw 'The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.' })
            $global:GraphGuardEnsure.Reply = '{"ok":true,"state":"already-running","error":null}'

            Invoke-Guard -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan' } | Should -Be 'deny'
            $global:GraphGuardEnsure.Calls | Should -Be 1
        }

        It 'scans locally when the server is down and will not start, and says so' {
            $global:GraphGuardMcp.Replies.Enqueue('{"ok":false,"tool":"grep","error":"cannot reach the MCP server: refused"}')
            $global:GraphGuardEnsure.Reply = '{"ok":false,"state":"start-failed","note":"nothing listening","error":"binary crashed on start"}'

            $reason = Get-GuardReason -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; path = $script:scanRepo; output_mode = 'content' }

            $global:GraphGuardMcp.Calls.Count | Should -Be 1
            ($reason -split "`n")[0] | Should -Match '^razorgraph was down and could not be started \(binary crashed on start\)'
            $reason | Should -Match "this hook's own scan"
            $reason | Should -Not -Match 'Graph:'
            $reason | Should -Match ([regex]::Escape('a.cs:2: var p = new WavePlan();'))
        }

        It 'scans locally when the server starts but the retry still cannot connect' {
            $global:GraphGuardMcp.Replies.Enqueue('{"ok":false,"tool":"grep","error":"cannot reach the MCP server: refused"}')
            $global:GraphGuardMcp.Replies.Enqueue('{"ok":false,"tool":"grep","error":"cannot reach the MCP server: refused again"}')
            $global:GraphGuardEnsure.Reply = '{"ok":true,"state":"started","error":null}'

            $reason = Get-GuardReason -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; path = $script:scanRepo; output_mode = 'content' }

            $global:GraphGuardMcp.Calls.Count | Should -Be 2
            $reason | Should -Match 'retry still could not connect: cannot reach the MCP server: refused again'
            $reason | Should -Match ([regex]::Escape('a.cs:2: var p = new WavePlan();'))
        }
    }

    Context "the hook's own scan" {

        BeforeEach {
            $global:GraphGuardMcp.Replies.Enqueue('{"ok":false,"tool":"grep","error":"cannot reach the MCP server: refused"}')
        }

        It 'honours the glob and the case rule' {
            $reason = Get-GuardReason -ToolName 'Grep' -GrepInput @{
                pattern = 'WavePlan'; path = $script:scanRepo; glob = '*.cs'; output_mode = 'content'
            }

            $reason | Should -Match ([regex]::Escape('a.cs:2: var p = new WavePlan();'))
            $reason | Should -Not -Match ([regex]::Escape('a.cs:3:'))
            $reason | Should -Not -Match 'b\.ps1'
        }

        It 'matches both cases with -i' {
            $reason = Get-GuardReason -ToolName 'Grep' -GrepInput @{
                pattern = 'WavePlan'; path = $script:scanRepo; glob = '*.cs'; output_mode = 'content'; '-i' = $true
            }

            $reason | Should -Match ([regex]::Escape('a.cs:3: var q = waveplan;'))
        }

        It 'skips a binary and counts it' {
            $reason = Get-GuardReason -ToolName 'Grep' -GrepInput @{ pattern = 'WavePlan'; path = $script:scanRepo }

            $lines = @($reason -split "`n" | Where-Object { $_ -match '^  ' } | ForEach-Object { $_.Trim() })
            $lines | Should -Be @('a.cs', 'b.ps1')
            $reason | Should -Match '1 skipped \(binary or over 2 MB\)'
        }

        It 'fails open on an invalid regex instead of scanning' {
            $output = Invoke-GuardRaw -ToolName 'Grep' -GrepInput @{ pattern = '(unclosed'; path = $script:scanRepo }

            Assert-FailedOpen -Output $output -Mentions "own scan failed"
        }
    }

    Context 'a shell search names the razorgraph call to paste' {

        It 'reads a plain command into a paste-ready call' {
            $reason = Get-GuardReason -Command 'rg "WavePlan" src/'

            $folder = (Join-Path $script:projectDir 'src') -replace '\\', '\\'
            $reason | Should -Match ([regex]::Escape('ToolSearch("select:mcp__razorgraph__grep")'))
            $reason | Should -Match ([regex]::Escape('mcp__razorgraph__grep {"pattern":"WavePlan","folder":"' + $folder + '","ignoreCase":false}'))
            $reason | Should -Not -Match 'find_nodes'
        }

        It 'reads Select-String as case-insensitive and a file path as folder plus glob' {
            $reason = Get-GuardReason -Command 'Select-String -Path src\Foo.cs -Pattern Bar'

            $folder = (Join-Path $script:projectDir 'src') -replace '\\', '\\'
            $reason | Should -Match ([regex]::Escape('{"pattern":"Bar","folder":"' + $folder + '","glob":"Foo.cs","ignoreCase":true}'))
        }

        It 'reads a pipeline into a paste-ready call' {
            # Was a placeholder until 2026-10-12: the lister upstream of Select-String
            # is now read for its folder and its -Filter.
            $reason = Get-GuardReason -Command 'Get-ChildItem src -Filter *.cs | Select-String -Pattern WavePlan'

            $folder = (Join-Path $script:projectDir 'src') -replace '\\', '\\'
            $reason | Should -Match ([regex]::Escape('{"pattern":"WavePlan","folder":"' + $folder + '","glob":"*.cs","ignoreCase":true}'))
        }

        It 'shows a placeholder for a pattern it cannot read, never a guess' {
            # The folder is what decided the denial, so it is always real; a pattern
            # held in a variable is not read.
            $reason = Get-GuardReason -Command 'Select-String -Path src\Foo.cs -Pattern $needle'

            $folder = (Join-Path $script:projectDir 'src') -replace '\\', '\\'
            $reason | Should -Match ([regex]::Escape('{"pattern":"<.NET regex>","folder":"' + $folder + '","glob":"Foo.cs","ignoreCase":true}'))
            $reason | Should -Match 'fill in pattern'
        }
    }

    Context 'any shell search of files in a repo is denied, 2026-10-12' {
        # "PowerShell and bash shouldn't be doing searches." The question is what the
        # search READS: files inside a git working tree are denied whatever their type,
        # another command's output passes, and so do files outside any repo.

        BeforeAll {
            New-Item -ItemType Directory -Path (Join-Path $script:projectDir 'config') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:projectDir 'config\settings.json') -Value '{ "version": "1.3" }' -Encoding utf8
            Set-Content -LiteralPath (Join-Path $script:plainDir 'notes.txt') -Value 'WavePlan' -Encoding utf8
            $script:configDir = Join-Path $script:projectDir 'config'
        }

        It 'refuses a recursive grep over a folder of non-C# files, with the call to paste' {
            $reason = Get-GuardReason -ToolName 'Bash' -Command 'grep -rn "version" config'

            $folder = $script:configDir -replace '\\', '\\'
            $reason | Should -Match ([regex]::Escape('mcp__razorgraph__grep {"pattern":"version","folder":"' + $folder + '","ignoreCase":false}'))
            $reason | Should -Match 'The Grep tool is also available'
            $reason | Should -Match 'answered through razorgraph'
        }

        It 'refuses Select-String -Path over *.json and carries the glob' {
            $reason = Get-GuardReason -Command 'Select-String -Path config\*.json -Pattern version'

            $folder = $script:configDir -replace '\\', '\\'
            $reason | Should -Match ([regex]::Escape('{"pattern":"version","folder":"' + $folder + '","glob":"*.json","ignoreCase":true}'))
        }

        It 'refuses Get-ChildItem -Recurse piped into Select-String' {
            Invoke-Guard -Command 'Get-ChildItem -Recurse | Select-String version' | Should -Be 'deny'
        }

        It 'refuses git grep over non-C# files' {
            Invoke-Guard -ToolName 'Bash' -Command 'git grep -n "version" -- "*.json"' | Should -Be 'deny'
        }

        It 'refuses a file read piped into grep' {
            $reason = Get-GuardReason -ToolName 'Bash' -Command 'cat config/settings.json | grep version'

            $reason | Should -Match ([regex]::Escape('"glob":"settings.json"'))
        }

        It 'refuses a file fed to grep through an input redirect' {
            $reason = Get-GuardReason -ToolName 'Bash' -Command 'grep version < config/settings.json'

            $reason | Should -Match ([regex]::Escape('"glob":"settings.json"'))
        }

        It 'reads a heredoc body as data, not as commands to judge' {
            # Writing a file whose TEXT contains a grep is not a search.
            $command = "cat <<'EOF' > notes.txt`ngrep -rn version config`nEOF"

            Invoke-GuardRaw -ToolName 'Bash' -Command $command | Should -BeNullOrEmpty
        }

        It 'refuses find -exec grep and find | xargs grep' {
            Invoke-Guard -ToolName 'Bash' -Command 'find config -name "*.json" -exec grep -l version {} \;' | Should -Be 'deny'
            Invoke-Guard -ToolName 'Bash' -Command 'find config -name "*.json" | xargs grep version' | Should -Be 'deny'
        }

        It 'refuses a search inside pwsh -Command run from Bash' {
            Invoke-Guard -ToolName 'Bash' -Command 'pwsh -NoProfile -Command "Select-String -Path config\settings.json -Pattern version"' |
                Should -Be 'deny'
        }

        It 'refuses a real search beside a git command, the old exemption hole' {
            # Until 2026-10-12 any command mentioning git passed whole, so this did.
            Invoke-Guard -ToolName 'Bash' -Command 'git status --short; rg "WavePlan" src' | Should -Be 'deny'
        }

        It 'refuses in a repo that holds no solution' {
            # scanRepo has no .slnx; the shell trigger needed one until 2026-10-12.
            Invoke-Guard -ToolName 'Bash' -Command 'grep -n WavePlan b.ps1' -Cwd $script:scanRepo | Should -Be 'deny'
        }

        It 'lets a search of another command''s output through: <command>' -ForEach @(
            @{ tool = 'Bash'; command = "git diff | grep '^@@'" }
            @{ tool = 'Bash'; command = 'git log --oneline | grep WavePlan' }
            @{ tool = 'PowerShell'; command = 'dotnet test | Select-String fail' }
            @{ tool = 'Bash'; command = 'env | grep GIT_' }
            @{ tool = 'Bash'; command = 'ps | grep pwsh' }
            @{ tool = 'PowerShell'; command = '.\some.ps1 | Select-String WavePlan' }
        ) {
            Invoke-GuardRaw -ToolName $tool -Command $command | Should -BeNullOrEmpty
        }

        It 'lets git log --grep and -S through, which are not file searches' {
            # Each names a path inside the repo ON PURPOSE: without one, a git log that
            # was misread as a search would still have nothing to read.
            Invoke-GuardRaw -ToolName 'Bash' -Command 'git log --grep=WavePlan -- config/settings.json' | Should -BeNullOrEmpty
            Invoke-GuardRaw -ToolName 'Bash' -Command 'git log -S version --grep=fix -- config' | Should -BeNullOrEmpty
        }

        It 'lets a grep of a file outside any repo through' {
            $outside = Join-Path $script:plainDir 'notes.txt'
            Invoke-GuardRaw -ToolName 'Bash' -Command "grep -n WavePlan '$outside'" | Should -BeNullOrEmpty
        }

        It 'lets a relative grep through when the cwd is outside any repo' {
            Invoke-GuardRaw -ToolName 'Bash' -Command 'grep -rn WavePlan .' -Cwd $script:plainDir | Should -BeNullOrEmpty
        }

        It 'runs a search it cannot resolve, and says so without allowing' {
            $output = Invoke-GuardRaw -Command 'Select-String -Path $file -Pattern version'
            Assert-FailedOpen -Output $output -Mentions 'could not tell which files'
            $output.additionalContext | Should -Match ([regex]::Escape('$file'))
        }
    }

    Context 'a shell read of source in a repo is denied, 2026-10-23' {
        # Thread item 55, decided "deny, source only": cat, head, sed -n and Get-Content
        # of a .cs all passed live probes, because a reader counted only when it fed a
        # search. Source is .cs, .razor, .csproj, .sln and .slnx; nothing else is.

        BeforeAll {
            Set-Content -LiteralPath (Join-Path $script:projectDir 'src\Page.razor') -Value '<h1>Hi</h1>' -Encoding utf8
            Set-Content -LiteralPath (Join-Path $script:projectDir 'App.csproj') -Value '<Project />' -Encoding utf8
            Set-Content -LiteralPath (Join-Path $script:projectDir 'App.sln') -Value '' -Encoding utf8
            Set-Content -LiteralPath (Join-Path $script:projectDir 'notes.md') -Value '# notes' -Encoding utf8
            Set-Content -LiteralPath (Join-Path $script:projectDir 'Tool.ps1') -Value 'Write-Output 1' -Encoding utf8
            Set-Content -LiteralPath (Join-Path $script:plainDir 'Outside.cs') -Value 'class Outside { }' -Encoding utf8
        }

        It 'refuses <name>' -ForEach @(
            @{ name = 'cat'; tool = 'Bash'; command = 'cat src/Foo.cs' }
            @{ name = 'head -n'; tool = 'Bash'; command = 'head -n 40 src/Foo.cs' }
            @{ name = 'tail'; tool = 'Bash'; command = 'tail -5 src/Foo.cs' }
            @{ name = 'less'; tool = 'Bash'; command = 'less src/Foo.cs' }
            @{ name = 'more'; tool = 'Bash'; command = 'more src/Foo.cs' }
            @{ name = 'tac'; tool = 'Bash'; command = 'tac src/Foo.cs' }
            @{ name = 'sed -n with the script first'; tool = 'Bash'; command = 'sed -n 1,80p src/Foo.cs' }
            @{ name = 'sed -n -e'; tool = 'Bash'; command = "sed -n -e '1,80p' src/Foo.cs" }
            @{ name = 'awk with a program'; tool = 'Bash'; command = "awk 'NR<40' src/Foo.cs" }
            @{ name = 'awk -v then a program'; tool = 'Bash'; command = "awk -v n=40 'NR<n' src/Foo.cs" }
            @{ name = 'a .razor'; tool = 'Bash'; command = 'cat src/Page.razor' }
            @{ name = 'a .csproj'; tool = 'Bash'; command = 'cat App.csproj' }
            @{ name = 'a .sln'; tool = 'Bash'; command = 'cat App.sln' }
            @{ name = 'a .slnx'; tool = 'Bash'; command = 'cat Stub.slnx' }
            @{ name = 'a read after cd'; tool = 'Bash'; command = 'cd src && head Foo.cs' }
            @{ name = 'Get-Content -TotalCount'; tool = 'PowerShell'; command = 'Get-Content src\Foo.cs -TotalCount 40' }
            @{ name = 'gc'; tool = 'PowerShell'; command = 'gc src\Foo.cs' }
            @{ name = 'type, the PowerShell alias'; tool = 'PowerShell'; command = 'type src\Foo.cs' }
            @{ name = 'Get-Content inside an assignment'; tool = 'PowerShell'; command = '$text = Get-Content -Raw -Path src\Foo.cs' }
            @{ name = '[IO.File]::ReadAllText'; tool = 'PowerShell'; command = "[IO.File]::ReadAllText('src\Foo.cs')" }
            @{ name = '[System.IO.File]::ReadAllLines'; tool = 'PowerShell'; command = "[System.IO.File]::ReadAllLines('src\Foo.cs').Count" }
            @{ name = 'a read inside pwsh -Command'; tool = 'Bash'; command = 'pwsh -NoProfile -Command "Get-Content src\Foo.cs"' }
        ) {
            Invoke-Guard -ToolName $tool -Command $command | Should -Be 'deny'
        }

        It 'points the denial at Read and razorgraph, naming the file' {
            $reason = Get-GuardReason -ToolName 'Bash' -Command 'sed -n 1,80p src/Foo.cs'
            $reason | Should -Match ([regex]::Escape((Join-Path $script:projectDir 'src\Foo.cs')))
            $reason | Should -Match 'with sed'
            $reason | Should -Match 'Read tool'
            $reason | Should -Match ([regex]::Escape('mcp__razorgraph__find_nodes {"nameContains":"Foo"}'))
        }

        It 'lets <name> through' -ForEach @(
            @{ name = 'a .json'; tool = 'Bash'; command = 'cat config/settings.json' }
            @{ name = 'a .md'; tool = 'Bash'; command = 'head -n 5 notes.md' }
            @{ name = 'a .ps1'; tool = 'PowerShell'; command = 'Get-Content Tool.ps1 -TotalCount 5' }
            @{ name = 'git show of a .cs'; tool = 'Bash'; command = 'git show HEAD:src/Foo.cs' }
            @{ name = 'git show of a .cs piped into head'; tool = 'Bash'; command = 'git show HEAD:src/Foo.cs | head -40' }
            @{ name = 'dotnet naming a .csproj'; tool = 'Bash'; command = 'dotnet build App.csproj' }
            @{ name = 'sed -i, which rewrites rather than reads'; tool = 'Bash'; command = 'sed -i s/a/b/ src/Foo.cs' }
            @{ name = 'awk reading a .md, given a .cs path as a -v value'; tool = 'Bash'; command = 'awk -v target=src/Foo.cs -f find.awk notes.md' }        ) {
            Invoke-GuardRaw -ToolName $tool -Command $command | Should -BeNullOrEmpty
        }

        It 'lets a read of a .cs outside any repo through' {
            $outside = (Join-Path $script:plainDir 'Outside.cs') -replace '\\', '/'
            Invoke-GuardRaw -ToolName 'Bash' -Command "cat '$outside'" | Should -BeNullOrEmpty
        }

        It 'runs a read of source it cannot resolve, and says so without allowing' {
            $output = Invoke-GuardRaw -Command 'Get-Content "$dir\Foo.cs"'
            Assert-FailedOpen -Output $output -Mentions 'could not tell which files'
            $output.additionalContext | Should -Match ([regex]::Escape('$dir\Foo.cs'))
        }
    }
}
