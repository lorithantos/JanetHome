# Insertion-Merge Hybrid Sort (Shower Thought)

Tags: not-actually-needed, algorithms, theoretical
Date: 2026-04-24

## Premise

Modified Timsort: replace natural run detection with recursive center-element splitting. Split until insertion-sort size (~1k), insertion sort each block, then use insertion sort (not merge) on the way back up.

## Key Insight

Insertion sort is O(n + inversions). Two adjacent sorted blocks concatenated are nearly sorted -- inversions cluster at the seam. The interior of each block is already settled. So the backout insertion sorts scan through mostly-ordered data with a thin band of disorder at each boundary.

## Algorithm

1. Recursively split array at center index until blocks are ~1024 elements
2. Insertion sort each block (cache-hot, ~1M comparisons worst case, fast in practice)
3. Sort blocks by first element -- O((n/1024) log(n/1024)), trivially cheap
4. Binary insertion sort across the full array with shrinking search window (see below)

## Block Merge Refinement: Binary Insertion with Shrinking Window

At step 4, we have a stronger invariant than "nearly sorted." We know *exactly* where the disorder is: the seam between adjacent blocks. Every element that's out of place crossed a block boundary. The interior of each block is proven sorted.

This means:
- **Binary insertion, not linear scan.** Standard insertion sort scans backwards O(k) to find the insertion point. Binary search finds it in O(log k). Since k is bounded by the overlap zone at the seam (not the full block), it's cheaper still.
- **Monotonically shrinking search window.** Both blocks are sorted, so no second-block element can land earlier than the previous insertion point. Each insertion narrows the binary search range for the next one. The window only shrinks, never grows.
- **No element in the interior moves.** Only elements near the seam participate. The scan skips the settled interior of each block entirely.

The result is in-place merging with binary search on a shrinking window -- structurally similar to mergesort's merge step but without the auxiliary buffer. The O(n^2) worst case gets pushed further into pathological territory (requires adversarial construction where entire blocks interleave at every element).

## Run-Aware Seam Merge (Apr 26 refinement)

Replaces element-by-element binary insertion at the seam with bulk run detection and copy. Three pointers, sequential scan, cache-hot.

**Merge procedure for two adjacent sorted blocks:**

1. **Disjoint check**: if `left.max <= right.min`, they're already ordered. One comparison, done.
2. **Overlap start**: binary search for where the blocks begin to overlap. Everything before the overlap point is settled -- don't touch it.
3. **Three-pointer scan in the overlap zone:**
   - Pointer A walks left block, pointer B walks right block (both advancing forward, sequential)
   - Pointer C marks the start of the current swap region
   - Scan forward until a discontinuity (where the other block's element should interpose)
   - At discontinuity: bulk copy the run from C to current position (memcpy-style, not individual shifts)
   - Advance C to the new swap start, continue scanning

**Why this matters:**
- Even in the overlap zone, most elements form runs. The scan is sequential (cache line friendly), the movement is bulk copy (not element-by-element shifting)
- The number of operations scales with the number of *discontinuities at the seam*, not the number of elements in the overlap
- For real-world data where blocks have significant non-overlapping regions, most data never moves

**Impact on worst case:**
The original adversarial defeater (interleaving at every element across all merge levels) is much less effective now. Run detection means the adversary needs to prevent *any* sequential runs at every seam, which is harder to construct and maintain across merge levels. Each level's merging reduces the number of discontinuities available to the next level.

**Cursor-based k-way merge for guaranteed O(n log n):**
The priority queue is NOT a block-level sort. It is a min-heap of **cursors** into the k blocks, each keyed by its current value. The algorithm:

1. Place the head element of each block on the heap (k entries, one per block)
2. Pop the minimum -- this is the next element in globally sorted order
3. Advance that block's read cursor; push the new head onto the heap
4. Repeat until all cursors are exhausted

The seam merge and the priority queue **compose, not alternate**. The priority queue is the navigator ("which block has the next value?"). The seam merge is the mover ("place it efficiently"). When the priority queue pops a cursor and the seam merge finds a run of consecutive elements from the same block, it bulk-copies the run and advances the cursor past it. In the adversarial case (perfect interleave), runs are length 1 and each element is a single placement. In the common case, runs are long and the priority queue is barely consulted.

**Complexity:**
- Comparisons: O(n log k) where k = n/blocksize. For 1M elements with 1K blocks, log k ~ 10.
- Data movement: O(n) total, O(1) per element -- each element is placed in its final position exactly once via the output buffer. No shifting/rotation of unread data.
- Space: O(k) for the heap (k pointers/cursors) + O(blocksize) for the output buffer. For 1M elements with 1K blocks: 8KB heap + 4KB buffer. Not O(1) but negligible for any real dataset -- effectively zero relative to n.

**Output buffer for in-place placement (Apr 26 refinement):**
The heap produces elements in globally sorted order, but writing directly into the source array would overwrite unread data. Solution: a single output buffer of size blocksize (~1024 elements). The merge writes to the buffer; when the buffer fills (one block's worth), it flushes to the next free region of the output array. Source blocks that have been fully consumed become free regions.

This resolves the in-place placement problem cleanly:
- The buffer is O(blocksize), not O(n). For 1M elements, it's 4KB -- rounding error.
- Each element is read once from its source block and written once to the buffer, then once from the buffer to its final position. Total data movement: 2n, still O(n).
- No rotation, no shifting, no overwriting unread data.
- The tradeoff: this is not strictly O(1) auxiliary space. It's O(blocksize). For any practical dataset, blocksize is a fixed constant (1024), so this is effectively O(1). Theoretically it's O(n/k) = O(blocksize), which is a parameter, not a function of n.

**Cache behavior:**
- Adversarial case: 1K active cursors at different memory locations. Exceeds L1, fits in L2. Each block is accessed sequentially (cursor advances forward), so prefetching helps. Pressure drops as blocks drain.
- Common case: most blocks are disjoint or nearly disjoint. Priority queue quickly exhausts small blocks and concentrates on a few active ones. Working set shrinks as merge progresses.

## Hierarchical Variant: Bottom-Up Pair Merging

Instead of one final insertion sort across the full array, merge bottom-up in pairs. Each level:

1. **Scan first**: if `left.last <= right.first`, the pair is already merged -- skip. Costs one comparison.
2. **Merge the seam**: run-aware three-pointer merge on the overlap zone. Bulk copy, not element-by-element.
3. **Result**: a sorted run twice the size. Next level sees cleaner input.

Each level doubles the run length, touches n elements, log(n/blocksize) levels. O(n log n) structure -- same as mergesort, zero buffer.

The scan-before-merge is the big practical win: after the first few levels, most adjacent pairs are already in order (especially on real-world data with natural runs). Those pairs cost one comparison to skip. Merges that DO happen operate on a single seam with run-aware bulk copy.

This is structurally bottom-up mergesort with in-place run-aware merging replacing the buffered merge. Hierarchical disorder reduction without ever allocating.

## Code Complexity

| Component | Lines | Notes |
|-----------|-------|-------|
| Binary insertion sort | ~15 | Standard, used for initial blocks |
| In-place binary merge | ~20 | Binary search + shrinking window + shift-insert at seam |
| Bottom-up driver | ~15 | Double run size each level, scan-skip, call merge on pairs |
| **Total** | **~50** | |

For comparison, Timsort's ~300 lines come from:
- Natural run detection + reversal (~30)
- Min-run calculation (~15)
- Galloping merge with mode switching (~80)
- Merge stack invariant enforcement (~50)
- Temporary buffer management (~40)
- Two separate merge routines (merge-lo, merge-hi) (~80)

This variant skips all of that. The tradeoff: Timsort's galloping is faster on long sorted runs, and its worst case is O(n log n) guaranteed. This variant is 6x less code, O(1) space, but O(n^2) theoretical worst case on adversarial input (requires interleaving maintained at every merge level).

## Performance Characteristics

- **Time:** O(n log n) practical on non-adversarial data. O(n^2) theoretical worst case, but requires pathological construction (entire blocks out of order).
- **Space:** O(1) auxiliary (plus O(log n) recursion stack). No merge buffer. This is the main win over Timsort.
- **Stability:** Stable (insertion sort preserves order).
- **Cache:** Extremely friendly. All operations are local. No buffer allocation/copy.
- **Disk:** Kinder than Timsort on extremely large arrays -- no auxiliary buffer means less I/O pressure when data spills to disk. Sequential scan pattern with local writes.

## Comparison to Timsort

| Aspect | Timsort | This variant |
|--------|---------|-------------|
| Random data | O(n log n) | O(n log n) practical |
| Nearly sorted | O(n) | O(n) (scan-skip + adaptive insertion) |
| Space | O(n) | O(1) |
| Seam merge | Galloping + buffer copy | Run-aware three-pointer, bulk copy, in-place |
| Merge strategy | Flat stack with invariant | Bottom-up pairs, scan-skip, priority queue fallback |
| Code complexity | ~300 lines | ~50 lines |
| Disk-friendly | Needs merge buffer | Sequential, in-place |
| Worst case guarantee | O(n log n) | O(n log n) with priority queue fallback; O(n^2) theoretical without (adversarial interleave, but run detection makes this much harder to construct) |

## Why It Works

The block-first-element sort is the key move. It gets the macro order right cheaply (Bresenham pattern: downsample the problem, solve the small version, let structure propagate). Then insertion sort's adaptive behavior handles the micro disorder at block boundaries for nearly free.

## Why It Doesn't Matter

Standard library sorts are fine. This is a thought experiment about the design space between quicksort, mergesort, and insertion sort -- specifically, that insertion sort's adaptivity can substitute for explicit merging when blocks are well-ordered and large enough.

## Origin

Friday night shower thought + Janet refinement session, April 24 2026.