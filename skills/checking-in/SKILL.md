---
name: checking-in
description: Commit work here -- when to check in, and the message style this log is written in. Use whenever you are about to commit, are asked to commit or check something in, have just finished a verified unit of work, or are looking at a working tree that has grown to cover more than one thing. Also use when writing a commit sentence for someone else to run.
---

# Checking in

## Check in at every verified unit

A commit is not a milestone. It is the point at which one true thing has been
made true and demonstrated, and the working tree can be described in a
sentence. Reach that point and commit, whether it took four hours or four
minutes.

**The reason here is not tidiness, it is concurrency.** This machine runs more
than one session against the same checkout, and at least one of them commits
with `git add -A` on a timer. On 2026-09-05 that swept up another session's
in-flight work twice, and the attempt to separate it afterwards -- tag, reset,
recommit -- was interrupted midway by a denied command and left the branch two
commits short. 239 lines were missing from `main` and from the remote for
several hours. Nothing was lost in the end, because the reset was tagged first,
but the whole incident is downstream of work sitting uncommitted long enough to
be swept up.

So: uncommitted work here is exposed to somebody else's `git add -A`, not just
to your own mistakes. Committing is how you take it off the table.

## What earns a commit

One of these, finished and verified:

- A behaviour changed, with its tests, its documentation and its catalog nodes.
- A bug fixed, with the regression test that fails without the fix.
- A decision recorded -- a caveat, a note, a manifest rule -- even with no code.

What does not earn one: a checkpoint in the middle of a change, a commit that
does not build, or a commit whose message would have to say "part one of two".
If the work genuinely is not finished, leave it uncommitted and say so; do not
manufacture a green commit out of a half-done state.

## Splitting is a symptom, not a tool

You can stage by hunk. Do not plan on it.

A working tree that needs splitting is one you should have committed twice
already. By the time two changes are in the tree together, they are usually not
separable in a way that produces two honest commits: the second change extends
a method the first one introduced, so the hunks interleave and a first commit
without the second would not build.

That happened on 2026-09-06, in this repo, and the commit message had to carry
a paragraph explaining why it was one commit rather than two. That paragraph is
the tell. **When you find yourself writing the justification for a fused
commit, the mistake was upstream, and it was not committing the first piece
when it was finished and green.**

When the tree really does hold two unrelated changes -- a fix plus a typo you
noticed in passing -- split it. `git add <paths>` per commit is enough; reach
for `git add -p` only when the two live in one file. Never rewrite pushed
history to tidy this up. A messy true history beats a clean one that lost
something.

## The subject line

Look at `git log --oneline -20` before writing one. The house voice:

- **State what is true now**, not what you did. "Notes are capped at the write"
  rather than "Add a notes cap".
- **Two clauses, often**, joined by a comma or `and`, where the second names
  the consequence or the other half: "Notes are capped at the write, and show
  leads by default"; "The build stamp is the warrant for the span".
- **No prefixes.** No `feat:`, no `fix:`, no ticket ids, no scope in brackets.
- **No trailing period.** Under about 72 characters.
- Specific enough to find later. "Add additional caveats" is in this log and is
  the one nobody can locate.

## The body

Prose paragraphs, one per change or per decision. Not bullets, not a file list
-- the diff already carries the file list, and a body that restates it is
wasted.

Say the things a diff cannot:

- **What was true before**, and why it was wrong. Date the failure that
  motivated the change if there was one.
- **The decision, including what was rejected.** "The two show cases left the
  golden suite rather than be corrected" tells a future reader that correcting
  them was considered.
- **Anything surprising in the shape of the change** -- a fused commit, a
  deliberate deviation from a rule, a test left red on purpose. State it
  outright rather than letting a reviewer discover it.
- **A closing verification line**: what you ran and what it said. Test counts,
  the mutation you used, the command whose output you actually read. "Verified:
  365 tests on a clean rebuild, 33 new, each mutation-checked."

ASCII only, and `--` rather than an em dash. Wrap at about 76 characters. The
encoding gate checks the files, not the message, so this one is on you.

Keep the attribution trailer the harness gives you, verbatim and last. Do not
copy it out of an older commit -- it names a model and a session, and both
change.

## Before you commit

The pre-commit gate runs itself since 2026-09-06, so you do not invoke it by
hand: `core.hooksPath` is set to `.githooks` in this clone, and
`Install-JanetEnvironment.ps1` sets it in a fresh one. It runs
PSScriptAnalyzer, the nesting check, Pester, config validation, the encoding
audit and the output contracts over the staged files.

The gate is not the verification. It checks style and encoding; it does not
know whether your change works. Run `dotnet test JanetHome.slnx` yourself, and
mutation-check any test you added -- break the subject, watch it fail, restore
it -- so the closing line of the body is something you observed rather than
something you assume.

Commit and push only when asked. If you were asked to do the work and not to
commit it, say what is in the tree and offer the sentence.

## Related

`script.test-pre-commit` for what the gate runs and what self-skips,
`script.install-janet-environment` for how the hook path gets wired,
`pattern.thread-items` for where the work that did not make it into this commit
belongs.
