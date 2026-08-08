# Build retrospective: review, handoff, and what I'd do differently

Retrospective material from the 2024–2026 build, moved out of `DESIGN-NOTES.md`
on 2026-08-08 because it is read once when the question comes up, not on every
session start. The patterns it describes are real and unchanged; they are simply
not session-operating rules, and every startup was paying for them.

Written from memory and reasoning rather than copied from employer artifacts.

---

## Reviewer personas — the pattern, and its limit

*(was DESIGN-NOTES §6)*

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

## The handoff-corpus format

*(was DESIGN-NOTES §10)*

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

## What I'd do differently

*(was DESIGN-NOTES §11)*

- **Classify at authoring time** (above). The single highest-leverage change.
- **Keep personal tooling in a personal repo from day one.** The genuinely generic
  utilities — encoding fixes, file writers, mermaid embedding, the thread stack —
  had no reason to live in employer repos. They ended up entangled purely by default,
  and untangling them afterward cost more than separating them would have.
- **Don't profile named colleagues in durable artifacts.** See the reviewer-persona
  limit above.
- **Write the operational discipline down earlier.** The query routing, the
  circuit-breakers, the verification-before-summary rule — those were what made the
  framework trustworthy, and they lived in my head for far too long before becoming
  skills.
