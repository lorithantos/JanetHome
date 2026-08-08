# Discriminator front end, enumerable back ends

When complex conditional logic is really dispatch in disguise, split it: one
discriminator that only routes, and a catalog of fixed back ends that only compute.

Added 2026-08-03 from the pricing-rewrite experience; written from memory and
reasoning, no employer specifics. Moved out of `DESIGN-NOTES.md` on 2026-08-08 —
it is an architecture pattern you reach for when you meet the shape, not a rule
that governs a session, so it is retrieved rather than read on every start.

---

**Pattern.** When complex conditional logic is really dispatch in disguise, split it:
one discriminator front end that only routes, and a catalog of well-known, fixed back
ends that only compute — state machines expressed as declarative queries, selected by
the front end, never selecting themselves. A large pricing subsystem rewritten this
way collapsed from an unnavigable conditional tangle into a routing table over boring,
named alternatives.

**Why it works.** Deep nesting is a discriminator tangled *into* its computations —
every level of the christmas tree is a routing decision interleaved with work. Pull
the routing out and the back ends go flat on their own. Guard clauses are the
forty-line version of the same move; this is what the code should become when the
tree was doing dispatch all along.

**The duplication corollary.** Duplication is not dangerous because code appears
twice; it is dangerous when the copies are unenumerable and drift silently. A
discriminator over fixed back ends kills both conditions: the copies are a catalog
with names, and drift is detectable because comparison has a defined surface.
Duplication behind a discriminator is inventory; duplication scattered through nested
conditionals is contraband. This cuts against the DRY reflex, deliberately.

**What tooling adds (2026, RazorGraph-era).** With a flow-equivalence prover, "are
these two back ends the same" becomes a provable relation — a pairwise sweep over the
catalog separates merge candidates (equivalent), parameterization candidates (one
canonicalized guard apart), and legitimate divergence (different, with evidence).
Reachability coverage prices the refactor's risk upfront by showing which paths tests
actually exercise. And the finished architecture is a checkable graph invariant:
every path from the discriminator lands in exactly one known back end, and nothing
routes around the front end. Conformance stops being a review-time opinion and
becomes a query.

**Generalizes to:** any domain where variant behavior accreted as nested conditions —
pricing, eligibility, routing, rendering pipelines. The tell is a method whose
nesting depth grows every time the business adds a case.

Related: `skill.christmas-tree-flattening`, `note.pre-checkin-style-janet-level`.
