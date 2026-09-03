# Delegate the context-expensive half, keep the judgement half

Lori, 2026-09-03, while asking for a commit: *"tasks should be shunted to agents to
preserve your context."*

## The claim

A session's context is the resource that runs out first, and most of what consumes it
is material that only mattered while a task was in flight: diffs, build logs, file
dumps, search results, directory listings. None of that is needed once the task is
done -- only its conclusion is. A subagent is the mechanism that separates the two.
Its tool output stays in its own transcript and dies with it; what comes back is a
report the size of an answer rather than the size of an investigation.

This is [[pattern.progressive-disclosure]] applied to work rather than to capability.
That pattern keeps instructions out of the prompt until they match; this one keeps
evidence out of the transcript once it has been weighed.

## The split

Delegate the half that is expensive to read and cheap to summarise:

- reviewing a large diff
- sweeping a codebase for every caller, every implementer, every occurrence
- running a long build or test suite and extracting what failed
- verifying a claim that requires opening many files
- mechanical multi-file edits (though [[pattern.deterministic-edits]] is often the better
  tool for those, because a plan is reviewable and an agent's intentions are not)

Keep the half that is cheap to state and expensive to get wrong: what the evidence
means, what to do about it, what the commit is actually saying, which of two designs
to take. A subagent that returns "here are the twelve callers" has done its job; a
subagent asked "should we change this API" has been handed a decision that needs the
context it does not have.

## What a delegated prompt must carry

Agents inherit the tool surface but not the operating rules, and this is the failure
mode that recurs. Observed 2026-08-11 (recorded on the startup manifest's advisory
rule): three exploration agents ran text-first and speculated, because nothing in
their prompt told them a graph server existed. Their graph-verified second passes
found facts the text passes had missed.

So a delegated prompt states, explicitly:

- **The environment facts it cannot infer.** On this machine that is at minimum: `git`
  is not on PATH (use `scripts\git.ps1`), there is no bash, and the checkout is CRLF
  while the Write tool emits LF. See [[script.git]].
- **The retrieval instructions**, when the task is code analysis: which RazorGraph
  tools to load through ToolSearch, the `graphId` or saved-graph path, and the
  graph-first rule itself. [[skill.graph-first-code-analysis]] is the content; the
  point here is that it must be *quoted into the prompt*, not assumed.
- **What "done" looks like** and what to report back, in what shape -- because the
  report is the only thing that survives, and an agent that reports its narrative
  instead of its findings has spent the context without buying the answer.
- **The boundary.** What it must not do. An agent told to commit should be told not to
  push; an agent told to investigate should be told not to fix.

## The cost that is not free

Delegation is not strictly cheaper. The agent re-derives orientation the main session
already has, which is duplicated work, and a badly scoped agent returns something
confidently wrong that costs more to detect than the task would have cost to do. The
trade is favourable when the tool output is large relative to the answer, and
unfavourable when it is not: a single file read, one known symbol, a two-line edit are
all cheaper done directly than described to someone else.

The tell is the ratio. If you can predict the shape of the answer but not its content,
and getting the content means reading a lot, delegate.
