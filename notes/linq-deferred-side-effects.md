# Deferred LINQ over a side-effecting projection

`Select` is lazy, so a sequence built from a projection that *does something* re-does it on
every enumeration — and `Count()` is an enumeration. Found in the wild 2026-08-08 in
ImageSelectorV2, where it silently doubled background work on every call.

---

## The shape

```csharp
IEnumerable<Task<byte[]?>> tasks = items.Select(i => dispatcher.EnqueueAsync(i, priority));

try
{
    await Task.WhenAll(tasks);          // enumeration 1 — creates and starts N tasks
}
finally
{
    Console.WriteLine($"count {tasks.Count()}");   // enumeration 2 — starts N MORE tasks
}
```

The selector's side effect is *starting work*. `Task.WhenAll` enumerates once and awaits
that set. The `.Count()` in the `finally` enumerates again, calling `EnqueueAsync` a second
time for every element. The second set is never awaited and its faults are never observed.
Net effect: double the queue load, half of it invisible, and the log line reports a count
of work that was created *by the act of counting it*.

Fix is one word — materialize at the point of creation:

```csharp
List<Task<byte[]?>> tasks = [.. items.Select(i => dispatcher.EnqueueAsync(i, priority))];
…
tasks.Count      // property on a materialized list, not a LINQ re-enumeration
```

## Why `Count()` doesn't save you

The intuition "`Count()` is cheap, it just reads a length" is wrong here, and wrong for a
specific and deliberate reason.

`Enumerable.Count()` looks for a fast path (`ICollection<T>.Count`, or the internal
`IIListProvider<T>.GetCount`). A `Select` iterator over a list *does* implement that
interface — but its `GetCount(onlyIfCheap: false)` **runs the selector on every element
anyway**. The BCL does this on purpose: skipping the projection would skip its side effects
and any exceptions it throws, so a "cheap" count would change observable behaviour. The
runtime chooses fidelity over speed.

So the optimization you were counting on is the exact thing that's been deliberately
disabled. `Count()` on a projection is a full re-run, by design.

## The tell

**An `IEnumerable<Task<T>>` local is almost always a latent bug.** A task is already-running
work; a lazy sequence of them is a promise to run that work again on demand. The two
concepts are in direct opposition. Whenever the element type is `Task`, `Task<T>`, or
`ValueTask<T>`, the sequence should be materialized the moment it is created.

Wider version of the rule: **if a projection has side effects, materialize at the point of
creation, not at the point of first use.** Deferred execution is only safe over pure
projections. The danger is that the code reads as correct — it's one `await` and one
`.Count()`, both idiomatic — and nothing fails. The symptom is load, not an exception, so it
survives review and testing and shows up later as unexplained throughput.

Other enumerations that bite the same way: `Any()`, `Last()`, `ElementAt()`, a second
`foreach`, and passing the same sequence to two different consumers.

## Detection

Grep-level heuristic, good enough to be worth running:

- a local or field typed `IEnumerable<Task` / `IEnumerable<ValueTask`
- `.Select(` whose lambda body calls something ending `Async(`, assigned to an
  `IEnumerable`/`var` and not immediately `ToList`/`ToArray`/`[.. ]`

A semantic check is stronger and is what RazorGraph could answer directly: does the
`Select` lambda reach a method with a write effect, and is the resulting sequence
enumerated more than once? That's a reachability question over the call graph plus a
use-count on the local — both things the graph already knows.
