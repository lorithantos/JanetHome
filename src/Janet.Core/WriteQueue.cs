using System.Collections.Concurrent;

namespace Janet.Core;

/// <summary>
/// A result that can be restated once the batch it belonged to has landed.
/// </summary>
/// <remarks>
/// An operation computes its result against the text as it stood partway through a batch, and
/// that intermediate state is never what ends up on disk. Reporting it would mean returning a
/// count that was true in memory for a moment and never true in the file -- the quiet kind of
/// wrong. Every result is restated with the batch's totals before its caller sees it.
/// </remarks>
public interface IBatchedResult<out T>
{
    /// <summary>Returns this result with the totals of the write it landed in.</summary>
    T WithBatch(int total, int batched);
}

/// <summary>
/// Serialises every write to a file, and merges the ones that arrive together.
/// </summary>
/// <remarks>
/// Both of Janet's mutable files are shared between concurrent writers, and both got this
/// wrong in the same way. The thread-item list lost a session's notes irrecoverably on
/// 2026-08-08 to an unlocked read-modify-write; the research catalog had the identical hole and
/// had simply not been unlucky yet. Two concurrent agent sessions is the normal case here, which
/// is why the list needed a mutex at all.
///
/// Three failures follow from read-splice-write with nothing in between, and all three are real:
///
///   1. Lost updates. Two writers read the same text, both change it, and the second write
///      erases the first -- silently, with no error anywhere.
///   2. Torn reads. File.WriteAllText truncates and then writes, so a reader arriving mid-write
///      sees a half-file. A consumer that cannot parse it reports corruption, which is a much
///      more alarming thing than what happened.
///   3. Multi-step writes that are visible half-done. An add and its reverse links, written
///      separately, leave the file inconsistent for as long as that takes -- and permanently if
///      anything fails partway.
///
/// The queue answers all three. Work is batched: one read, every queued operation applied to
/// that text in arrival order, one atomic write, and only then does any caller return. That is
/// stronger than plain serialisation and cheaper -- N writes to a 110KB file become one -- and
/// no caller is told about a state that was never on disk.
///
/// Each file has one BlockingCollection and exactly one consumer, a background thread started
/// the first time the file is written. Callers add their operation and block on its
/// completion; the consumer takes everything queued at that moment as one batch, writes it, and
/// takes whatever arrived meanwhile as the next. Nobody decides who writes, because only one
/// thread ever does -- there is no election and no who-is-writing flag to get wrong. The one
/// way left to hang it, an operation submitting a write of its own, is refused by Submit with
/// a GraphException naming the target, rather than waited on.
///
/// A failing operation fails only its own caller. A batch is not a transaction across unrelated
/// requests: someone else's duplicate id is not a reason to reject your valid update, so the
/// failed operation's text is discarded and the rest of the batch still commits.
///
/// Cross-process exclusion is a lock file beside the target, because the CLI runs as a separate
/// process on every hook and shim invocation and cannot join an in-process queue. Merging is
/// in-process only; exclusion is what makes the cross-process case correct.
/// </remarks>
public static class WriteQueue
{
    private static readonly ConcurrentDictionary<string, Lane> Lanes =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>How long to wait for another process to finish its batch before giving up.</summary>
    /// <remarks>
    /// Bounded rather than indefinite: a lock held by a wedged process would otherwise hang a
    /// hook forever, and a hook that hangs is indistinguishable from an agent thinking.
    /// </remarks>
    public static TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Queues one edit and returns its result once the batch containing it has been written.
    /// </summary>
    /// <param name="path">The file to edit.</param>
    /// <param name="operation">
    /// Applied to the file's current text; returns the new text and the result. Must be pure
    /// with respect to disk -- the queue owns reading and writing, and an operation that read
    /// the file itself would see the pre-batch state. It runs on the file's consumer thread, so it
    /// must not submit to the queue at all, for this file or any other; Submit refuses it.
    /// </param>
    /// <param name="totalOf">
    /// How many things the file holds, for restating results. Called once per batch.
    /// </param>
    /// <param name="whenMissing">
    /// The text to start from when the file does not exist yet. Null means a missing file is an
    /// error -- the catalog must exist, while the thread-item list is legitimately absent until
    /// something is written to it.
    /// </param>
    /// <remarks>
    /// Refused outright on a consumer thread -- any lane's, not just this path's. A Submit from
    /// inside an operation would wait on a consumer that is busy running the operation, and
    /// hang: directly when it is the same file, and through a cycle when it is not (a catalog
    /// operation writing a thread item whose operation writes the catalog). A per-lane check
    /// would miss the cycle. The check comes before the lane is created or started, so a
    /// refused call changes nothing.
    /// </remarks>
    public static T Submit<T>(
        string path,
        Func<string, (string Text, T Result)> operation,
        Func<string, int> totalOf,
        string? whenMissing = null)
        where T : IBatchedResult<T>
    {
        if (t_onConsumer)
        {
            throw new GraphException(
                $"Refused a write to {Path.GetFullPath(path)} from inside another queued write. " +
                "A write-queue operation must transform the text it is handed and must not write " +
                "files itself: this call would wait on a consumer that is busy running its caller, " +
                "and hang.");
        }

        return Lanes.GetOrAdd(Path.GetFullPath(path), full => new Lane(full, totalOf, whenMissing))
                    .Submit(operation);
    }

    /// <summary>True on every lane's consumer thread, for that thread's whole life.</summary>
    /// <remarks>
    /// Thread-static and never reset: a consumer is a dedicated thread that does nothing else,
    /// and it is what lets Submit refuse re-entry from any lane rather than only its own.
    /// </remarks>
    [ThreadStatic]
    private static bool t_onConsumer;

    /// <summary>
    /// Test-only: called on a lane's consumer thread with the lane's path after a batch is taken
    /// and before it is applied. A throw here is a throw outside Apply, which is the only way a
    /// test can reach the dead-consumer path. Null, and never set, outside tests.
    /// </summary>
    internal static Action<string>? BeforeApplyForTests { get; set; }

    /// <summary>One file's queue. One lane per path, so unrelated files never block each other.</summary>
    /// <remarks>
    /// A blocking collection with exactly one consumer, and that consumer is the only thread that
    /// ever writes the file. There is no election to win and no flag saying who is draining, so
    /// there is nothing for two submitters to disagree about. The lane this replaced decided its
    /// writer with a lock and a flag, and got the flag wrong in a way that still lost nothing:
    /// every second submitter cleared it, so the next became a second drainer, and the drainers
    /// then queued on the sidecar lock and wrote small batches one after another.
    ///
    /// BlockingCollection rather than a Channel, because its Take is a synchronous wait that
    /// needs no pool thread to wake. Blocking a dedicated thread on Channel's
    /// WaitToReadAsync().AsTask() does need one now and then -- a write that completes the wait
    /// between WaitToReadAsync returning and AsTask attaching has its continuation queued to the
    /// pool -- and Submit's callers block, often on pool threads, so on a saturated pool the
    /// consumer never woke (WriteQueuePoolTests). The hand-built fix, an event set after each
    /// write, worked only because of the order of two lines; BlockingCollection owns that order.
    /// </remarks>
    private sealed class Lane
    {
        private readonly string _path;
        private readonly Func<string, int> _totalOf;
        private readonly string? _whenMissing;

        // Never disposed: a lane lives as long as the process, and its consumer blocks on this
        // collection for all of that time. Disposing it would pull it out from under that thread.
        private readonly BlockingCollection<IWorkItem> _queue = new(new ConcurrentQueue<IWorkItem>());

        // Lazy, and started from Submit rather than the constructor: GetOrAdd may build a lane it
        // then throws away, and a discarded lane must not leave a thread behind. Only the lane
        // GetOrAdd returns is ever submitted to.
        private readonly Lazy<Thread> _consumer;

        public Lane(string path, Func<string, int> totalOf, string? whenMissing)
        {
            _path = path;
            _totalOf = totalOf;
            _whenMissing = whenMissing;
            _consumer = new Lazy<Thread>(StartConsumer, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public T Submit<T>(Func<string, (string Text, T Result)> operation)
            where T : IBatchedResult<T>
        {
            _ = _consumer.Value;

            WorkItem<T> item = new(operation);

            try
            {
                _queue.Add(item);
            }
            catch (InvalidOperationException ex) when (_queue.IsAddingCompleted)
            {
                // Only a consumer that died completes adding; see Consume.
                throw new GraphException(
                    $"The write queue for {_path} has stopped after an unexpected error. Nothing was written; restart the process.",
                    ex);
            }

            return item.Wait();
        }

        /// <summary>
        /// Starts the one thread that reads this lane, for the life of the process.
        /// </summary>
        /// <remarks>
        /// A dedicated thread rather than the pool, because Submit blocks its caller: callers
        /// blocked on pool threads could starve a pooled consumer of the thread it needs to
        /// release them. Background, because the CLI is a short-lived process and a foreground
        /// thread would keep it alive after Main returns.
        /// </remarks>
        private Thread StartConsumer()
        {
            Thread thread = new(Consume)
            {
                IsBackground = true,
                Name = "janet-write-queue " + Path.GetFileName(_path),
            };

            thread.Start();
            return thread;
        }

        /// <summary>
        /// Takes everything queued as one batch, writes it, and waits for more.
        /// </summary>
        /// <remarks>
        /// Take blocks for the first item; TryTake then gathers everything else already queued,
        /// so whatever arrives while a batch is being written is the next batch and writers who
        /// arrive together share a write. Waiting synchronously is deliberate: this thread has
        /// nothing else to do, and Take wakes it without borrowing a pool thread.
        /// </remarks>
        private void Consume()
        {
            t_onConsumer = true;

            List<IWorkItem> batch = [];

            try
            {
                while (true)
                {
                    batch = [_queue.Take()];

                    while (_queue.TryTake(out IWorkItem? item))
                    {
                        batch.Add(item);
                    }

                    BeforeApplyForTests?.Invoke(_path);
                    Apply(batch);
                }
            }
            catch (Exception ex)
            {
                // Apply answers every item it is handed, so reaching here means the loop itself
                // broke. Nobody is left to take from the lane, so close it to new work and fail
                // everything still in it rather than leave its callers blocked forever.
                // CompleteAdding waits out any Add already in progress, so nothing can land after
                // the drain below. Failing an item Apply already answered does nothing.
                _queue.CompleteAdding();

                foreach (IWorkItem item in batch)
                {
                    item.Fail(ex);
                }

                while (_queue.TryTake(out IWorkItem? item))
                {
                    item.Fail(ex);
                }
            }
        }

        private void Apply(List<IWorkItem> batch)
        {
            try
            {
                using FileLock fileLock = FileLock.Take(_path, LockTimeout);

                string text = Read();
                string original = text;

                foreach (IWorkItem item in batch)
                {
                    // A throw here is this item's caller's problem and nobody else's: the text
                    // is left as it was and the batch carries on.
                    text = item.Run(text);
                }

                if (!string.Equals(text, original, StringComparison.Ordinal))
                {
                    AtomicWrite(_path, text);
                }

                int total = _totalOf(text);
                int written = batch.Count(i => i.Succeeded);

                foreach (IWorkItem item in batch)
                {
                    item.Complete(total, written);
                }
            }
            catch (Exception ex)
            {
                // Failing to take the lock, read, or write is everyone's problem: nothing was
                // written, so every caller in the batch has to hear about it rather than be
                // handed a result for work that did not happen.
                foreach (IWorkItem item in batch)
                {
                    item.Fail(ex);
                }
            }
        }

        private string Read()
        {
            if (File.Exists(_path))
            {
                return File.ReadAllText(_path);
            }

            return _whenMissing ?? throw new GraphException($"File not found: {_path}");
        }

        /// <summary>
        /// Writes to a sibling temp file and renames it over the target.
        /// </summary>
        /// <remarks>
        /// A rename is atomic on both platforms and File.WriteAllText is not, so a reader sees
        /// either the whole old file or the whole new one and never the seam between them.
        /// Readers are deliberately not made to take the lock: reading is the most common thing
        /// done to these files, and making it wait behind a write would be a real cost paid for
        /// a problem the rename already solves.
        ///
        /// The temp file is a sibling so the rename stays within one volume; across volumes it
        /// degrades to a copy, which is exactly the non-atomic write being avoided.
        /// </remarks>
        private static void AtomicWrite(string path, string text)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = path + ".tmp-" + Guid.NewGuid().ToString("n")[..8];

            try
            {
                NodeText.WriteUtf8NoBom(temp, text);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); }
                    catch (IOException) { /* the failure being reported is the interesting one */ }
                }

                throw;
            }
        }
    }

    private interface IWorkItem
    {
        bool Succeeded { get; }

        /// <summary>Applies the operation, returning the text unchanged if it threw.</summary>
        string Run(string text);

        void Complete(int total, int batched);

        void Fail(Exception error);
    }

    private sealed class WorkItem<T>(Func<string, (string Text, T Result)> operation) : IWorkItem
        where T : IBatchedResult<T>
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private T _result = default!;
        private Exception? _error;

        public bool Succeeded => _error is null;

        public string Run(string text)
        {
            try
            {
                (string updated, T result) = operation(text);
                _result = result;
                return updated;
            }
            catch (Exception ex)
            {
                _error = ex;
                return text;
            }
        }

        public void Complete(int total, int batched)
        {
            if (_error is not null)
            {
                _completion.TrySetException(_error);
                return;
            }

            _completion.TrySetResult(_result.WithBatch(total, batched));
        }

        public void Fail(Exception error) => _completion.TrySetException(error);

        /// <summary>Blocks until the batch lands. GetResult rather than Wait, so the operation's own exception surfaces rather than an AggregateException wrapping it.</summary>
        public T Wait() => _completion.Task.GetAwaiter().GetResult();
    }
}

/// <summary>
/// An exclusive lock on a file, held across processes.
/// </summary>
/// <remarks>
/// A sidecar file rather than a named mutex. The thread-item list used a named mutex, which
/// works but carries two problems this does not: a mutex abandoned by a dying process raises
/// AbandonedMutexException at whoever takes it next, leaving that caller to decide whether the
/// file is torn; and named mutexes behave differently across platforms and containers, which
/// matters for a tool whose reason to exist is running somewhere other than Windows. An
/// exclusive file handle is the same mechanism everywhere and is released by the OS on process
/// death with nothing to reason about.
///
/// It is also the same mechanism a second PROCESS contends on -- the OS enforces share mode per
/// handle and does not care which process opened it -- so a test with two handles proves the
/// cross-process case as well as it can be proved without spawning one.
///
/// The lock is beside the file rather than on it, so an exclusive open never collides with an
/// ordinary reader.
/// </remarks>
internal sealed class FileLock : IDisposable
{
    private readonly FileStream _stream;

    private FileLock(FileStream stream) => _stream = stream;

    public static FileLock Take(string path, TimeSpan timeout)
    {
        string lockPath = path + ".lock";
        string? directory = Path.GetDirectoryName(lockPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        int wait = 5;

        while (true)
        {
            try
            {
                return new FileLock(new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(wait);
                wait = Math.Min(wait * 2, 250);
            }
            catch (IOException ex)
            {
                throw new GraphException(
                    $"Could not take the write lock on {path} within {timeout.TotalSeconds:0}s. " +
                    $"Another janet process is writing to it, or {lockPath} was left locked by one " +
                    "that died. Nothing was written.", ex);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
