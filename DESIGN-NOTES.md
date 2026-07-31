# Janet — Design Notes

Transferable patterns from the 2024–2026 build, written from memory and reasoning
rather than copied from employer artifacts. This is the part of the work that is
portable: the ideas, not the integrations.

Written 2026-07-27, during the civilian rebuild.

---

## 1. Manifest-driven startup

**Pattern.** Don't hand the agent a prose "context file" and hope it reads the right
things. Give it an explicit, mechanical contract: a `startup-manifest.json` listing
files to read and commands to run, in order.

**Why it beats a prose root file.** A summary document drifts silently — it stays
syntactically valid while becoming factually wrong, and nothing detects that. A
manifest is checkable: every entry either resolves or it doesn't. You can lint it,
you can test it, and a broken entry is a hard failure instead of a quiet degradation.

**Shape:**

```json
{
  "read":    [ { "path": "...", "why": "..." } ],
  "run":     [ { "cmd": "...", "captureAs": "..." } ],
  "order":   "sequential",
  "onMissing": "fail"
}
```

**The lesson underneath it:** prefer contracts that fail loudly over documents that
degrade quietly. This generalizes well past agent startup.

---

## 2. Progressive disclosure via skills

**Pattern.** Capability lives in per-topic `SKILL.md` files, not in one giant system
prompt. The agent loads a skill's full instructions only when the task matches it.

**Why.** Context is the scarce resource. A monolithic prompt pays the token cost of
every capability on every turn, and the instructions interfere with each other —
guidance for task A subtly biases behavior on unrelated task B. Skills make capability
additive rather than multiplicative.

**What made skills work in practice:**

- One skill = one workflow with a clear trigger. If you can't write the trigger
  sentence, the skill is too vague to fire correctly.
- Skills are markdown, not code. No compile step, no deploy. The edit-test loop is
  seconds, which is the whole reason the catalog grew.
- Scripts live *beside* the skill, not inside it. The `SKILL.md` explains judgment;
  the `.ps1` does deterministic work. Keeping them separate meant the scripts stayed
  independently testable and independently useful.
- A skill that only ever gets invoked explicitly by name is a slash command wearing a
  costume. Real skills fire on task shape.

---

## 3. Thread stack

**Pattern.** A push/pop/show stack of investigation topics (`Push-ThreadStack.ps1` et al.,
included in `scripts\`).

**Why it exists.** Debugging is a depth-first search and human working memory is not.
You start on A, notice B, chase B into C, fix C — and then have no idea whether you
ever finished A. The stack makes the descent explicit and gives you an unwind path.

This is the single most-used thing in the toolkit and it is about 40 lines of code.
Ratio of value to complexity is the highest in the whole framework. Worth remembering
as a general lesson about which tools actually get used.

---

## 4. Graph-first code analysis

**Pattern.** Give the agent a compiler's semantic model instead of text search. For
C#, that meant an MCP server wrapping Roslyn: symbols, references, callers,
implementations, overload resolution, call/dependency graph export, cycle detection,
impact slices.

**Operating rule: graph first, narrative second.** Query the schema, ask for the
narrowest slice that answers the question, then read source *only* to spot-check the
claims that matter. Never open files and start summarizing.

**Why it matters more than it sounds.** Text search cannot answer "which overload
does this call bind to," "who actually implements this interface," or "what breaks if
I change this signature." An agent without a semantic model will confidently guess at
exactly these questions, and the guesses are plausible enough to survive review. The
graph turns speculation into evidence.

**Generalizes to:** any language with a real language server. The pattern is
"expose the compiler's model as agent tools," not anything C#-specific.

**Note:** the original implementation was employer work product and was left behind.
The pattern has since been rebuilt clean-room as `RazorGraph.Mcp` in
`D:\Repos\RazorGraphTool` (2026-07-27) — an MCP stdio server over a Roslyn + Razor
code graph, built on the public RazorGraphTool codebase and the official
`ModelContextProtocol` SDK. See `notes\razorgraph-mcp-server.md` for its shape and
operational caveats.

---

## 5. Deterministic edits via JSON plan

**Pattern.** `Invoke-SurgicalEdit.ps1` (included). The agent emits a JSON plan of exact
operations — `delete`, `removeLines`, `removeParameter`, `removeProperty`,
`removeArgument`, `replace`, `insertAfter` — and a script executes it.

**Why split it that way.** Models are good at *deciding* what to change and unreliable
at *performing* dozens of mechanical edits without drift. Separating the two gets you
the model's judgment plus deterministic execution, and the plan is reviewable before
anything touches disk. Failures become inspectable artifacts instead of a mangled tree.

---

## 6. Reviewer personas — the pattern, and its limit

**Pattern.** Mine a reviewer's historical comments, extract recurring concerns, and
pre-check your own work against them before requesting review.

**Why it worked.** It shortened review cycles substantially. Most review feedback is
predictable in aggregate; a reviewer who always asks about negative test cases will
ask again, so just write them first.

**The limit, learned in hindsight.** These artifacts are behavioral profiles of real,
named people who did not consent to being profiled. That's fine as an ephemeral
private prep note. It is *not* fine as a durable document — and it should never leave
the employer's systems. The portable version is depersonalized: keep the engineering
principles, drop the person.

**The principles worth keeping, stated generically:**

- Capture *why* in code comments — non-obvious constants, workarounds,
  environment-specific behavior, architecture decisions. Future readers include agents.
- Tests must be able to fail for the right reason. Assertions that cannot fail are
  worse than no test: they broadcast false confidence.
- Move/rename PRs owe evidence — every delete has a matching add, old names have zero
  remaining references. Show the diff stat and the search.
- A warnings-fix PR must not silently change runtime defaults. If nullability cleanup
  alters behavior, that's a separate decision PR.
- No hardcoded machine-specific or environment-specific values in shared content.
- Runbooks must never route to a named individual. Document the procedure or the team
  alias. "Ask <person>" is a single point of failure and it expires the day they leave.

That last one is the whole reason handoff corpora exist. It is also the one I'd
enforce hardest if starting over.

---

## 7. Two-question routing for expensive queries

**Pattern.** Before issuing a costly telemetry query, force two questions: *what
exactly am I trying to learn*, and *what is the cheapest query that would distinguish
the hypotheses*. Only then query.

**Why.** Agents default to broad exploratory queries that time out, return unusable
volumes, or silently truncate. Making hypothesis-formation an explicit prerequisite
converts flailing into bisection. Same discipline a good on-call engineer applies;
it just has to be written down for the agent.

---

## 8. Circuit-breakers around flaky infrastructure

**Pattern.** When an external tool is known to hang, catch it *inside* the agent loop
with an explicit timeout and fallback, rather than letting the harness surface a
stall.

**Why.** An agent blocked on a hanging call produces no output and no diagnosis — the
worst possible failure mode, because it's indistinguishable from thinking. A
circuit-breaker turns it into a fast, legible error the agent can route around.

**General principle:** an agent's tools will fail. Design the failure path deliberately
or you'll get the default one, which is silence.

---

## 9. Per-scope storage, not shared

**Pattern.** Per-user / per-scope containers with scoped credentials, rather than one
shared store.

**Why.** Centralized convenience is centralized failure. A shared store means a shared
rate limit, a shared blast radius, and a permissions model that ratchets toward
over-broad. Per-scope containers cost effectively nothing at small scale and remove an
entire category of contention. Meta's Second Brain post independently reported hitting
exactly this wall — they needed a 10x capacity increase on shared cloud storage.

See `notes\meta-second-brain-vs-janet.md` for the fuller comparison.

---

## 10. The handoff-corpus format

Not agent architecture, but the most reusable artifact of the whole project.

**Rules that made it work:**

1. One note per file. One decision, one landmine, one person.
2. Filenames are topic-prefixed and kebab-case. The filename *is* the index entry.
3. Every file opens with an H1 and a one-sentence summary. Retrieval hits the summary.
4. Cross-reference liberally; do **not** deduplicate. Written for retrieval, not
   linear reading — duplication aids discovery.
5. A machine-readable `manifest.json` is the source of truth for what exists.
6. Density over polish. Include the why, the stories, the rejected alternatives.
   Stream-of-consciousness with structure beats polished prose.
7. Write for an LLM reader, not a human one. This changes what you include: more
   context, more explicit cross-links, less narrative smoothing.

**What I'd add next time:** a classification field per file from day one —
`portable` / `employer-confidential` / `personal`. Sorting 640 files by hand a month
after departure is entirely avoidable work, and the person best placed to make each
call is whoever wrote the file, at the moment they wrote it.

---

## 11. What I'd do differently

- **Classify at authoring time** (above). The single highest-leverage change.
- **Keep personal tooling in a personal repo from day one.** The genuinely generic
  utilities — encoding fixes, file writers, mermaid embedding, the thread stack —
  had no reason to live in employer repos. They ended up entangled purely by default,
  and untangling them afterward cost more than separating them would have.
- **Don't profile named colleagues in durable artifacts.** See §6.
- **Write the operational discipline down earlier.** The query routing, the
  circuit-breakers, the verification-before-summary rule — those were what made the
  framework trustworthy, and they lived in my head for far too long before becoming
  skills.
