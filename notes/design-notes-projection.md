# DESIGN-NOTES projection

`DESIGN-NOTES.md` is one of two files the startup manifest makes every session read in
full. It is growing again, and on 2026-08-12 it was found asserting a mechanism that had
not existed for several commits. Both problems have the same fix, and it is the one this
repo has already applied once to the same file.

Designed 2026-08-12, not built.

## The measurement

| File | Chars | In the read list? |
|---|---|---|
| `DESIGN-NOTES.md` | 10,219 | yes |
| `README.md` | 6,901 | yes |
| **Total** | **17,120** | ~4,300 tokens, every session |

`note.startup-brief-budget` trimmed exactly this list from 28,499 to **12,300**
characters on 2026-08-08. Four days later it is 17,120: **+39%**, and the file it warned
about is 79% of the growth.

Section by section:

| Chars | Section | |
|---|---|---|
| 2,555 | 3. Thread items | 25% of the file |
| 1,272 | 4. Graph-first code analysis | |
| 1,167 | 2. Progressive disclosure via skills | |
| 910 | 1. Manifest-driven startup | |
| 629 | 5. Deterministic edits via JSON plan | |
| 614 | 8. Circuit-breakers | |
| 566 | 12. Discriminator front end | pointer |
| 557 | 9. Per-scope storage | |
| 554 | 7. Two-question routing | |
| 402 | 11. What I'd do differently | pointer |
| 359 | 6. Reviewer personas | pointer |
| 335 | 10. The handoff-corpus format | pointer |

The four sections already converted to pointers on 2026-08-08 average **415** characters.
The eight that were not average **1,032**. The conversion works; it was applied to the
sections consulted once and stopped at the sections that govern how a session works.

## The drift, which is the more serious half

Section 3 said, until 2026-08-12:

> Writers serialise on a named mutex.

That was true of `scripts\ThreadItems.Common.ps1`, which is retired and kept only so
goldens can be generated from it. The shipped path has serialised through
`src\Janet.Core\WriteQueue.cs` since the port -- in-process writers coalesce into one
read-apply-write, a sidecar lock file excludes across processes, and the file is replaced
atomically rather than truncated in place. Corrected in `512e3d2`.

**Nothing detected it.** The manifest guarantees the file is *read*, not that it is
*true* -- and a false claim in a startup-read file is worse than a false claim anywhere
else, because every session ingests it as authority before it is in any position to
check.

### Why the catalog stayed correct and the prose did not

The same port updated `script.show-thread-items`, `script.add-thread-item`,
`script.update-thread-item`, `script.complete-thread-item`, `script.set-active-thread`
and marked `script.thread-items-common` RETIRED, in commit `9ea0927`. Those nodes are
accurate today.

The difference is not care. **The catalog was updated because the work touched it** --
you cannot retire a script without the node in front of you -- and DESIGN-NOTES was not,
because nothing pointed at it from the change. Prose that duplicates a fact maintained
elsewhere will lose to it every time; the only question is how long before someone
notices.

This is section 1's own argument (*"a summary document drifts silently -- it stays
syntactically valid while becoming factually wrong, and nothing detects that"*)
happening to the file that makes it.

## The design

**DESIGN-NOTES carries the claim and the argument. It does not carry the mechanism.**

The split is not by length, it is by what goes stale:

- **Durable** -- the pattern, the failure that motivated it, why the obvious alternative
  is worse. Section 3's *"a structure chosen for the operation you first imagined will
  quietly forbid the operations you actually turn out to need"* is as true after the port
  as before it. So was *"it is shared mutable state with genuinely concurrent sessions."*
- **Perishable** -- which type, which file, which locking primitive. Exactly the sentence
  that was wrong.

Perishable content moves to the catalog node that already indexes the section, where the
work that changes it will have the node open anyway. Retrieval is on demand, and every
`pattern.*` node already carries its section number.

**Section numbers stay put.** `startup-manifest.json` rules cite sections 3, 4, 5, 7 and
8 by number, and three notes cite section 12. Add or repoint; never renumber. A converted
section keeps its identifier, as 6 and 10-12 did.

## Backstop, and what it does not cover

Worth having, because it is nearly free: a gate that fails when a startup-read file names
a script whose catalog node is marked **RETIRED**, or a path that no longer exists. Both
signals already exist -- the `# JANET-SHIM` marker and the node status -- and neither is
checked by anything.

**It would not have caught this one.** Section 3 named the five live shims, which are
correct to name; the stale sentence named a *mechanism*, and no gate reads English. Say
so plainly rather than shipping a gate and believing the class is closed.

Which is the argument for the projection rather than an alternative to it: the reliable
fix is not detecting false mechanism claims in prose, it is **not making them there.**

## Sequence

1. Convert section 3 first -- largest, most recently wrong, and its node
   (`pattern.thread-items`) is current.
2. Sections 4 and 2 next, on the same rule.
3. Add the RETIRED/dead-path gate to the pre-commit checks.
4. Re-measure the read list and record it here. The 2026-08-08 entry recorded a number
   and nothing re-checked it for eleven days.

---

Related: `note.startup-brief-budget`, `note.thread-item-projection`, DESIGN-NOTES
section 1 (manifest-driven startup), section 2 (progressive disclosure).
