#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }
<#
.SYNOPSIS
    Pester tests for scripts\Invoke-DelegationGuard.ps1 -- both paths: the prompt gate
    that refuses a graph-less C# delegation, and the hands-on volume warning that must
    never refuse anything.

.DESCRIPTION
    The guard has two jobs and they pull in opposite directions, so the tests hold both
    ends. Path one DENIES: an Agent prompt about C# structure with no RazorGraph
    instructions in it. Path two WARNS AND ONLY WARNS: a long run of hands-on tool calls
    with no Agent dispatch in the transcript.

    The load-bearing test in this file is 'does not deny'. Path two exists because
    nothing fired when a session simply never delegated -- about ninety consecutive
    hands-on calls after one Agent dispatch, through no gate at all -- and the reason it
    warns rather than blocks is that legitimate deep work looks identical from outside.
    A gate that refuses real work gets switched off, and switching it off would take the
    prompt gate with it. So a future edit that turns the warning into a denial has to
    fail here, loudly, rather than pass because the message still says the right words.

    That test asserts on the ABSENCE of permissionDecision in the raw envelope rather
    than on an exit code, for the same reason Invoke-GraphGuard.Tests.ps1 asserts on the
    envelope: this script fails open in every error path, so exit 0 alone cannot tell a
    working guard from a crashed one.

    Three bounds on path two were added 2026-09-07, each settled against real transcripts
    rather than reasoned about, and each pinned here. It does not fire inside a SUBAGENT
    (the payload's agent_id says so, and transcript_path there names the PARENT's
    transcript). It warns ONCE PER CROSSING -- on the multiples of the window -- because
    109 warnings over the real failure transcript is how a warn-only guard gets switched
    off, against 5 under this rule. And a USER TURN resets the streak, which was checked
    against that same transcript before it was applied: the reset takes the longest streak
    from 147 to 144 and the warning still fires at 25, 50, 75, 100 and 125, so it clears
    five small errands without clearing the case the guard exists for.

    Because the warning now fires on crossings, fixtures are sized to LAND on one -- 50
    rather than 30 or 40. A fixture off a crossing is silent, which would make a test
    green for a reason that has nothing to do with what it claims.

    Each test drives the script with -InputJson and its own transcript under TestDrive.
    Pipe input does not bind to a [string] parameter, which is why the script documents
    -InputJson for exactly this.
#>

BeforeAll {
    $script:repoRoot = Split-Path $PSScriptRoot -Parent
    $script:guard = Join-Path $script:repoRoot 'scripts\Invoke-DelegationGuard.ps1'

    # A project directory holding a solution, so the prompt gate can derive a graphId
    # for its denial text. A stub file is enough -- only the name is read.
    $script:projectDir = Join-Path $TestDrive 'repo'
    New-Item -ItemType Directory -Path $script:projectDir | Out-Null
    Set-Content -LiteralPath (Join-Path $script:projectDir 'Stub.slnx') -Value '<Solution />' -Encoding utf8

    function New-Transcript {
        <#
            Writes a transcript in the harness's shape -- one JSON object per line, tool
            calls inside an assistant message -- and answers its path.

            Count is how many hands-on calls to write. DispatchAtDepth, when given,
            replaces the entry that many calls back from the newest with an Agent
            dispatch, which is how a reset is expressed: depth 1 is the most recent call.

            UserTurnAtDepth inserts a real user turn just BEFORE the call at that depth,
            and TaskNotificationAtDepth inserts the harness's background-agent report in
            the same position -- which looks like a user turn in the transcript and must
            not be treated as one.
        #>
        [CmdletBinding()]
        param(
            [string]$Name,
            [int]$Count,
            [int]$DispatchAtDepth = 0,
            [int]$UserTurnAtDepth = 0,
            [int]$TaskNotificationAtDepth = 0
        )

        $userTurn = '{"type":"user","message":{"role":"user","content":"Now do the next small thing please"}}'
        $notification = '{"type":"user","message":{"role":"user","content":"<task-notification> <task-id>abc</task-id> done"}}'

        $path = Join-Path $TestDrive $Name
        $lines = foreach ($i in 1..$Count) {
            $depth = $Count - $i + 1
            if ($UserTurnAtDepth -gt 0 -and $depth -eq $UserTurnAtDepth) { $userTurn }
            if ($TaskNotificationAtDepth -gt 0 -and $depth -eq $TaskNotificationAtDepth) { $notification }
            $tool = if ($DispatchAtDepth -gt 0 -and $depth -eq $DispatchAtDepth) { 'Agent' } else { 'Read' }
            '{"type":"assistant","message":{"content":[{"type":"tool_use","name":"' + $tool + '"}]}}'
        }
        Set-Content -LiteralPath $path -Value $lines -Encoding utf8
        return $path
    }

    function New-ErrandTranscript {
        <#
            Five unrelated small requests, six hands-on calls each: a user turn, then six
            Reads, five times over. Thirty calls in total and not one of them a reason to
            spawn an agent.
        #>
        [CmdletBinding()]
        param([string]$Name, [int]$Requests = 5, [int]$CallsEach = 6)

        $path = Join-Path $TestDrive $Name
        $lines = foreach ($r in 1..$Requests) {
            '{"type":"user","message":{"role":"user","content":"Small request ' + $r + '"}}'
            foreach ($c in 1..$CallsEach) {
                '{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read"}]}}'
            }
        }
        Set-Content -LiteralPath $path -Value $lines -Encoding utf8
        return $path
    }

    function Invoke-Guard {
        <#
            Runs the guard once and answers a result object: the raw stdout, and a
            Decision of 'deny', 'warn' or 'pass'.

            CLAUDE_PROJECT_DIR is set per call rather than in BeforeAll: the real hook
            environment may already carry one, and a test that inherits it would resolve
            a different solution and stop testing what it says it tests.
        #>
        [CmdletBinding()]
        param(
            [string]$ToolName = 'Read',
            [hashtable]$ToolInput = @{},
            [string]$Transcript,
            [string]$AgentId
        )

        $payload = @{
            tool_name  = $ToolName
            tool_input = $ToolInput
        }
        if ($PSBoundParameters.ContainsKey('Transcript')) { $payload['transcript_path'] = $Transcript }
        if ($PSBoundParameters.ContainsKey('AgentId')) { $payload['agent_id'] = $AgentId }
        $json = $payload | ConvertTo-Json -Compress -Depth 6

        $previous = $env:CLAUDE_PROJECT_DIR
        $env:CLAUDE_PROJECT_DIR = $script:projectDir
        try {
            $out = & $script:guard -InputJson $json 2>&1 | Out-String
        }
        finally {
            $env:CLAUDE_PROJECT_DIR = $previous
        }

        $decision =
        if ($out -match '"permissionDecision"\s*:\s*"deny"') { 'deny' }
        elseif ($out -match '"additionalContext"') { 'warn' }
        else { 'pass' }

        return [PSCustomObject]@{ Raw = $out; Decision = $decision }
    }
}

Describe 'Invoke-DelegationGuard' {

    Context 'hands-on volume: what it warns about' {

        It 'says nothing under the window' {
            # Twenty-four consecutive calls against a window of twenty-five. Real batches
            # of in-session work run this long, and a warning that arrives during ordinary
            # work is one that gets read past.
            $t = New-Transcript -Name 'under.jsonl' -Count 24
            (Invoke-Guard -Transcript $t).Decision | Should -Be 'pass'
        }

        It 'warns past the window' {
            # Fifty rather than thirty: the warning fires on the CROSSINGS of the window,
            # so a fixture has to land on one. Thirty is now deliberately silent, and the
            # test below pins that.
            $t = New-Transcript -Name 'over.jsonl' -Count 50
            (Invoke-Guard -Transcript $t).Decision | Should -Be 'warn'
        }

        It 'warns on every watched tool, not only on reads' {
            # The point is volume of tool output, so the whole hands-on surface is
            # watched. Agent and Task are not on this list -- they are path one.
            $t = New-Transcript -Name 'tools.jsonl' -Count 50
            foreach ($tool in 'Read', 'Grep', 'Glob', 'Edit', 'Write', 'Bash', 'PowerShell') {
                (Invoke-Guard -ToolName $tool -Transcript $t).Decision | Should -Be 'warn' -Because "$tool is a watched tool"
            }
        }

        It 'counts the streak and names it in the warning' {
            $t = New-Transcript -Name 'count.jsonl' -Count 50
            (Invoke-Guard -Transcript $t).Raw | Should -Match '50 tool calls in a row'
        }

        It 'honours JANET_DELEGATION_WINDOW' {
            # Ten calls against a window of five: the second crossing of a window that
            # the default of twenty-five would not have reached at all.
            $t = New-Transcript -Name 'window.jsonl' -Count 10
            $previous = $env:JANET_DELEGATION_WINDOW
            $env:JANET_DELEGATION_WINDOW = '5'
            try {
                (Invoke-Guard -Transcript $t).Decision | Should -Be 'warn'
            }
            finally {
                $env:JANET_DELEGATION_WINDOW = $previous
            }
        }
    }

    Context 'once per crossing, not once per call' {
        # Warning at 26, then 27, then 28 is the noise that gets a guard switched off,
        # and a warn-only guard has no power except being read. Measured over the real
        # transcript of the failure that motivated this path: 109 warnings under
        # warn-on-every-call, 5 under this rule.

        It 'says nothing between one crossing and the next' {
            foreach ($count in 26, 30, 37, 49) {
                $t = New-Transcript -Name "between$count.jsonl" -Count $count
                (Invoke-Guard -Transcript $t).Decision | Should -Be 'pass' -Because "$count is between crossings"
            }
        }

        It 'warns again at the next multiple of the window' {
            foreach ($count in 25, 50, 75, 100) {
                $t = New-Transcript -Name "crossing$count.jsonl" -Count $count
                (Invoke-Guard -Transcript $t).Decision | Should -Be 'warn' -Because "$count is a crossing"
            }
        }

        It 'names the next crossing in the warning, so the escalation is legible' {
            $t = New-Transcript -Name 'nextcrossing.jsonl' -Count 50
            (Invoke-Guard -Transcript $t).Raw | Should -Match 'the next is at 75'
        }

        It 'crosses on the multiples of a configured window too' {
            $previous = $env:JANET_DELEGATION_WINDOW
            $env:JANET_DELEGATION_WINDOW = '10'
            try {
                (Invoke-Guard -Transcript (New-Transcript -Name 'w10.jsonl' -Count 20)).Decision | Should -Be 'warn'
                (Invoke-Guard -Transcript (New-Transcript -Name 'w13.jsonl' -Count 13)).Decision | Should -Be 'pass'
            }
            finally {
                $env:JANET_DELEGATION_WINDOW = $previous
            }
        }

        It 'goes quiet past the depth it can actually measure' {
            # The reader scans eight windows back. Past that the streak is pinned at the
            # bound rather than counted, so it only LOOKS like a multiple -- and warning
            # on every call there is the noise this whole rule removes. By then the
            # warning has fired at every crossing below.
            $t = New-Transcript -Name 'beyond.jsonl' -Count 260
            (Invoke-Guard -Transcript $t).Decision | Should -Be 'pass'
        }
    }

    Context 'not inside a subagent' {
        # A subagent running forty tool calls is this guard working, not failing, and the
        # warning is unactionable there -- a subagent should not be spawning nested
        # agents. Measured on real payloads 2026-09-07: a subagent's carries agent_id
        # (and agent_type), a main session's carries neither. It matters more than it
        # looks, because transcript_path inside a subagent names the PARENT's transcript,
        # so an unguarded hook warns the child about its parent's streak.

        It 'says nothing when the payload carries an agent_id' {
            $t = New-Transcript -Name 'subagent.jsonl' -Count 50
            (Invoke-Guard -Transcript $t -AgentId 'a2a3502c4a6489379').Decision | Should -Be 'pass'
        }

        It 'still warns when there is no agent_id, which is how a main session looks' {
            $t = New-Transcript -Name 'mainsession.jsonl' -Count 50
            (Invoke-Guard -Transcript $t).Decision | Should -Be 'warn'
        }

        It 'still gates a subagent prompt on path one' {
            # The subagent skip belongs to the volume warning only. A subagent that
            # spawns a nested agent for C# work needs the graph block just as much.
            Invoke-Guard -ToolName 'Agent' -ToolInput @{ prompt = 'Find the callers in src\Nav.Core\Grid.cs' } -AgentId 'a2a3502c4a6489379' |
                Select-Object -ExpandProperty Decision | Should -Be 'deny'
        }
    }

    Context 'the warning must not be a refusal' {

        It 'does not deny, and carries no permission decision at all' {
            # THE LOAD-BEARING TEST. Legitimate deep work is indistinguishable from the
            # failure this warns about, so blocking would be wrong and would get the
            # whole guard switched off -- taking the prompt gate with it.
            #
            # It also pins the ABSENCE of permissionDecision, not merely that it is not
            # 'deny'. An 'allow' would reach the model just as well and would be a second
            # bug: allow bypasses the permission system, so a warning would silently
            # pre-approve every Edit and Write it fired on.
            $t = New-Transcript -Name 'notdeny.jsonl' -Count 50
            $result = Invoke-Guard -ToolName 'Edit' -Transcript $t

            $result.Decision | Should -Not -Be 'deny'
            $result.Raw | Should -Not -Match '"permissionDecision"'
            $result.Raw | Should -Match '"additionalContext"'
        }

        It 'puts the text where the model reads it' {
            # additionalContext is the PreToolUse field the harness injects into model
            # context. Stdout alone reaches the transcript view and nothing else, so a
            # warning emitted any other way accomplishes nothing.
            $t = New-Transcript -Name 'shape.jsonl' -Count 50
            $envelope = (Invoke-Guard -Transcript $t).Raw | ConvertFrom-Json
            $envelope.hookSpecificOutput.hookEventName | Should -Be 'PreToolUse'
            $envelope.hookSpecificOutput.additionalContext | Should -Not -BeNullOrEmpty
        }

        It 'says the five things it exists to say' {
            # The text is the whole mechanism here, so it is pinned. Fires repeatedly, so
            # it stays short: a wall of text gets ignored.
            $t = New-Transcript -Name 'text.jsonl' -Count 50
            $text = ((Invoke-Guard -Transcript $t).Raw | ConvertFrom-Json).hookSpecificOutput.additionalContext

            $text | Should -Match 'DEFAULT posture'
            $text | Should -Match 'VOLUME OF TOOL OUTPUT'
            $text | Should -Match 'not the size of the task'
            $text | Should -Match '50 tool calls in a row'
            $text | Should -Match 'dispatch the rest'
            $text | Should -Match 'agent-kickoff'
            @($text -split "`n").Count | Should -BeLessOrEqual 12
        }
    }

    Context 'what clears it' {

        It 'resets on a recent Agent dispatch' {
            # Forty calls, with a dispatch five back. The streak is what has happened
            # SINCE the last delegation, so this session is orchestrating and is left
            # alone.
            $t = New-Transcript -Name 'reset.jsonl' -Count 40 -DispatchAtDepth 5
            (Invoke-Guard -Transcript $t).Decision | Should -Be 'pass'
        }

        It 'warns again once the dispatch falls out of the window' {
            # The reset is not permanent. A dispatch twenty-six calls back does not
            # excuse the twenty-five calls since.
            $t = New-Transcript -Name 'stale.jsonl' -Count 60 -DispatchAtDepth 26
            (Invoke-Guard -Transcript $t).Decision | Should -Be 'warn'
        }

        It 'goes quiet when JANET_DELEGATION_GUARD is off' {
            $t = New-Transcript -Name 'off.jsonl' -Count 50
            $previous = $env:JANET_DELEGATION_GUARD
            $env:JANET_DELEGATION_GUARD = 'off'
            try {
                (Invoke-Guard -Transcript $t).Decision | Should -Be 'pass'
            }
            finally {
                $env:JANET_DELEGATION_GUARD = $previous
            }
        }

        It 'resets on a user turn, so a run of small errands does not add up' {
            # Unrelated requests, five hands-on calls each, and not one of them a reason
            # to spawn an agent. Warning here would train exactly the click-past reflex a
            # warn-only guard cannot afford.
            #
            # The call totals are 25 and 50 ON PURPOSE. The first draft used the five
            # six-call errands the scenario was reported as, which totals 30 -- and 30 is
            # between crossings, so the test passed with the reset ripped out and proved
            # nothing. Landing on a crossing is what makes the mutation bite.
            foreach ($requests in 5, 10) {
                $t = New-ErrandTranscript -Name "errands$requests.jsonl" -Requests $requests -CallsEach 5
                (Invoke-Guard -Transcript $t).Decision | Should -Be 'pass' -Because "$requests errands of five calls are $($requests * 5) calls in total"
            }
        }

        It 'still warns for a long run inside one request' {
            # The other half of the user-turn reset, and the half that matters: the
            # failure this guard was built for was ninety-odd calls under ONE request.
            # Measured over that real transcript, the reset moves the longest streak from
            # 147 to 144 and the warning still fires at 25, 50, 75, 100 and 125 -- so the
            # reset is admissible. A reset that swallowed this case would not be.
            $t = New-Transcript -Name 'onerequest.jsonl' -Count 50 -UserTurnAtDepth 50
            (Invoke-Guard -Transcript $t).Decision | Should -Be 'warn'
        }

        It 'does not treat a task-notification as a user turn' {
            # A background agent reporting in arrives as a type 'user' entry, but it is
            # the harness talking, not a person making a new decision. Forgiving a streak
            # because an agent happened to finish would be a silent hole.
            $t = New-Transcript -Name 'notification.jsonl' -Count 50 -TaskNotificationAtDepth 6
            (Invoke-Guard -Transcript $t).Decision | Should -Be 'warn'
        }

        It 'leaves unwatched tools alone' {
            $t = New-Transcript -Name 'other.jsonl' -Count 50
            (Invoke-Guard -ToolName 'WebFetch' -Transcript $t).Decision | Should -Be 'pass'
        }
    }

    Context 'fails open, always' {
        # A guard that breaks a session because it could not read a file is worse than
        # no guard at all.

        It 'passes when no transcript path is in the payload' {
            (Invoke-Guard).Decision | Should -Be 'pass'
        }

        It 'passes when the transcript does not exist' {
            (Invoke-Guard -Transcript (Join-Path $TestDrive 'absent.jsonl')).Decision | Should -Be 'pass'
        }

        It 'passes when the transcript is not JSON at all' {
            $t = Join-Path $TestDrive 'garbage.jsonl'
            Set-Content -LiteralPath $t -Value (1..50 | ForEach-Object { "not json at all $_" }) -Encoding utf8
            (Invoke-Guard -Transcript $t).Decision | Should -Be 'pass'
        }

        It 'passes when transcript lines are JSON of the wrong shape' {
            $t = Join-Path $TestDrive 'wrongshape.jsonl'
            Set-Content -LiteralPath $t -Value (1..50 | ForEach-Object { '{"type":"assistant","message":{"content":"a string, not blocks"}}' }) -Encoding utf8
            (Invoke-Guard -Transcript $t).Decision | Should -Be 'pass'
        }
    }

    Context 'the prompt gate still gates' {
        # Regression pins for path one, which existed first and must not have moved.

        It 'denies a C# structural prompt carrying no RazorGraph instructions' {
            Invoke-Guard -ToolName 'Agent' -ToolInput @{ prompt = 'Refactor src\Nav.Core\Grid.cs and find the callers' } |
                Select-Object -ExpandProperty Decision | Should -Be 'deny'
        }

        It 'passes a C# structural prompt that names the razorgraph tools' {
            Invoke-Guard -ToolName 'Agent' -ToolInput @{ prompt = 'Refactor src\Nav.Core\Grid.cs. Use mcp__razorgraph__find_nodes first.' } |
                Select-Object -ExpandProperty Decision | Should -Be 'pass'
        }

        It 'passes a prompt that is not about C# structure' {
            Invoke-Guard -ToolName 'Agent' -ToolInput @{ prompt = 'Summarise the deployment notes in README.md' } |
                Select-Object -ExpandProperty Decision | Should -Be 'pass'
        }

        It 'gates the Task spelling too' {
            Invoke-Guard -ToolName 'Task' -ToolInput @{ prompt = 'List the implementations of the interface in src\Foo.cs' } |
                Select-Object -ExpandProperty Decision | Should -Be 'deny'
        }

        It 'writes no invented graphId into the prompt it hands back' {
            # It used to say: use graphId "stub", taken from the solution's file name.
            # Nothing files graphs under that name except convention, and this text goes
            # INTO a subagent prompt -- so the agent inherited an id that need not exist
            # and read the next line as licence to build a duplicate. The same defect
            # was fixed in Invoke-GraphGuard.ps1 on 2026-09-14; this one shipped it to
            # an agent that could not know better. Found only because the reason text
            # had never been asserted by anything.
            $raw = (Invoke-Guard -ToolName 'Agent' -ToolInput @{
                    prompt = 'Refactor src\Nav.Core\Grid.cs and find the callers'
                }).Raw
            $reason = ($raw | ConvertFrom-Json).hookSpecificOutput.permissionDecisionReason

            $reason | Should -Not -BeNullOrEmpty
            $reason | Should -Not -Match 'graphId\s+"?stub"?'
            $reason | Should -Match 'list_graphs'
        }

        It 'tells the agent to trust builtAt rather than loadedAt' {
            # loadedAt says when a graph entered the server, not when it was built, so a
            # graph loaded a minute ago can have been built days earlier. The block spent
            # months naming the field that cannot answer the question it was asked.
            $raw = (Invoke-Guard -ToolName 'Agent' -ToolInput @{
                    prompt = 'Refactor src\Nav.Core\Grid.cs and find the callers'
                }).Raw
            $reason = ($raw | ConvertFrom-Json).hookSpecificOutput.permissionDecisionReason

            $reason | Should -Match 'builtAt'
        }

        It 'still denies while JANET_DELEGATION_GUARD is off' {
            # The off switch belongs to the volume warning, NOT to this gate. Path one is
            # ENFORCED in the manifest and was escalated from advisory after four prompts
            # in one session skipped it; an environment variable that silently turned it
            # off would make the startup brief's "ENFORCED" label untrue. When the volume
            # path was added, its opt-out was checked before the tool name was read and
            # disabled both -- this pins that it does not.
            $previous = $env:JANET_DELEGATION_GUARD
            $env:JANET_DELEGATION_GUARD = 'off'
            try {
                Invoke-Guard -ToolName 'Agent' -ToolInput @{ prompt = 'Refactor src\Nav.Core\Grid.cs and find the callers' } |
                    Select-Object -ExpandProperty Decision | Should -Be 'deny'
            }
            finally {
                $env:JANET_DELEGATION_GUARD = $previous
            }
        }

        It 'never applies the volume warning to a dispatch' {
            # A long streak plus an Agent call is the moment the session is finally doing
            # the right thing. Warning there would be exactly backwards.
            $t = New-Transcript -Name 'dispatchnow.jsonl' -Count 50
            Invoke-Guard -ToolName 'Agent' -ToolInput @{ prompt = 'Summarise the deployment notes in README.md' } -Transcript $t |
                Select-Object -ExpandProperty Decision | Should -Be 'pass'
        }
    }
}
