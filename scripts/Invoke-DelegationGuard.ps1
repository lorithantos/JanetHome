<#
.SYNOPSIS
    PreToolUse hook with two paths. It refuses to delegate C# structural work to a
    subagent unless the prompt carries explicit RazorGraph instructions, and it warns --
    without blocking -- when the session has run a long stretch of hands-on tool calls
    and dispatched nothing.

.DESCRIPTION
    Two failures, one boundary. The first is delegating badly; the second is never
    delegating at all.

    PATH ONE, the prompt gate (Agent/Task). Agents inherit the tool surface and none of
    the operating rules. A prompt that does not say "graph first" gets a text-first
    agent, every time.

    The failure this exists for, 2026-09-05: a session spawned four subagents for C#
    work and gave none of them any RazorGraph instruction. All four fell back to grep.
    One agent's own retrospective was "I ran the whole sweep on grep and only reached
    for the graph after you asked" -- and its later graph pass found things the grep
    could not: zero nodes across all 16 projects, where the grep had needed a
    hand-written exclusion list, and a compiled constructor signature proving a removed
    field was gone from a record's shape rather than just from the text of a file.

    An ADVISORY rule already said to do this, and a skill already carried the template.
    Both were in force and both were skipped four times. That is the argument for a
    gate, and it is the argument Invoke-ResearchGuard.ps1 makes in its own comments: an
    agent does not skip a step from fatigue, it skips from rationalisation.

    Deliberately narrow. It fires only when the prompt looks like C# structural work --
    a .cs, .csproj or .slnx file, a src path, or the words caller, method, class,
    namespace, implementations, blast radius. Prose, config, PowerShell and web research
    pass untouched, because a guard that fires on everything gets clicked past.

    One check, not five: does the prompt contain the literal mcp__razorgraph__. That is
    the single thing whose absence caused the failure. A brittle multi-check gate that
    tried to regex the rest of the kickoff checklist would get disabled.

    The denial does the work. It finds the solution file and hands back a ready-to-paste
    block with real tool and file names in it, rather than a template full of brackets,
    or a scolding.

    It deliberately does NOT derive a graphId. Until 2026-09-14 it took one from the
    solution's file name, which is a convention nothing enforces -- the id is whatever
    was passed to build_solution -- so the block asserted an id that need not exist, and
    its own next line then read as licence to build a duplicate of a graph already
    loaded under another name. That mattered more here than in Invoke-GraphGuard.ps1,
    because this text is written INTO a subagent prompt: the agent inherits the wrong id
    with no way to know better, and pays for it in a full Roslyn compile.

    PATH TWO, the hands-on volume warning (Read/Grep/Glob/Edit/Write/Bash/PowerShell).
    Path one only fires once the controller has ALREADY decided to delegate, so a
    session that quietly does everything itself passes no gate at all -- there is no
    tool call to intercept. note.subagent-delegation named that gap on 2026-09-07 and
    left it open; the recurrence that closed it was a session making about ninety
    consecutive hands-on tool calls after a single Agent dispatch.

    So this path counts CONSECUTIVE recent tool calls in the transcript carrying no
    Agent or Task dispatch, and past the window it warns.

    Three things bound that count, all of them settled against real transcripts on
    2026-09-07 rather than guessed at.

    IT DOES NOT RUN INSIDE A SUBAGENT. The payload carries agent_id (and agent_type)
    only when the calling session is a subagent; a main session's payload has neither
    key. Measured on this machine: a subagent's payload carried
    agent_id 'a2a3502c4a6489379', and a concurrent main session in another repo carried
    no agent_id at all. The reason this matters is not the obvious one. transcript_path
    inside a subagent points at the PARENT's transcript, not the child's -- so without
    the check the guard reads the parent's streak and warns the CHILD about it, which
    is unactionable twice over: a subagent cannot change what its parent did, and a
    subagent has no business spawning nested agents. Forty tool calls inside a subagent
    is the guard working, not failing.

    IT WARNS ONCE PER CROSSING, not once per call. It fires only when the streak is
    exactly a multiple of the window -- 25, 50, 75 -- so a long unbroken run gets
    escalating reminders instead of a wall. Stateless on purpose: a state file would
    have to be keyed per session and cleaned up, and this hook runs before every
    hands-on call. Measured over the transcript of the failure that motivated the path:
    109 warnings under warn-on-every-call, 5 under this rule, at streaks 25, 50, 75,
    100 and 125. A warn-only guard's entire power is being read, and 109 warnings is
    how one gets switched off.

    A USER TURN RESETS THE STREAK, the same way a dispatch does. Five unrelated small
    requests of six calls each are not the failure this exists for, and warning at the
    thirtieth call across them trains the click-past reflex. This was checked against
    the real transcript before it was applied, because a reset that swallowed the real
    case would have been the wrong fix: the ninety-odd hands-on calls there ran between
    one user turn at call 4 and the next at call 162, so the reset changes the maximum
    streak from 147 to 144 and the warning still fires at 25, 50, 75, 100 and 125.
    A <task-notification> does not count as a user turn -- it is the harness announcing
    that a background agent finished, not a person making a new decision.

    IT MUST NOT BLOCK, and that is a design decision rather than caution. Legitimate
    deep work exists -- the small in-session edits the doctrine explicitly permits look
    exactly like the failure from the outside -- and a gate that refuses real work is a
    gate that gets switched off, taking path one with it. So the decision field is
    omitted entirely and the text rides in hookSpecificOutput.additionalContext, which
    the harness injects into model context. Note that permissionDecision 'allow' would
    ALSO have carried the message, and would have been wrong: 'allow' bypasses the
    permission system, so a warning would silently pre-approve every Edit and Write it
    fired on. Saying nothing about the decision leaves the normal prompt flow intact.

.PARAMETER InputJson
    Hook payload, for testing. Normally omitted -- the real invocation gets it on stdin.
    Pipe input does not bind to a [string] parameter, so tests must pass -InputJson.

.OUTPUTS
    A PreToolUse deny decision, a PreToolUse additionalContext warning, or nothing.

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-DelegationGuard.ps1" -InputJson '{"tool_name":"Agent","tool_input":{"prompt":"Refactor src\\Nav.Core\\Grid.cs"}}'

.EXAMPLE
    & "$env:JanetBase\scripts\Invoke-DelegationGuard.ps1" -InputJson '{"tool_name":"Read","tool_input":{},"transcript_path":"C:\\t.jsonl"}'

.NOTES
    Wired via BOTH .claude\settings.json and ~\.claude\settings.json as a PreToolUse
    hook, on Agent|Task for path one and on Read|Grep|Glob|Edit|Write|Bash|PowerShell
    for path two. The project-level file arms it when the project dir is JanetHome; the
    user-level file arms it by absolute path everywhere else -- and the failure that
    motivated path one happened while the project dir was a DIFFERENT repo, so the
    user-level wiring is the one that matters.

    $env:JANET_DELEGATION_GUARD = 'off' silences PATH TWO ONLY -- the volume warning.
    Path one has no opt-out and is not meant to have one: it is ENFORCED in the
    manifest, it was escalated from advisory after being skipped four times in a
    single session, and a gate you can turn off with an environment variable is an
    advisory rule wearing a gate's clothes.
    $env:JANET_DELEGATION_WINDOW overrides the 25-call window of path two.

    Fails OPEN everywhere: no transcript, an unreadable one, a malformed payload, a
    missing field or any exception -- exit 0 and say nothing. A guard that breaks a
    session because it could not read a file is worse than no guard.

    Exits 0 always -- a hook that crashes must not block unrelated work.
#>
[CmdletBinding()]
param(
    [string]$InputJson
)

Set-StrictMode -Version Latest

# Consecutive hands-on tool calls tolerated before path two says something. Chosen high
# on purpose: a real batch of in-session work runs to a dozen or two, and a warning that
# arrives during ordinary work is a warning that gets read past.
$script:DefaultWindow = 25

# The tools path two watches. They are the ones that GENERATE TOOL OUTPUT, which is the
# thing delegation exists to keep out of the session.
$script:VolumeTools = @('Read', 'Grep', 'Glob', 'Edit', 'Write', 'Bash', 'PowerShell')

function Get-Prop {
    param($Object, [string]$Name)
    if ($null -ne $Object -and $Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
}

# The narrow trigger: a file extension or path shape that only occurs in a C# repo, or
# one of the structural words that name exactly the questions the graph answers better
# than text does -- callers, implementers, blast radius.
function Test-CSharpStructuralPrompt {
    param([string]$Prompt)

    $patterns = @(
        '\.cs\b',
        '\.csproj\b',
        '\.slnx\b',
        '\bsrc[\\/]',
        '\bcallers?\b',
        '\bmethods?\b',
        '\bclass(es)?\b',
        '\bnamespaces?\b',
        '\bimplementations?\b',
        '\bblast radius\b'
    )

    foreach ($p in $patterns) {
        if ($Prompt -match $p) { return $true }
    }
    return $false
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

# Is this transcript entry a real user turn -- a person typing -- rather than a tool
# result, a harness note, or a background agent reporting in?
#
# The shapes, taken from a live transcript: a tool result is a type 'user' entry with a
# toolUseResult field and a tool_result content block; a harness note carries isMeta; a
# finished background agent arrives as a type 'user' entry whose text opens with
# <task-notification>, which is the harness talking and not a new decision by anyone.
# Only what is left is a user turn.
function Test-UserTurn {
    param($Entry)

    if ($null -ne (Get-Prop $Entry 'toolUseResult')) { return $false }
    if (Get-Prop $Entry 'isMeta') { return $false }

    $content = Get-Prop (Get-Prop $Entry 'message') 'content'
    $text = ''
    if ($content -is [string]) {
        $text = $content
    }
    elseif ($null -ne $content) {
        foreach ($block in @($content)) {
            if ((Get-Prop $block 'type') -eq 'tool_result') { return $false }
            if ((Get-Prop $block 'type') -eq 'text') { $text += [string](Get-Prop $block 'text') }
        }
    }

    if ($text.TrimStart() -like '<task-notification>*') { return $false }
    return $true
}

# Names of the last N tool_use blocks in the session transcript, newest first, with the
# marker <user-turn> standing in the sequence wherever a person spoke.
# Any line that fails to parse is skipped. Returns a comma-wrapped array.
# Started as a copy of Invoke-GraphGuard.ps1's reader and has since diverged -- that one
# has no reason to care about user turns. Still deliberately a copy rather than a shared
# module: these are standalone hook scripts, and a dot-source is one more path that can
# fail to resolve inside a hook that must fail open.
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

        if ((Get-Prop $entry 'type') -eq 'user') {
            if (Test-UserTurn $entry) { $names.Add('<user-turn>') }
            continue
        }

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

# How many tool calls back the most recent Agent or Task dispatch is. Counts from the
# newest end and stops at the first dispatch, so ONE delegation resets the count.
#
# A user turn stops it too. The doctrine's volume test is applied per decision, and a
# new request from a person is a new decision -- five unrelated six-call errands are not
# the failure this guard was built for. This does not blunt it: over the transcript of
# the failure it WAS built for, the reset moves the longest streak from 147 to 144,
# because those ninety-odd calls ran between one user turn and the next.
function Get-NonDispatchStreak {
    param([string[]]$Names)

    $streak = 0
    foreach ($name in $Names) {
        if ($name -eq 'Agent' -or $name -eq 'Task') { break }
        if ($name -eq '<user-turn>') { break }
        $streak++
    }
    return $streak
}

# The window, from the environment when it is set to something sane.
function Get-DelegationWindow {
    $configured = 0
    if ($env:JANET_DELEGATION_WINDOW -and [int]::TryParse($env:JANET_DELEGATION_WINDOW, [ref]$configured) -and $configured -gt 0) {
        return $configured
    }
    return $script:DefaultWindow
}

try {
    $raw = if ($InputJson) { $InputJson } else { [Console]::In.ReadToEnd() }
    if (-not $raw) { exit 0 }

    $payload = $raw | ConvertFrom-Json

    # This harness names the tool Agent; older ones call it Task.
    $toolName = [string](Get-Prop $payload 'tool_name')

    # ---------------------------------------------------------------------------
    # PATH TWO: hands-on volume. Warns, never refuses. Returns before path one's
    # code, which reads tool_input.prompt and would find nothing on these tools.
    # ---------------------------------------------------------------------------
    if ($script:VolumeTools -contains $toolName) {
        # The off switch belongs to THIS path only. It used to sit at the top of the
        # try, which quietly handed path one an escape it has never had: that gate is
        # ENFORCED in the manifest, was escalated from advisory precisely because it
        # had been skipped four times in one session, and its rule documents no opt-out.
        # An env var that silently turns an enforced gate off makes the brief's
        # "ENFORCED" label a lie, which is the one thing this manifest exists not to do.
        if ($env:JANET_DELEGATION_GUARD -eq 'off') { exit 0 }

        # NOT INSIDE A SUBAGENT. agent_id is present in the payload only when the caller
        # is one; a main session's payload has no such key. A subagent running forty
        # calls is this guard working, and the warning would be unactionable there --
        # worse, transcript_path in a subagent's payload names the PARENT's transcript,
        # so what the child would be warned about is its parent's streak.
        if (Get-Prop $payload 'agent_id') { exit 0 }

        $transcriptPath = [string](Get-Prop $payload 'transcript_path')
        if (-not $transcriptPath) { exit 0 }
        if (-not (Test-Path -LiteralPath $transcriptPath -PathType Leaf)) { exit 0 }

        $window = Get-DelegationWindow

        # Scan several windows deep so the later crossings -- 50, 75, 100 -- are
        # reachable and the warning can say a real number. Bounded because this runs
        # before every hands-on call, though the bound is nearly free: the whole
        # transcript is read either way, and 200 names costs the same as 50 (measured at
        # ~120ms on a 3MB transcript, both).
        $scan = [Math]::Min($window * 8, 400)

        # Assigned BARE, with no @() around it. The reader returns ,@(...) to stop a
        # one-element result unrolling, so the stream carries ONE item that IS the
        # array; @() would collect that item and hand back an array holding an array,
        # and the streak would count 1 whatever the transcript said. Cost an hour.
        $recent = Get-RecentToolNames -TranscriptPath $transcriptPath -Count $scan
        $streak = Get-NonDispatchStreak -Names $recent

        if ($streak -lt $window) { exit 0 }

        # ONCE PER CROSSING. Warning at 26, then 27, then 28 is how a warn-only guard
        # gets switched off, and its only power is being read. Firing on the multiples
        # gives escalating reminders instead of a wall, and needs no state file -- which
        # would have to be keyed per session and cleaned up, in a hook that runs before
        # every hands-on call.
        if (($streak % $window) -ne 0) { exit 0 }

        # The scan ran out before a dispatch appeared, so the true streak is unknown and
        # only looks like a multiple because it is pinned at the bound. Saying nothing is
        # the honest answer: by here the warning has already fired at every crossing
        # below, and repeating it on every call is the noise this rule removes.
        if ($streak -ge $scan) { exit 0 }

        $warning = @"
Delegation guard -- NOT a refusal, this $toolName call is going through.

$streak tool calls in a row with no Agent dispatch. Orchestrating is the DEFAULT posture
here, not an escalation for large jobs: the question is not whether a task is big enough to
delegate, but what in it must stay in the main session. And the test is VOLUME OF TOOL OUTPUT,
not the size of the task -- a one-line question whose answer needs a repo sweep is agent work,
a long piece of reasoning that needs no tools is not.
Next move: name what in the batch you are about to run genuinely has to stay here -- one known
file, a two-line edit, a judgment call -- and dispatch the rest. The prompt template is
skills\agent-kickoff\SKILL.md; the doctrine is note.subagent-delegation. Window is $window calls
(JANET_DELEGATION_WINDOW); this fires once per crossing, so the next is at $($streak + $window).
A dispatch or a new request from Lori resets the count. JANET_DELEGATION_GUARD=off silences it.
"@

        # No permissionDecision: 'allow' would bypass the permission prompt on every
        # Edit and Write this fires on, which is a side effect a warning must not have.
        [PSCustomObject]@{
            hookSpecificOutput = [PSCustomObject]@{
                hookEventName     = 'PreToolUse'
                additionalContext = $warning
            }
        } | ConvertTo-Json -Depth 4 -Compress

        exit 0
    }

    # ---------------------------------------------------------------------------
    # PATH ONE: the delegation prompt gate. Unchanged since 2026-09-05.
    # ---------------------------------------------------------------------------
    if ($toolName -ne 'Agent' -and $toolName -ne 'Task') { exit 0 }

    $prompt = [string](Get-Prop (Get-Prop $payload 'tool_input') 'prompt')
    if (-not $prompt) { exit 0 }

    # The one check. Its absence is what caused the failure; the rest of the kickoff
    # checklist is judgment, and a regex would only get it wrong.
    if ($prompt -like '*mcp__razorgraph__*') { exit 0 }

    if (-not (Test-CSharpStructuralPrompt $prompt)) { exit 0 }

    $projectDir = if ($env:CLAUDE_PROJECT_DIR) { $env:CLAUDE_PROJECT_DIR } else { (Get-Location).Path }
    $solution = Get-SolutionFile $projectDir

    if ($null -ne $solution) {
        # NO graphId is derived here, and the omission is the correction. Filing a graph
        # under its solution's file name is a convention nothing enforces -- the id is
        # whatever was passed to build_solution -- so asserting one told the agent an id
        # that need not exist, and the following line then read as licence to build a
        # duplicate of a graph already loaded under another name. Fixed 2026-09-14,
        # alongside the same defect in Invoke-GraphGuard.ps1: this guard writes the
        # instruction INTO a subagent prompt, so a wrong id here is inherited by an agent
        # with no way to know better.
        $graphBlock = @"
Get a graph: call list_graphs and find the row whose source is
  $($solution.FullName)
and take its graphId. Read the id off that row rather than guessing it from the
file name -- graphs are filed under whatever id built them.
If NO row names that solution: load_graph path=<saved .json> if one exists, else
build_solution path=$($solution.FullName) (slow: a full Roslyn compile).
"@
    }
    else {
        $graphBlock = @"
Get a graph: call list_graphs and use the graphId for this repo.
No .slnx or .sln was found under $projectDir, so name both explicitly in the prompt
yourself: if the graph is not listed, build_solution path=<the .slnx> graphId=<name>
(slow: a full Roslyn compile). Do not leave the agent to guess either one.
"@
    }

    $reason = @"
Delegation guard: this Agent prompt is C# structural work and carries no RazorGraph instructions.

2026-09-05: four subagents were spawned for C# work with no graph instruction, and all four ran
the sweep on grep. The graph pass afterwards found what the grep could not -- zero nodes across
all 16 projects, where the grep had needed a hand-written exclusion list, and a compiled
constructor signature proving a removed field was gone from a record's shape rather than just
from the text of a file. An advisory rule said to do this and a skill carried the template;
both were skipped four times, which is why this is a gate now and not a reminder.

Agents inherit the tool surface and NONE of the operating rules. Paste this into the prompt:

## RazorGraph -- FIRST for any question about C# structure
Load the tools: ToolSearch("select:mcp__razorgraph__list_graphs,mcp__razorgraph__graph_summary,mcp__razorgraph__find_nodes,mcp__razorgraph__get_node,mcp__razorgraph__find_path,mcp__razorgraph__research")
$graphBlock
Pass graphId on every call. Check builtAt -- NOT loadedAt -- against the edits you make, and
rebuild after changing C#: loadedAt only says when a graph entered the server, so a graph loaded
a minute ago can have been built days before.
Graph first, grep second. Grep, Glob and Read are for (a) spot-checking a claim the graph made
and (b) text the graph does not index -- prose, JSON, .ps1, config. They are never the first move
on callers, implementers, blast radius or test reach. If ToolSearch returns no razorgraph tools,
say so in your report before falling back to text.
Ids are exact: m:Type.Name(paramTypes). Check 'truncated' in every result.

Then re-issue the Agent call. The full prompt template, and the checklist to run it past, are in
skills\agent-kickoff\SKILL.md; the doctrine is note.subagent-delegation.
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
    # A broken guard must fail open, not wedge every delegation in the session.
    exit 0
}

exit 0
