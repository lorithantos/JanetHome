<#
.SYNOPSIS
    PreToolUse hook: refuses to delegate C# structural work to a subagent unless the
    prompt carries explicit RazorGraph instructions.

.DESCRIPTION
    Agents inherit the tool surface and none of the operating rules. A prompt that does
    not say "graph first" gets a text-first agent, every time.

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

    The denial does the work. It finds the solution file, derives the graphId from it,
    and hands back a ready-to-paste block with real tool and file names in it, rather
    than a template full of brackets, or a scolding.

.PARAMETER InputJson
    Hook payload, for testing. Normally omitted -- the real invocation gets it on stdin.

.OUTPUTS
    A PreToolUse deny decision, or nothing when the call is fine.

.EXAMPLE
    '{"tool_name":"Agent","tool_input":{"prompt":"Refactor src\\Nav.Core\\Grid.cs"}}' |
        & "$env:JanetBase\scripts\Invoke-DelegationGuard.ps1"

.NOTES
    Wired via BOTH .claude\settings.json and ~\.claude\settings.json as a PreToolUse
    hook on Agent|Task. The project-level file arms it when the project dir is
    JanetHome; the user-level file arms it by absolute path everywhere else -- and the
    failure that motivated it happened while the project dir was a DIFFERENT repo, so
    the user-level wiring is the one that matters.

    Exits 0 always -- a hook that crashes must not block unrelated work.
#>
[CmdletBinding()]
param(
    [string]$InputJson
)

Set-StrictMode -Version Latest

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

try {
    $raw = if ($InputJson) { $InputJson } else { [Console]::In.ReadToEnd() }
    if (-not $raw) { exit 0 }

    $payload = $raw | ConvertFrom-Json

    # This harness names the tool Agent; older ones call it Task.
    $toolName = Get-Prop $payload 'tool_name'
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
        $graphId = [System.IO.Path]::GetFileNameWithoutExtension($solution.Name).ToLowerInvariant()
        $graphBlock = @"
Get a graph: call list_graphs; use graphId "$graphId" (built from $($solution.FullName)).
If it is not listed: load_graph path=<saved .json> if one exists, else
build_solution path=$($solution.FullName) graphId=$graphId (slow: a full Roslyn compile).
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
Pass graphId on every call. Check loadedAt against the edits you make -- rebuild after changing C#.
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
