# Derive, Don't Memorize — raw pieces for an essay

Raw material, not a draft: the pieces of an essay about one cognitive strategy —
store generators, not instances — traced from a calculus class ~43 years ago through
image processing, schedulers, an agent framework, and an ADHD recognition that
arrived decades after the compensations did. Gathered 2026-08-01 from a long
conversation; [LT] marks Lori's material (near-verbatim where possible), [J] marks
framings Claude offered that should be diffed against perception before use.

---

## The spine (candidate thesis)

[J] One person, one theorem, ~43 years of instances: *find the smaller structure
that already contains the answer, and pay per-structure instead of per-item.*
What began as an unrecognized ADHD compensation — derivation because rote
memorization wouldn't hold — turned out to be a superior general strategy, and
eventually an architecture. The essay's turn: the "deficit" was the discovery.

---

## Piece: the quotient rule (senior year, class of 1983)

[LT] Never memorized d(f(x)/g(x)) because it's the same rule as d(f(x)·g(x)⁻¹).
Knowing that means not having to remember — you just need the pattern. Told this
story for ~43 years ("who cares at that point though").

[J] The sign is the payoff: memorized quotient rules fail silently on the sign
(f′g − fg′ or fg′ − f′g? both look plausible). Derived ones can't — the minus
arrives *with a reason* (the −g⁻² from differentiating an inverse), and reasons
don't flip when you're tired. Derivation is checkable; memorization fails
silently. A memorized rule is a prose summary that degrades quietly; a
derivation is a contract that fails loudly.

---

## Piece: physics vs calculus (the apparent contradiction that proves the rule)

[LT] "This is why I was better at physics than at calculus — physics was
recognizing relatively easy calculus problems (they don't generally have
answers that are too hard for the instructor to check). Calculus was long
chains where I had to be certain I remembered everything and where a missed
sign was a wrong answer."

[J] Looks like a paradox — the quotient-rule person, worse at calculus — and
resolves into the thesis's sharpest form. Calculus-as-graded was long serial
symbol chains: perfect recall sustained across every step, errors silent
until the end, correctness carried in working memory the whole way — the
taxed faculty, stressed multiplicatively. Physics was recognition (which
small generator applies?) plus a short derivation — the strong faculty — and,
crucially, physics answers *check themselves*: units must come out, limiting
cases must behave, a dropped sign produces a negative mass and becomes
visibly absurd. Physics failures fail loudly; calculus chains degrade
quietly. The instructor-checkable-answers aside is the selection effect that
guarantees it: physics problems are engineered so a small generator suffices.

[J] The through-line this hands the essay: she has been choosing loud-failure
domains since high school, and the framework's first principle (contracts that
fail loudly over documents that degrade quietly) is that preference,
engineered. Janet is the lifelong project of making software feel like
physics instead of calculus homework.

---

## Piece: the string sort (high school, same era as the calculus class)

[LT] Sorting strings in BASIC on an Apple IIe ("I think it was pre-himem" —
the hardware/BASIC-version label is hedged; the solution is not). String
copies on that machine were brutal: interpreted char-by-char copies, and in
Applesoft, a quadratic garbage collector that could freeze the machine for
minutes, fed by every swap. The solution at 17: sort a parallel array of
integer indexes and never move a string. The permutation is the answer; the
heavy data holds still.

[LT] "I didn't understand pointers at the time. But I invented them anyway."

[J] The capstone, possibly of the whole essay. Two readings, both load-bearing:
1. Independent reinvention is the strongest evidence in the corpus. Everything
   else shows derivation keeping pace with instruction; this shows it producing
   a concept nobody had supplied. Memorization wasn't available — no one had
   taught pointers — so derivation was the only road, and it arrived where the
   field arrived, because indirection is what precipitates for anyone standing
   at those constraints. That's why pointers exist at all.
2. "Didn't understand" is precisely wrong: the concept was fully operational;
   only the *name* was missing. The word "pointer" arrived years after the
   pointer — the way "ADHD" arrived forty years after the compensations.
   Structure first, label later, at every scale — including for her own
   inventions.

[J] Three things this piece does for the essay:
1. It pairs with the quotient rule *in the same school year* — the generator
   running in two domains at once (math class, computer lab), class of 1983.
2. In 2026, Claude re-derived the identical solution from the constraints
   before hearing it — which is the proof it's a generator, not a trick.
   Derivations are recoverable by anyone standing at the same constraints;
   memorized tricks die with their owner. Same reason the derived quotient
   rule can't lose its sign.
3. The memory kept the *move* crisp for 43 years while holding the machine
   label loosely — structure retained, symbol table approximate. The thesis,
   demonstrated in the act of contributing to the essay.

---

## Piece: the memory architecture

[LT] Bad at remembering lots of things; super good at specifics *with a trigger*.
May not know her name, but remembers a photo she's in — usually one of the very
first. Can build a structure around the events of a movie with no idea who the
players are, but can connect them to other movies they're in.

[LT] The movie story: describing *Naked* (Mike Leigh, 1993) by emitting David
Thewlis, Lesley Sharp, Katrin Cartlidge, plus adjacent films for disambiguation —
the horrible *Island of Dr. Moreau*, the lovely *Career Girls* and *The Full
Monty* — plus "incredibly good, but dark." A friend's diary has a specific entry
about the day they met: the conversation was entirely opaque through all the
memory workarounds, even as Lori was trying to explain the movie. The story is
countable-on: told in the friend's presence, with the friend adding flavor —
corroborated, and jointly performed.

[J] That emission *is* a graph query — actor nodes, film edges, a tonal
property — executed against an external index. The retrieval failed; the edges
were sufficient for something else to resolve the label. Not a worse memory —
a different one, missing only the symbol table.

[J] Why names specifically: names are the one thing that can't be derived.
"Thewlis" has no structure; no pattern generates it. Names are pure rote —
the quotient rule of human data. The memory keeps structure and drops arbitrary
tokens, consistently.

[J] The diary coda: the conversation wasn't opaque where it counted. Labels
never arrived; the structure and conviction did. The friend couldn't have named
the film either, afterward — but she wrote down the day. Retrieval failed,
transmission succeeded.

[LT] IMDb changed a bunch of that: repeated exposure eventually encodes names
(can now say the three actors *without* reciting the films). But the encoding
takes far longer than it does for others. That's the burden.

[J] IMDb as external symbol table: supply edges, receive labels. (Same role
research.json plays for the engineering corpus — ids held externally so
retrieval can run on association, the mode that works.)

---

## Piece: the ledger — linear tax, compounding compensation

[LT] Appearing better at names = more repeated exposure; encoding is slow.
Burden. But the ability to do graphs and convert unrelated things into a
coherent thing is *way more* compensation.

[J] The accounting: the encoding tax is linear — paid again for every label,
forever, with repetition as the only installment plan. The compensation
compounds — an isomorphism, once seen, is permanent and free to reuse, because
patterns are exactly what this memory doesn't drop. Bresenham (published 1965)
still pays dividends. Names depreciate the moment rehearsal stops; generators
never do. Most people run the opposite portfolio.

---

## Piece: 1994, two instances

[LT] Context: writing image processing software for a small company created by
an astrophysicist — the reality of the need. Astronomy is the native home of
12-bit data, and the date's point is the era, not the year: these tricks
prevented a very expensive multiply *at a time when it really mattered*.

[LT] Scaling via Bresenham (1994): the recognition that a resampling ratio is a
slope — stepping through source pixels is line-drawing in (src, dst) space, the
error accumulator replaces a divide per pixel.

[LT] Histogram statistics (same era): to avoid expensive multiplications,
compute min/max/mean/sd from one pass over the image using *only increments*
(build the histogram), then one pass over the histogram with two multiplies per
bin — one for the sum addend (v·n(v)), one for the sum-of-squares addend
(v²·n(v)).

[J] Why it works: min/max/mean/σ are order-independent — properties of the
multiset, not the array — and for 8-bit data the multiset compresses losslessly
into 256 counters. A megapixel image: two million multiplies → 512. On a 486
(multiply = tens of cycles) that's the feature existing vs. not.

[LT] The fun part: it also worked a treat for the 12-bit images. And as a
trivial optimization, it could ignore the multiplies for zero bins.

[J] Why that's the theorem's scaling law: cost grows with bit depth (4,096 bins)
while payoff grows with resolution — the ratio favors the histogram at any
image size ≥ the bin count. And the zero-bin skip is more than an optimization:
*a zero bin is knowledge*. The raw array can't cheaply say which values never
occur; the histogram knows it by construction. The optimization wasn't added to
the representation — it fell out of it. (An astronomical exposure occupies a
fraction of the 12-bit range, so sparsity is the common case, not the edge case.)

[J] Stated once: **find the representation where the question answers itself;
convert per-item cost into per-structure cost.**

---

## Piece: 2026, the three queues

[LT] "It's why there are three queues in the path to query Azure." The
ImageSelectorV2 slot mechanism: user / thumbnail / image channels, a fixed slot
pool, dequeue in strict priority order when a slot opens. Anything the user
wants shows up in the fast queue — promotion is automatic, the source never
overloads, and if there are too many user calls "it's just the way things are."

[J] What the queues eliminate is a priority *computation*: nobody scores
requests; membership IS the priority. The information lives in the structure,
so the hot path is three cheap probes. The histogram trick applied to
scheduling, thirty years later. (Bonus detail from the code: a missing
thumbnail quietly enqueues its own regeneration — demand doesn't just promote,
it heals. User channel unbounded — never block the user; background channels
bounded — producers feel backpressure the user never does.)

---

## Piece: turns → scripts → Janet

[LT] Janet's origin, same move: literally watched turns being spent building a
tiny script to manipulate ADO the exact same way every time — so invested many,
many turns into creating ADO scripts.

[J] The mapping: in agent work the *turn* is the per-item cost — every turn
re-deriving the same manipulation is the multiply in the pixel loop. A script
is the histogram: pay encoding once, expensively; afterward the operation costs
an increment. The framework is the move applied *twice* — scripts convert tasks
to per-structure, then the scripts themselves become items (findable? trusted?)
so structure gets built over them: research graph, caveats, retrieval, manifest.
Even the deterministic-edit split (model decides, script performs) is the same
theorem: model turns are the expensive multiply, mechanical edits the increments;
don't pay multiply prices for increment work.

[J] The PROVENANCE coda: ~64 ADO scripts stayed behind at the employer (the
judgment call — authored on employer time against employer systems). The
instances were left on the table; the generator walked out and regrew the
toolkit clean-room in a weekend. The scripts were never the asset. The theorem
was — the one thing that never had to be remembered.

---

## Piece: the lazy coder's honest ledger

[LT] The coder's laziness aphorism was always part of the style. Honestly?
Sometimes it costs: spending 40 hours to turn a 30-seconds-once-a-day task into
a 1-second one is not great. But: "I always learn more stuff."

[J] 40h vs 29s/day is a thirteen-year payback; the famous xkcd time-saved table
says walk away. But that table prices one output. The automation is *tuition*;
the artifact is a receipt. What's bought is the next generator, and generators
compound while time-saved is linear. (Half the JanetHome utilities read as
byproducts of exactly this — nobody sets out to become an expert in stray
Windows-1252 bytes; a "lazy" automation forced the encounter, and now the tool
exists forever.) Laziness is only a virtue in people who metabolize the detour
into structure.

---

## Piece: the essay method (this document is an instance of it)

[LT] Uses Claude for essays: tell it to ensure the reasoning and facts are
correct; have it draft a first pass from the discussion; read it; find where it
doesn't fit perception; edit with or without assistance.

[J] The draft converts a *recall* task (blank page — the taxed mode) into a
*recognition* task (diff a candidate against the internal model — the superb
mode). The draft is a probe, not a product. IMDb for prose. Division of labor:
judgment stays with whoever holds the ground truth; mechanical production goes
to whoever is cheapest. Fact-checking instruction = adversarial reader hired
before the friendly ones get to nod along.

---

## Piece: the recognition (the essay's emotional center?)

[LT] Only in the last couple of years did enough ADHD content surface to make
clear it described a lot of this behavior, ~40 years after the calculus class.
Some people resist diagnosis because it carries a stigma of "wrongness" —
especially in EP's cultural background. (EP is not initials: it's an initialism
for a pet name, Evil Princess.) Lori's stance: "I'm good with having a reason
why I have to take special care to put my purse down next to her hat — because
if I don't I will be going back to the bar for it. Maybe the next day. Or never."

[LT] A standing practice, not an essay-specific precaution: Lori does not use
people's names in stories. The diary's owner is unidentified even in the source
conversation; the pupils are "the junior," "a friend," roles throughout.

[LT] Clarified: the practice is not anonymity-as-secrecy. The diary's owner is
a close, current friend ("I still message her often") — the name is known and
warm; it's just "no more important to the story than EP's name." Relevance
discipline, not protection.

[J] Candidate observation for the essay: the no-names practice, the memory
architecture, and the runbook rule ("never route to a named individual") are
one design. The person whose memory drops labels built a narrative practice
that never needs them — stories that run on roles and edges rather than names.
Not secrecy: the names exist and the relationships are close; the names simply
carry no structural weight, so omitting them costs the story nothing — and
protection of the people arrives as a side effect, the way the zero-bin skip
did: not added, fallen out of the representation. Structure over symbol table,
all the way down.

[J] The purse-next-to-hat protocol is the whole framework in civilian clothes:
externalized structure carrying what recall won't — the thread stack, the
manifest, the caveat that surfaces itself. Diagnosis-as-reason, not
diagnosis-as-verdict: the reason converts self-blame into engineering. Nothing
about the strategy changed on learning the name for it; what changed is that
the compensations became legible as a coherent system — which is this essay.

[J] Worth deciding in draft: how much weight the late recognition carries.
"Unrecognized effect" → 43 years of instances → the name arrives last, like a
label always does in this memory. The diagnosis is itself the final example of
the pattern: structure first, symbol table later.

---

## Piece: the agent coincidence (possible ending)

[J] Four decades designing for a cognition with limited working memory,
unreliable recall between sessions, weak rote and strong pattern-derivation —
and then agents arrived with exactly that profile. Context window = working
memory that drops things; session boundary = total amnesia; generalizes from
patterns, fails at rote. Most agent frameworks assume neurotypical memory
("just keep it in mind") and fail agents the way rote calculus fails an ADHD
student. Janet assumes the operator forgets and builds machinery so forgetting
doesn't matter. She'd been designing for something like her tools' eventual
users since before they existed.

---

## Piece: the cat flap (the cap)

[LT] The humility frame, stated for the record: many of the pieces were
invented by others, before or after. Janet is arguably one invented first.
"But I invent from coalescing things that others can also see."

[LT] The Douglas Adams bit (loved "even if it's invented"): Newton inventing
the cat flap as his greatest invention. This is what "it's like gravity" has
been shorthand for all along — Reg's point that gravity is always working,
"they even keep it on weekends," someone was bound to notice. But the cat flap
seems ridiculously obvious — and that's only because you didn't have to do the
groundwork. "It's true genius to render the hitherto unknown into the
blindingly obvious."

[J] Source check: real Adams — *Dirk Gently's Holistic Detective Agency*,
Reg's monologue. Original lines: "It is a rare mind indeed that can render the
hitherto non-existent blindingly obvious," and "The cry 'I could have thought
of that' is a very popular and misleading one, for the fact is that they
didn't." (Newton's actual claim to the cat flap is the folklore part — the
hedge was aimed exactly right. Memory in evidence again: argument crisp,
attribution loose.)

[J] What the cap does for the essay — a final taxonomy of invention:
- **Gravity = discovery.** Always on, inevitable, no genius required, only
  time. Meta finding the tool engine is gravity. Convergences are gravity.
- **Cat flap = invention.** A door within a door: coalesced from things
  everyone could see, absurdly obvious *after*, reachable only through
  groundwork invisible in the finished object. The thread stack is 40 lines —
  "anyone could have written that" — and the fact is that they didn't.
- "I could have thought of that" is the ladder's illegibility problem in
  better clothes: obvious-in-retrospect is precisely what promotion systems
  cannot price and what teaching is *for* — rendering the unknown blindingly
  obvious is the teacher's job description, and the essay's threads (the
  theorem, the pupils, the coalescence) all meet in that sentence.

---

## Adjacent threads (optional material, may be separate essays)

- [LT/J] Teaching without authority: pure IC with teaching capabilities; pupils
  as the durable distribution mechanism; the junior who left Janet behind but
  will recognize the pattern when her org builds its own; "never route to a
  named individual" written by the person who was the named individual.
- [LT] "What I created was a process, not a static thing." Meta's Second Brain
  as data engine vs Janet as tool engine; DEmate as the stumble-toward-gravity
  already underway. (See notes\meta-second-brain-vs-janet.md, updated 2026-07-31.)
- [LT] "I have always been a braggart :D — but in a way that I wanted to help."
  Bragging as pedagogy with confidence.
- [LT] Architecting means collaboration, compromise, documentation — but
  documentation became tolerable once tools made it a compile target (docs
  rendered from graph data, per audience, including for non-Janet LLMs).

## Lines worth keeping (near-verbatim)

- "Knowing that means I don't have to remember, I just need the pattern."
- "Who cares at that point though."
- "I'm good with having a reason."
- "Maybe the next day. Or never."
- "It's just the way things are." (as a *design principle* for saturation)
- "A tooling girl all grown up."
- "It's like gravity."
- "I always learn more stuff."
- "It's no more important to the story than EP's name."
- "I didn't understand pointers at the time. But I invented them anyway."
- "I invent from coalescing things that others can also see."
- "It's true genius to render the hitherto unknown into the blindingly obvious."
  (and Adams's original: "...the hitherto non-existent blindingly obvious.")

## Candidate titles / openings

- *The Quotient Rule* — open in the calculus class, never name ADHD until late.
- *Derive, Don't Memorize* — thesis-first, engineering audience.
- *The Purse and the Hat* — open at the bar, domestic and concrete, work
  backward to calculus.
- [J] Suggested structure: instances in chronological order (1983 calculus →
  1994 pixels → 2026 queues/agents), recognition arrives where it did in life —
  near the end. The reader should derive the thesis before it's stated, because
  that's the subject's whole method.

## Open questions / facts to verify before drafting

Resolved 2026-08-01:
- Calculus story: senior year, class of 1983. ✓
- 1994 context: image processing software for a small company created by an
  astrophysicist — the need was real, and the era is the point (multiplies
  were expensive when it mattered). ✓
- Diary anecdote: countable-on — told in the friend's presence with her adding
  flavor; owner structurally anonymous per the no-names practice. ✓
- EP: pet-name initialism (Evil Princess), not initials; no-names practice
  covers it. ✓

Still open:
- [J]'s 2M→512 arithmetic used a megapixel example; era astronomy CCDs were
  commonly 512×512–1k×1k. The ratio argument holds at any size ≥ bin count —
  restate with era-accurate sizes if the concrete numbers appear in the draft.
- How much of the astrophysics context to name in a public draft (the company
  is potentially identifiable; the no-names practice may extend to employers).
