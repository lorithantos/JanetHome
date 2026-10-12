# One consumer, not a lock

**Rule.** In-process coordination is a single consumer draining a concurrent queue. No
`lock`, no flag that decides who does the work, no semaphore election. When several threads
need the same thing done, they put requests on a queue and exactly one loop does the work.

Decided 2026-10-12, by Lori: *"We do not like locks -- they are easy to get wrong and waste
tokens finding out why they are."*

## Why

A lock is correct only if every path that touches the guarded state takes it, in the right
order, for the right span. That property lives in no single place you can read. When it is
wrong the failure is intermittent, the symptom shows up somewhere else, and the diagnosis
is expensive in exactly the resource that runs out.

The instance that settled it is `WriteQueue.Lane` (src\Janet.Core\WriteQueue.cs), which
chose its drainer under a lock:

```csharp
lock (_gate)
{
    _pending.Enqueue(item);
    mine = !_draining;
    _draining = mine;   // a submitter that loses CLEARS the flag
}
```

It looks careful: everything is inside the lock, and the clear in `Drain` is commented as
being under "the same lock that enqueues". But a second submitter that finds a drain running
sets `_draining` back to false, so the third becomes a second drainer, and the drainers then
fight over the cross-process file lock and back off. What it cost:

- One test in 590 failed once, on 2026-10-09, under a bare `dotnet test -v q` that printed
  no name. Six reruns passed.
- Tracking it down took an agent 36 minutes and 104 tool calls. It reproduced the failure
  (17 of 20 runs with the two contending test classes alone) and found that a test had set a
  process-wide timeout to 200ms.
- The defect underneath was the two lines above. A scratch A/B with 16 submitters failed 98
  of 100 runs as written and 0 of 100 with `_draining = true`.

The fix that removes the CLASS of bug is not the one-character correction. It is having no
election at all.

## The shape

One consumer thread draining a `ConcurrentQueue<T>`. The reference implementation is
`WriteQueue.Lane` (src\Janet.Core\WriteQueue.cs): one lane per file, its consumer a lazily
started background thread, a `TaskCompletionSource` per item for the caller's answer.

```csharp
BlockingCollection<WorkItem> queue = new(new ConcurrentQueue<WorkItem>());

// The one consumer, on its own background thread:
while (true)
{
    List<WorkItem> batch = [queue.Take()];               // blocks while idle
    while (queue.TryTake(out WorkItem? item))            // greedy: take all that is waiting
    {
        batch.Add(item);
    }

    Apply(batch);
}
```

Producers only ever add to the queue. There is one consumer, so "who drains" is not a
question anyone answers at runtime. Draining greedily makes the batch whatever arrived while
the last one was being applied: coalescing for free, and no armed flag. This is the loop
`pattern.change-coalescing` describes for UI change streams.

`BlockingCollection` is the `ConcurrentQueue` plus the one thing a bare queue lacks: a way
for the consumer to sleep while it is empty. A bare `ConcurrentQueue` polled in a loop
either spins or needs a wake-up signal, and a hand-built signal is the thing this rule
exists to delete. Let the library own the wait.

## Which queue: match it to the consumer

- **A consumer on a dedicated synchronous thread** -- the default here, because Janet's
  callers block on their result -- drains a `BlockingCollection<T>` over a
  `ConcurrentQueue<T>`, as above. `Take()` blocks until work arrives, `TryTake()` until empty
  forms the batch, and `CompleteAdding()` closes it.
- **An async consumer** (a hosted service, a loop that awaits, callers async end to end)
  reads a `Channel<T>`, which is the same queue with an awaitable wait. ImageSelectorV2 uses
  this form for uploads (`UploadFinishService.ConsumeLoopAsync`,
  ImageSelectionTools\ImageSystem\UploadFinishService.cs), adding a K-slot worker pool that
  a strictly serial writer does not need.

```csharp
Channel<WorkItem> channel = Channel.CreateUnbounded<WorkItem>(
    new UnboundedChannelOptions { SingleReader = true });

// The one consumer, as an async loop:
while (await channel.Reader.WaitToReadAsync(token))
{
    List<WorkItem> batch = [];
    while (channel.Reader.TryRead(out WorkItem? item))   // greedy: take all that is waiting
    {
        batch.Add(item);
    }

    Apply(batch);
}
```

Mixing the two is where it goes wrong, and WriteQueue did both wrong ways on 2026-10-12.
First, a dedicated thread blocking on `Channel.Reader.WaitToReadAsync().AsTask()` still
needs a pool thread now and then. If a write completes the wait before the task is attached,
the wake-up is queued to the pool, so with the pool full of blocked submitters the consumer
never woke: 3 runs of 3, and 2 of 4 even with `AllowSynchronousContinuations`. The fix that
followed, an `AutoResetEvent` set by each submitter after its write, was correct but only
because of that ordering: set it before the write and an item is stranded (3 of 3 under
mutation). That is a hand-rolled signal again. `BlockingCollection` replaced both.
`WriteQueuePoolTests.BlockedSubmittersOnACappedPoolStillLand` pins the hang.

A `TaskCompletionSource` is not a substitute for the consumer's wait. It is one-shot, so the
consumer would have to swap in a fresh one after every wake-up and re-check the queue, or an
item arriving between the drain and the swap sets the old one and is stranded. It belongs on
the CALLER's side, below.

## When the caller needs an answer

ImageSelector's producers fire and forget, and a durable pending log replays work that was
accepted but never finished. A caller that must not be told "done" before the work lands
gets a `TaskCompletionSource<T>` in its work item instead, created with
`TaskCreationOptions.RunContinuationsAsynchronously`, so completing it does not run the
caller's continuation on the consumer's thread. The consumer completes or faults it after
`Apply`. Then no log is needed, because nobody was acknowledged early.

## Traps, each one met or reasoned through here

- **Synchronous callers starve a pool consumer.** If callers block on their result and the
  consumer needs a thread-pool thread, enough blocked callers stall it. Either go async end
  to end, or run the consumer on its own thread (`TaskCreationOptions.LongRunning`, or a
  `Thread`), and give that thread a blocking queue to wait on, not an async one. See
  "Which queue" above.
- **A foreground consumer keeps a CLI alive.** A short-lived process has no host to stop the
  loop, so the consumer thread must be a background thread.
- **Lazy start races.** With no host `StartAsync`, the consumer starts the first time
  something uses the queue. `ConcurrentDictionary.GetOrAdd` can run its factory more than
  once, so start the consumer only for the instance that won (a `Lazy`, or after `GetOrAdd`
  returns), never inside the factory.
- **A dead consumer strands everyone.** Anything the loop throws outside the per-batch
  handling must fault every pending completion source, or callers wait forever. Bounded
  waits are better than hangs, but a stranded item is a bug, not a timeout.
- **Re-entrancy deadlocks, so refuse it by construction.** An operation that submits to
  its own queue waits on the one thread that would serve it, and two queues whose operations
  submit to each other wait on each other. Nothing crashes and nothing times out: it is the
  one hang a consumer can suffer without dying. Nobody writes this on purpose. They reach for
  the public method that already does the job: `GraphWriter.Add` writing reverse links by
  calling the public `Update`, or a rename that also rewrites the thread items referring to
  the renamed node. A convention is not enough, so the consumer marks its own thread
  (`[ThreadStatic] bool`, set as the loop's first statement) and `Submit` refuses any call
  made from a marked thread, naming the target, before it touches a queue. Make the mark
  GLOBAL across queues, not per queue: a per-queue thread-id check misses the two-queue
  cycle. Then "nothing hangs unless the loop crashes" holds by construction, and a crash
  faults every waiter.

## What an in-process queue cannot replace

A queue lives in one process. Exclusion between PROCESSES -- the janet CLI runs fresh on
every hook and shim call, beside a resident janet-mcp -- still needs something the OS
arbitrates. The two honest options:

1. **A lock file beside the target**, taken by the single consumer (`FileLock` in
   WriteQueue.cs: a sidecar `.lock` opened with `FileShare.None`, bounded by
   `LockTimeout`). This is the one lock that stays. With one consumer per process it is
   contended only by other processes, which is its actual job, never by threads of its own
   process. The measurements behind putting
   it beside the file rather than on it are on `note.graph-write-queue`.
2. **A single writer process**, with every other process sending it requests. This removes
   the lock entirely, but every write then fails when that process is down.

Atomics are not what this rule is about. `Interlocked` counters and concurrent collections
for status snapshots (ImageSelector's `recentErrors` ring) coordinate nothing. They are
fine.

## The tell

A boolean that answers "is someone already doing this?", read and written under a lock.
That is an election, and a single consumer deletes it.
