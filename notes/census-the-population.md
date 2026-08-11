# Census the population; the anomaly announces itself

When one instance will not explain itself, stop interrogating it and
classify every instance you can reach. Over-representation is a signature
you COUNT rather than deduce -- no theory of the bug required, and the
answer arrives as a name rather than a hypothesis.

## The rule

A single object, crash, or record is a sample of size one, and reading it
harder does not add information. A census does: classify the whole
population by some cheap property, sort by frequency, and the outlier is
the finding. This works even when you have no idea what you are looking
for, which is exactly when instance-level inspection is worst.

Two corollaries that matter as much as the rule:

- **The census needs a classifier that is always present.** Whatever
  marker you count must exist on every member of the population, not on
  the well-behaved ones. Pick the byproduct nobody can turn off over the
  metadata that is usually there (see
  `note.debugger-displays-are-claims`).
- **Absence is a finding.** A count of zero where the domain says there
  should be thousands is the loudest possible signal, and it is invisible
  to instance inspection -- you cannot inspect an object that was never
  created.

## The story (WinINet leak, Watson era)

A buffer overflow in WinINet started "eating memory" past a certain
point. Instance inspection said only that memory was growing. Lori turned
her per-object type-recovery extension (see the sibling note) around and
ran it across the entire heap: resolve each candidate vfptr to its
vftable symbol, tally by type. The same structure appeared thousands of
times over. The leak went from "memory is growing" to "forty thousand of
THESE" -- and a type name names an owner.

Why the classifier existed at all: a native C++ heap has no universal
object header. Allocator blocks carry size and flags, never type. The
vfptr is the only self-describing marker in the heap, and only for
polymorphic types -- so a vtable-symbol census is not *a* way to classify
a native heap, it is essentially *the* way. Managed developers were later
handed this as SOS `!dumpheap -stat`; native C++ never got an equivalent,
which is why it had to be built by hand and why the payoff was so
lopsided.

## Modern occurrences

- **RazorGraph nodeCounts, 2026-08-08.** The census of a saved solution
  graph reported Method 1317, Class 172, ViewModel 16 -- and Property 0.
  Instance queries had been silently returning empty edge lists that read
  as "nothing references this" when they meant "not extracted." The
  population count exposed in one line what per-node inspection had been
  hiding for weeks. Absence as finding, exactly.
- **verifyheap, 2026-08-10.** 288,741 objects enumerated, zero errors --
  a whole-population statement that overturned the plausible instance
  reading (a corrupted-looking stack implies a corrupted heap) and
  redirected a two-year crash hunt.
- **Watson bucketing itself** is the pattern at organization scale: not
  this crash, the distribution of crashes.

Related: `note.debugger-displays-are-claims` (derive from the artifact
that is always present), and the graph-first operating rule -- ask the
model for the population before reading any single file.
