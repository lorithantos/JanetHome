using System.Collections.Concurrent;

namespace Janet.Core;

/// <summary>
/// A result that can be restated once the batch it belonged to has landed.
/// </summary>
/// <remarks>
/// An operation computes its result against the text as it stood partway through a batch, and
/// that intermediate state is never what ends up on disk. Reporting it would mean returning a
/// node count that was true in memory for a moment and never true in the file -- the quiet kind
/// of wrong. Every result is restated with the final count before its caller sees it.
/// </remarks>
public interface IGraphResult<out T>
{
    /// <summary>Returns this result with the batch's final totals substituted.</summary>
    T WithBatch(int totalNodes, int batched);
}

/// <summary>
/// Serialises every write to a graph file, and merges the ones that arrive together.
/// </summary>
/// <remarks>
/// Before this, each of Add, Update and Rename did its own read, splice and write with nothing
/// in between. Three things follow from that, and all three are real:
///
///   1. Lost updates. Two writers read the same text, both splice their own node into it, and
///      the second write erases the first. This is the 2026-08-08 thread-item incident with a
///      different file underneath it, and two concurrent sessions is the normal case here.
///   2. Torn reads. The write was File.WriteAllText, which truncates and then writes, so a
///      reader arriving mid-write sees a half-file. A query that fails to parse the catalog
///      reads as "the catalog is corrupt", which is a much more alarming thing than it was.
///   3. A partially linked add. Add wrote the node, then wrote each reverse link as its own
///      separate file write, so the node was visible without its back-links for as long as
///      that took, and a crash in the middle left it that way permanently.
///
/// The queue answers all three. Work is batched: one read, every queued operation applied to
/// that text in arrival order, one atomic write, and only then does any caller return. That is
/// stronger than plain serialisation and cheaper -- N writes to a 110KB file become one -- and
/// it means no caller is told about a state that was never on disk.
///
/// A failing operation fails only its own caller. A batch is not a transaction across unrelated
/// requests: someone else's duplicate id is not a reason to reject your valid update, so the
/// failed operation's text is discarded and the rest of the batch still commits.
///
/// Cross-process exclusion is a lock file beside the graph, because the CLI runs as a separate
/// process on every hook and shim invocation and cannot join an in-process queue. Merging is
/// in-process only; exclusion is what makes the cross-process case correct.
/// </remarks>
public static class GraphQueue
{
    private static readonly ConcurrentDictionary<string, Lane> Lanes =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>How long to wait for another process to finish its batch before giving up.</summary>
    /// <remarks>
    /// Bounded rather than indefinite: a stale lock from a killed process would otherwise hang
    /// a hook forever, and a hook that hangs is indistinguishable from an agent thinking.
    /// </remarks>
    public static TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Queues one edit and returns its result once the batch containing it has been written.
    /// </summary>
    /// <param name="graphPath">The graph to edit.</param>
    /// <param name="operation">
    /// Applied to the graph's current text; returns the new text and the result. Must be pure
    /// with respect to disk -- the queue owns reading and writing, and an operation that reads
    /// the file itself would see the pre-batch state.
    /// </param>
    public static T Submit<T>(string graphPath, Func<string, (string Text, T Result)> operation)
        where T : IGraphResult<T> =>
        Lanes.GetOrAdd(Path.GetFullPath(graphPath), path => new Lane(path)).Submit(operation);

    /// <summary>One graph file's queue. One lane per path, so unrelated graphs never block each other.</summary>
    private sealed class Lane(string path)
    {
        private readonly Queue<IWorkItem> _pending = new();
        private readonly Lock _gate = new();
        private bool _draining;

        public T Submit<T>(Func<string, (string Text, T Result)> operation)
            where T : IGraphResult<T>
        {
            WorkItem<T> item = new(operation);
            bool mine;

            lock (_gate)
            {
                _pending.Enqueue(item);

                // Whoever finds the lane idle becomes the writer for as long as work keeps
                // arriving. Everyone else waits for that thread to finish the batch they are
                // now part of, rather than queueing behind a lock and writing one at a time.
                mine = !_draining;
                _draining = mine;
            }

            if (mine)
            {
                Drain();
            }

            return item.Wait();
        }

        private void Drain()
        {
            while (true)
            {
                List<IWorkItem> batch;

                lock (_gate)
                {
                    if (_pending.Count == 0)
                    {
                        // Cleared under the same lock that enqueues, so an item cannot arrive
                        // between the check and the clear and be left with nobody to write it.
                        _draining = false;
                        return;
                    }

                    batch = [.. _pending];
                    _pending.Clear();
                }

                Apply(batch);
            }
        }

        private void Apply(List<IWorkItem> batch)
        {
            try
            {
                using FileLock fileLock = FileLock.Take(path, LockTimeout);

                string text = File.Exists(path)
                    ? File.ReadAllText(path)
                    : throw new GraphException($"Research graph not found: {path}");

                string original = text;

                foreach (IWorkItem item in batch)
                {
                    // A throw here is this item's caller's problem and nobody else's: the text
                    // is left as it was and the batch carries on.
                    text = item.Run(text);
                }

                if (!string.Equals(text, original, StringComparison.Ordinal))
                {
                    AtomicWrite(path, text);
                }

                int totalNodes = CountNodes(text, path);
                int written = batch.Count(i => i.Succeeded);

                foreach (IWorkItem item in batch)
                {
                    item.Complete(totalNodes, written);
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

        private static int CountNodes(string text, string path) => ResearchGraph.Parse(text, path).Nodes.Count;

        /// <summary>
        /// Writes to a sibling temp file and renames it over the target.
        /// </summary>
        /// <remarks>
        /// A rename is atomic on both platforms and File.WriteAllText is not, so a reader sees
        /// either the whole old file or the whole new one and never the seam between them.
        /// Readers are deliberately not made to take the lock: a query is the most common thing
        /// this catalog does, and making it wait behind a write would be a real cost paid for a
        /// problem the rename already solves.
        ///
        /// The temp file is a sibling so the rename stays within one volume; across volumes it
        /// degrades to a copy, which is exactly the non-atomic write being avoided.
        /// </remarks>
        private static void AtomicWrite(string path, string text)
        {
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

        void Complete(int totalNodes, int batched);

        void Fail(Exception error);
    }

    private sealed class WorkItem<T>(Func<string, (string Text, T Result)> operation) : IWorkItem
        where T : IGraphResult<T>
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

        public void Complete(int totalNodes, int batched)
        {
            if (_error is not null)
            {
                _completion.TrySetException(_error);
                return;
            }

            _completion.TrySetResult(_result.WithBatch(totalNodes, batched));
        }

        public void Fail(Exception error) => _completion.TrySetException(error);

        /// <summary>Blocks until the batch lands. GetResult rather than Wait, so the operation's own exception surfaces rather than an AggregateException wrapping it.</summary>
        public T Wait() => _completion.Task.GetAwaiter().GetResult();
    }
}

/// <summary>
/// An exclusive lock on a graph, held across processes.
/// </summary>
/// <remarks>
/// A sidecar file rather than a named mutex: named mutexes behave differently across platforms
/// and containers, while an exclusive file handle is the same mechanism everywhere. It is also
/// the same mechanism a second PROCESS contends on -- the OS enforces share mode per handle and
/// does not care which process opened it -- so a test with two handles proves the cross-process
/// case as well as it can be proved without spawning one.
///
/// The lock is beside the graph rather than on it, so an exclusive open never collides with an
/// ordinary reader.
/// </remarks>
internal sealed class FileLock : IDisposable
{
    private readonly FileStream _stream;

    private FileLock(FileStream stream) => _stream = stream;

    public static FileLock Take(string graphPath, TimeSpan timeout)
    {
        string lockPath = graphPath + ".lock";
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
                    $"Could not take the write lock on {graphPath} within {timeout.TotalSeconds:0}s. " +
                    $"Another janet process is writing to it, or {lockPath} was left locked by one " +
                    "that died. Nothing was written.", ex);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
