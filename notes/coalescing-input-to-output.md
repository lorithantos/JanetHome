# Coalescing input changes into output work

A pattern for the boundary between **input-side changes** and **output-side
representations**. Distilled 2026-08-07 from a WPF chart-rebuild problem, but the machine is
not WPF-specific and has at least two independent instances (see *Second instance*, below).

Status: **pattern, not code.** No implementation exists yet. The first is planned in
`D:\Repos\RetirementCore\DESIGN-COALESCING.md`, which carries the application-specific half.

---

## The problem shape

Any system coupling changing input to derived output hits the same mismatch:

- **Input** arrives at whatever rate the world produces it — typing, messages, file-watcher
  events, sensor samples, parallel workers reporting progress.
- **Output** is expensive to produce and only useful when something observes it — charts,
  layouts, reports, indexes, network calls.
- **Coupling them directly** does output work once per input event, which is wrong by orders
  of magnitude.

The tell: a notification handler that rebuilds everything, subscribed to a source that
raises many notifications per logical operation. Symptoms are excess CPU, allocation churn,
and — where the output owns native or pooled resources — lifetime bugs that look like
random crashes.

**Two independent axes fix it, and both are needed:**

- **Time** — collapse many input events into one output pass, with a stated latency bound.
- **Demand** — produce output only for what is actually being observed.

Time alone still rebuilds invisible things. Demand alone still rebuilds the visible thing
once per keystroke.

---

## The machine

Three roles, deliberately separated. Getting these boundaries wrong is the main failure
mode.

| Role | Answers | Lives with |
|---|---|---|
| **Key** | "May these two postings be combined *at all*?" | the producer |
| **Value** | "What was this posting's payload?" | — |
| **Destination** | "Given everything that accumulated, what do I do?" | the consumer |

- The key **partitions**. It does not merge.
- The queue **buffers**. It does not merge either.
- The destination **collapses**. Seven postings of "I updated x" become one "propagate x",
  because the destination is what knows that.

### Why the fold is not on the key

Binding a merge function to the key destroys information at ingress and imposes one
collapse on every consumer. Different destinations legitimately want different folds — one
wants only "did anything change", another the latest value, another a count across the
batch. A merge on the key is a constraint disguised as an invariant.

Buffering also **removes any associativity requirement**. An incremental pairwise merge
needs `f(f(a,b),c) = f(a,f(b,c))`, or the moment the window happens to close changes the
answer. A destination folding a complete list has no such obligation, so non-associative
collapses — average, median, first-and-last — become legal.

Cost: buffering holds `arrival rate × window` items instead of one, so the latency knob
bounds memory too. Where a key is genuinely high-volume *and* its payload large, an opt-in
ingress pre-merge can be added for that key alone; it must then be associative.

---

## The loop is the state machine

```
consume():                                 # one long-running loop
    while await queue.notEmpty():          # zero cost while idle
        await delay(FrameLength)           # let the burst accumulate
        batch = drainAll()                 # greedy: read until empty
        await execute(batch)               # completes before the next window opens
```

**One knob.** The first posting into an empty queue opens a window; one `FrameLength`
later everything drains. That is the whole algorithm.

**"A window is open" is not a flag** — it is the loop sitting at the `delay`. This is the
single most important simplification. Earlier drafts carried an `armed` bit, an atomic
exchange, and a drain/post race that had to be argued benign. All of it dissolves when the
state lives in the loop's position instead of in a variable.

**Drains never overlap.** `await execute(batch)` completes before returning to
`notEmpty()`, so delivery is serialized by construction, not by convention. A timer firing
while the previous delivery runs has no such guarantee — it re-enters. A slow consumer here
simply widens the effective window; the queue absorbs the difference. Backpressure for
free.

**Greedy drain, not snapshot.** `drainAll` reads until the queue reports empty, so an item
enqueued *while the drain runs* still catches the current frame. A snapshot drain — take
the contents, then process — defers everything arriving during processing by a full window.
Same amount of code, materially different behavior, and easy to lose to a well-meaning
refactor. Pin it with a test.

That leaves two regimes: under pressure the next frame is dirty by construction
(self-limiting: cadence settles at `window + execute`), and otherwise being deferred takes
bad luck — arriving after the drain observed empty.

**No maximum-delay cap is needed.** A cap only ever bounds *extension* (debounce, where each
posting pushes the deadline later and continuous input defers forever). A fixed window never
extends, so there is nothing to cap. Sustained input drains every window: steady, not
frozen. Dropping extension took worst-case latency from two windows to one and deleted all
the first/last-posting bookkeeping.

**Wait for the first element rather than run a heartbeat.** A timer ticking forever is
simpler still but burns wakeups while idle — which is most of the time.

**Termination.** A read-until-empty loop is unbounded in principle; a producer outpacing the
dequeue would spin it. It does not bite when a dequeue is orders of magnitude cheaper than
the work a producer does between postings. If that ever changed, a per-drain item bound
trades the straggler property for hard termination.

---

## Rate decoupling (the property that matters most)

Delivery happens **at most once per window, by construction**, whatever producers do. Input
rate changes only batch size — memory — never cadence.

The consequence: producer speed cannot overwhelm this design. A 500 wpm typist emits a
character every ~24 ms; at a 100 ms window that is four postings collapsing to one delivery,
asking the consumer for ten passes a second rather than forty-two. The only way to fall
behind is a single delivery costing more than the window — a slow *consumer*, never a fast
producer.

And even that resolves: input bursts are bounded because people stop. Backlog is capped at
`burst duration × rate`, and the greedy drain clears it when the burst ends. The system must
stay bounded during a burst and converge after it, not keep pace with the peak.

---

## Demand: laziness

Routing answers *who registered*; demand answers *who is being looked at*. Every registered
destination is invoked per settle even when nothing observes its output.

In a retained-mode UI this is nearly free: make each output a lazy getter over a cell that
holds a dirty flag. Invalidation raises a change notification, which is a no-op when no live
binding exists; the getter rebuilds only when something actually reads it. In WPF, template
materialization means an off-screen page then computes nothing at all, with no activation
plumbing to maintain.

**Compose cells per output; do not hoist a base class.** A base class carries one dirty flag
and one `rebuild()`, which models *"one signal → rebuild this whole consumer"* — usually the
very thing being fixed, relocated rather than solved. Real consumers have several outputs
invalidated by *different* keys. One cell per bindable output, each registered for exactly
the keys that dirty it, gives the granularity the coarse version flattens. (Inheritance is
not the obstacle — semantics are.)

Contract on a lazy getter: idempotent, and cheap when clean.

---

## Threading

- **Concurrent ingress, single-threaded egress.** Posting happens from any thread; delivery
  happens on one.
- In a UI framework, start the consumer loop on the UI thread and never
  `ConfigureAwait(false)`. Every continuation then resumes there and *no explicit marshalling
  exists anywhere*. The invariant is enforced by where the loop was started — which is worth
  a comment at that call site, because "tidying up" by adding `ConfigureAwait(false)` would
  silently move output mutation onto a pool thread.
- Posting reduces to one lock-free enqueue with no scheduling work, which matters when it is
  called from worker threads.
- Fan out to destinations at **drain**, not at post: keeps the hot path minimal, puts
  bookkeeping on the already-single-threaded side, and stops registration racing the buffers.

---

## Derived state as explicit status

Consumers of this pattern usually also need to say *what state the derived thing is in*.
Scattering that across booleans (`isStale`, `isRunning`) is the same mistake as prose errors:

```
Never      not computed yet         Valid      current
Stale      previous value, invalid  Computing  recomputing, with progress
Failed     carries the error
```

Synchronous values are the degenerate case — they move `Stale → Valid` inside the getter and
never occupy `Computing`. Async recomputation needs a **generation counter** captured at
start and compared at publish, which generalizes the common `ReferenceEquals(input, current)`
supersession check and works for inputs that are not reference-comparable.

Making failure a state rather than a modal dialog is usually an improvement, and a behavior
change worth taking deliberately.

---

## Second instance, and the two variants

The pattern is not fitted to one case: a batching cache for an endpoint that accepted many
entries per call but **could not mix locales** (`en-ca` combined with neither `en-us` nor
`fr-ca`) arrived independently at the same machine. Locale was the partition — what was
*allowed* to combine — while how the batch was folded stayed the caller's business. That is
where the key/destination split came from.

| | Fire-and-forget | Request/response (batching loader) |
|---|---|---|
| Producer | a change happened | a component needs data |
| Partition | source + member | what may share one call |
| Destination fold | often ignored — rebuild from current state | dedupe the ids, issue one call |
| After delivery | nothing | route results back to each waiter |
| Window driver | wall-clock | often "end of tick" |
| Failure | one cell's state | fan out to every waiter in the batch |

The request/response form needs correlation (request → waiter) and failure fan-out, neither
of which the fire-and-forget form exercises. Same machine, genuine extension — do not assume
one implementation serves both. The well-known dataloader pattern is this machine with an
end-of-tick window.

---

## Invariants worth testing

1. N postings in one window produce exactly one delivery carrying N buffered values.
2. Postings under different keys never combine.
3. A posting made *during* the drain appears in that batch, not the next (greedy drain).
4. A posting made during delivery lands in the next window and is never lost.
5. Deliveries never overlap; a slow destination stretches cadence rather than running twice.
6. Idle schedules nothing.
7. An async recompute whose generation advanced mid-flight publishes nothing.
8. Conflation never loses the final value of a monotonic counter.
9. A destination that mutates its source during delivery re-schedules rather than recursing.
10. One logical operation produces one output pass, not one per property changed.

Test with an **injected clock advanced explicitly** — never sleep. Time-based code is
notoriously flaky, and sleeping tests are a regression in kind.

---

## Why this kept getting simpler

Worth recording as method, not just result. Every revision **removed** a mechanism:

- an explicit batch scope → dissolved into the deferred drain
- a maximum-delay cap and extension bookkeeping → dissolved into the fixed window
- an `armed` flag and its race → dissolved into the consumer loop
- a merge function on the key → moved to the destination, deleting an associativity rule
- a shared base class → replaced by one cell per output, gaining granularity

The consistent direction was **state moving out of explicit bookkeeping and into structure**.
When a design needs a flag to describe where it is, ask whether some structure could *be*
there instead.
