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
Rebuilt clean-room as `RazorGraph.Mcp` — shape, tool surface, and its several
operational caveats in `note.razorgraph-mcp-server`.

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

Mine a reviewer's recurring concerns and pre-check against them; then depersonalize,
because a behavioral profile of a named colleague is not a durable artifact. The six
generic review principles that survive the depersonalization are in
`notes\build-retrospective.md` (`note.build-retrospective`).

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
exactly this wall. Fuller comparison: `note.meta-second-brain-vs-janet`.

---

## 10. The handoff-corpus format

The document format that outlived the project — one note per file, filename as index
entry, cross-reference without deduplicating, a machine-readable manifest as source of
truth, written for an LLM reader rather than a human one.

In `notes\build-retrospective.md` (`note.build-retrospective`).

---

## 11. What I'd do differently

Classify every file `portable` / `employer-confidential` / `personal` at authoring
time rather than sorting 640 of them by hand after departure; keep personal tooling in
a personal repo from day one; don't profile named colleagues in durable artifacts;
write the operational discipline down earlier.

In `notes\build-retrospective.md` (`note.build-retrospective`).

---

## 12. Discriminator front end, enumerable back ends

When complex conditional logic is really dispatch in disguise, split it: one
discriminator that only routes, and a catalog of fixed back ends that only compute.
The tell is a method whose nesting depth grows every time the business adds a case.
Includes the duplication corollary — duplication behind a discriminator is inventory,
duplication scattered through nested conditionals is contraband — and what a
flow-equivalence prover adds.

In `notes\discriminator-front-end.md` (`note.discriminator-front-end`).
