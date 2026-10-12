using System.Collections.Concurrent;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Pins the write queue's in-process mechanism: one consumer per file, batching whatever has
/// arrived, on a background thread of its own.
/// </summary>
/// <remarks>
/// ConcurrencyTests proves the outcomes -- no lost update, honest totals, a failure confined to
/// its caller. These prove the mechanism under them, because the mechanism was wrong while the
/// outcomes held: the lock-and-flag lane let every second submitter clear the "draining" flag,
/// so the next one became a second drainer, and drainers then queued on the sidecar lock and
/// wrote one small batch each. Nothing was lost, so nothing in ConcurrencyTests noticed.
/// <para>
/// Not parallel: AConsumerThatDiesFailsEveryCallerItHolds sets WriteQueue.BeforeApplyForTests.
/// </para>
/// </remarks>
[Collection(WriteQueueProcessStateCollection.Name)]
public sealed class WriteQueueTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "janet-tests", Guid.NewGuid().ToString("n")[..8]);

    public WriteQueueTests()
    {
        Directory.CreateDirectory(_directory);
    }

    /// <summary>
    /// Sixteen writers arriving together land in at most two writes, made by one thread.
    /// </summary>
    /// <remarks>
    /// The sidecar lock is held while the submitters arrive, so the first batch cannot land until
    /// all of them have queued: the consumer takes whatever was there first, waits on the lock,
    /// and everything that arrived meanwhile is the second batch. Two is the ceiling, not a
    /// target -- one is fine. The drainer election this replaced produced nine in every run
    /// measured, because every second arrival made itself a drainer.
    /// <para>
    /// Five rounds, each on a fresh file and so a fresh consumer, because a second consumer does
    /// not always show itself: in one round of three measured, one of two readers took every
    /// item. Five rounds make that a one-in-a-few-hundred escape.
    /// </para>
    /// </remarks>
    [Fact]
    public void SixteenSubmittersShareAtMostTwoBatchesOnOneThread()
    {
        // One write first, to another file, so no submitter is still compiling the queue when
        // the lock is released.
        Append(Path.Combine(_directory, "warm.txt"), 0, new Probe());

        for (int round = 0; round < 5; round++)
        {
            SixteenSubmittersRound(Path.Combine(_directory, $"sixteen-{round}.txt"));
        }
    }

    private static void SixteenSubmittersRound(string path)
    {
        const int submitters = 16;

        Probe probe = new();
        ConcurrentBag<Line> results = [];
        Submitters running;

        using (FileStream held = new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            running = Submitters.Start(submitters, i => results.Add(Append(path, i, probe)));

            // Blocked, not merely started: a submitter blocks only once its item is queued (or,
            // under the old election, while it waits on the lock as a drainer). Releasing earlier
            // would let a late arrival make a third write and blame the queue for it.
            Assert.True(running.WaitUntilAllBlocked(Patience), "the submitters never all blocked");
        }

        List<Exception> failures = running.Join(Patience);

        Assert.Empty(failures);
        Assert.Equal(submitters, results.Count);
        Assert.Equal(submitters, CountLines(File.ReadAllText(path)));

        // Every write adds lines, so the total a result reports identifies the write it was in.
        List<IGrouping<int, Line>> batches = [.. results.GroupBy(r => r.Total)];

        Assert.True(batches.Count <= 2, $"{submitters} submitters landed in {batches.Count} writes: {Describe(batches)}");
        Assert.All(batches, batch => Assert.All(batch, r => Assert.Equal(batch.Count(), r.Batched)));

        Assert.Equal(1, probe.Most);
        Assert.Single(probe.DistinctThreads());
    }

    /// <summary>
    /// An item that arrives just as the consumer runs out of work is still written.
    /// </summary>
    /// <remarks>
    /// Each submitter sends its next item the moment its last one returns, which is the moment
    /// the consumer has finished a batch and is deciding whether to wait. An item lost in that
    /// gap would leave its caller blocked forever, which this sees as a thread that never joins.
    /// </remarks>
    [Fact]
    public void AnItemArrivingAsTheConsumerGoesIdleIsStillWritten()
    {
        string path = Path.Combine(_directory, "idle.txt");
        const int submitters = 2;
        const int rounds = 250;

        Probe probe = new();

        Submitters running = Submitters.Start(submitters, s =>
        {
            for (int r = 0; r < rounds; r++)
            {
                Append(path, (s * rounds) + r, probe);
            }
        });

        List<Exception> failures = running.Join(Patience);

        Assert.Empty(failures);
        Assert.Equal(submitters * rounds, CountLines(File.ReadAllText(path)));
        Assert.Equal(1, probe.Most);
    }

    /// <summary>
    /// The consumer is a dedicated background thread: not the caller, not the pool, and never
    /// the reason a process stays alive.
    /// </summary>
    /// <remarks>
    /// Background, because the janet CLI is a short-lived process and a foreground consumer would
    /// keep it running after Main returns. Not a pool thread, because Submit blocks its caller:
    /// enough blocked callers on pool threads would starve a pooled consumer of the thread it
    /// needs to release them.
    /// </remarks>
    [Fact]
    public void TheConsumerRunsOnItsOwnBackgroundThread()
    {
        string path = Path.Combine(_directory, "background.txt");
        Probe probe = new();

        Append(path, 0, probe);

        Runner consumer = Assert.Single(probe.DistinctThreads());

        Assert.True(consumer.IsBackground, "a foreground consumer keeps the CLI alive after Main returns");
        Assert.False(consumer.IsThreadPoolThread, "a pooled consumer can be starved by the callers it is meant to release");
        Assert.NotEqual(Environment.CurrentManagedThreadId, consumer.ManagedThreadId);
    }

    /// <summary>
    /// A consumer that dies fails every caller it was holding rather than leaving anyone blocked,
    /// and the next write to that file goes to a fresh queue rather than being refused.
    /// </summary>
    /// <remarks>
    /// Apply answers every item it is handed, so the only way to reach this path is a throw in
    /// the loop itself, which BeforeApplyForTests supplies. The hook holds the first batch until
    /// every other submitter has queued behind it, so the throw finds callers in both places:
    /// one in the batch in hand, the rest still in the collection. Every wait is bounded, so a
    /// stranded caller fails the test rather than hanging it.
    /// </remarks>
    [Fact]
    public void AConsumerThatDiesFailsEveryCallerItHolds()
    {
        string path = Path.GetFullPath(Path.Combine(_directory, "dies.txt"));
        const int submitters = 8;
        TimeSpan bound = TimeSpan.FromSeconds(15);

        InvalidOperationException injected = new("injected: the consumer loop broke outside Apply");
        ManualResetEventSlim release = new(false);

        // Compile the queue first, on another file, so every submitter is queued when released.
        Append(Path.Combine(_directory, "dies-warm.txt"), 0, new Probe());

        WriteQueue.BeforeApplyForTests = lanePath =>
        {
            if (string.Equals(lanePath, path, StringComparison.OrdinalIgnoreCase))
            {
                release.Wait(bound);
                throw injected;
            }
        };

        try
        {
            Submitters running = Submitters.Start(submitters, i => Append(path, i, new Probe()));

            Assert.True(running.WaitUntilAllBlocked(bound), "the submitters never all blocked");
            release.Set();

            List<Exception> failures = running.Join(bound);

            Assert.Equal(submitters, failures.Count);
            Assert.All(failures, ex => Assert.Same(injected, ex));

            Assert.False(File.Exists(path), "a consumer that died before applying wrote anyway");

            // The dead lane is gone, so the next write builds a fresh one and lands. The hook is
            // cleared first: left in place it would kill the new lane too.
            WriteQueue.BeforeApplyForTests = null;
            List<Exception> retried = Submitters.Start(1, i => Append(path, submitters + i, new Probe())).Join(bound);
            Assert.Empty(retried);
            Assert.True(File.Exists(path), "a write after the consumer died was refused or lost");
        }
        finally
        {
            WriteQueue.BeforeApplyForTests = null;
        }
    }

    /// <summary>
    /// An operation that submits to its own file is refused at once, and only it fails.
    /// </summary>
    /// <remarks>
    /// Unguarded, the inner Submit waits on the consumer that is running it, and both hang for
    /// good. Every wait here is bounded, so a regression fails the test rather than the run.
    /// </remarks>
    [Fact]
    public void AnOperationWritingItsOwnFileIsRefusedNotHung()
    {
        string path = Path.Combine(_directory, "reentrant-same.txt");

        RefusedReentry(path, path);
    }

    /// <summary>
    /// An operation that submits to another file is refused too, which is what catches a cycle
    /// between two files that a per-file check would miss.
    /// </summary>
    [Fact]
    public void AnOperationWritingAnotherFileIsRefusedNotHung()
    {
        string path = Path.Combine(_directory, "reentrant-outer.txt");
        string other = Path.Combine(_directory, "reentrant-other.txt");

        RefusedReentry(path, other);

        Assert.False(File.Exists(other), "the refused inner write landed anyway");
    }

    /// <summary>
    /// Queues good writes and one re-entrant write into the same batch, then checks the
    /// re-entrant one alone failed.
    /// </summary>
    /// <remarks>
    /// The sidecar lock holds the first batch while the rest queue: the good submitters go
    /// first, then the re-entrant one once they are all blocked, so the second batch is every
    /// good write but the first plus the re-entrant one.
    /// </remarks>
    private void RefusedReentry(string path, string innerPath)
    {
        const int good = 6;
        TimeSpan bound = TimeSpan.FromSeconds(15);

        Append(Path.Combine(_directory, "reentrant-warm.txt"), 0, new Probe());

        ConcurrentBag<Line> results = [];
        Submitters goodOnes;
        Submitters reentrant;

        using (FileStream held = new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            goodOnes = Submitters.Start(good, i => results.Add(Append(path, i, new Probe())));
            Assert.True(goodOnes.WaitUntilAllBlocked(bound), "the good submitters never all blocked");

            reentrant = Submitters.Start(1, _ => WriteQueue.Submit(
                path,
                text =>
                {
                    Append(innerPath, -1, new Probe());
                    return (text + "reentrant\n", new Line(-1, 0, 0));
                },
                CountLines,
                whenMissing: ""));
            Assert.True(reentrant.WaitUntilAllBlocked(bound), "the re-entrant submitter never blocked");
        }

        List<Exception> refusals = reentrant.Join(bound);
        List<Exception> failures = goodOnes.Join(bound);

        GraphException refused = Assert.IsType<GraphException>(Assert.Single(refusals));
        Assert.Contains("from inside another queued write", refused.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(innerPath), refused.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(failures);
        Assert.Equal(good, results.Count);

        string written = File.ReadAllText(path);
        Assert.Equal(good, CountLines(written));
        Assert.DoesNotContain("reentrant", written, StringComparison.Ordinal);

        // Each write reports only the operations that succeeded in it, so a batch that shared a
        // write with the refused one still reports exactly its own good results.
        Assert.All(results.GroupBy(r => r.Total), batch => Assert.All(batch, r => Assert.Equal(batch.Count(), r.Batched)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    internal static Line Append(string path, int index, Probe probe) =>
        WriteQueue.Submit(
            path,
            text =>
            {
                probe.Enter();

                try
                {
                    return (text + index + "\n", new Line(index, 0, 0));
                }
                finally
                {
                    probe.Leave();
                }
            },
            CountLines,
            whenMissing: "");

    internal static int CountLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Describe(List<IGrouping<int, Line>> batches) =>
        string.Join(", ", batches.OrderBy(b => b.Key).Select(b => $"{b.Count()} reaching {b.Key}"));

    /// <summary>
    /// One dedicated thread per submitter, each signalling just before it begins.
    /// </summary>
    /// <remarks>
    /// Dedicated threads rather than Parallel.For, so all of them start at once whatever the
    /// pool's size. Background, so a submitter stranded by a broken queue cannot keep the test
    /// host alive; and every exception is caught, since one escaping a raw thread ends the
    /// process rather than failing the test.
    /// </remarks>
    private sealed class Submitters
    {
        private readonly List<Thread> _threads = [];
        private readonly ConcurrentBag<Exception> _errors = [];
        private readonly CountdownEvent _started;

        private Submitters(int count)
        {
            _started = new CountdownEvent(count);
        }

        public static Submitters Start(int count, Action<int> body)
        {
            Submitters submitters = new(count);

            for (int i = 0; i < count; i++)
            {
                int index = i;
                Thread thread = new(() => submitters.Run(index, body))
                {
                    IsBackground = true,
                    Name = $"submitter-{index}",
                };

                submitters._threads.Add(thread);
                thread.Start();
            }

            return submitters;
        }

        public bool WaitUntilAllBlocked(TimeSpan timeout) =>
            _started.Wait(timeout) &&
            SpinWait.SpinUntil(
                () => _threads.All(t => (t.ThreadState & ThreadState.WaitSleepJoin) != 0),
                timeout);

        public List<Exception> Join(TimeSpan timeout)
        {
            foreach (Thread thread in _threads)
            {
                Assert.True(thread.Join(timeout), $"{thread.Name} never returned: its item was stranded");
            }

            _started.Dispose();
            return [.. _errors];
        }

        private void Run(int index, Action<int> body)
        {
            _started.Signal();

            try
            {
                body(index);
            }
            catch (Exception ex)
            {
                _errors.Add(ex);
            }
        }
    }

    /// <summary>A thread that ran an operation, as it was while running it.</summary>
    /// <remarks>
    /// Captured rather than read later: a thread that has since ended cannot be asked.
    /// </remarks>
    internal sealed record Runner(int ManagedThreadId, bool IsBackground, bool IsThreadPoolThread);

    /// <summary>Records which threads ran operations and how many ran at once.</summary>
    internal sealed class Probe
    {
        private readonly ConcurrentBag<Runner> _threads = [];
        private int _inside;
        private int _most;

        public int Most => Volatile.Read(ref _most);

        public void Enter()
        {
            Thread current = Thread.CurrentThread;
            _threads.Add(new Runner(current.ManagedThreadId, current.IsBackground, current.IsThreadPoolThread));

            int now = Interlocked.Increment(ref _inside);
            int seen = Volatile.Read(ref _most);

            while (now > seen)
            {
                int was = Interlocked.CompareExchange(ref _most, now, seen);

                if (was == seen)
                {
                    break;
                }

                seen = was;
            }
        }

        public void Leave()
        {
            Interlocked.Decrement(ref _inside);
        }

        public List<Runner> DistinctThreads() => [.. _threads.DistinctBy(t => t.ManagedThreadId)];
    }

    internal sealed record Line(int Index, int Total, int Batched) : IBatchedResult<Line>
    {
        public Line WithBatch(int total, int batched) => this with { Total = total, Batched = batched };
    }
}

/// <summary>
/// The consumer wakes without a pool thread, so callers blocked on the pool cannot starve it.
/// </summary>
/// <remarks>
/// Its own collection, run alone: capping the pool is process-wide, and anything running
/// alongside would be starved by it too.
/// </remarks>
[Collection(WriteQueueProcessStateCollection.Name)]
public sealed class WriteQueuePoolTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "janet-tests", Guid.NewGuid().ToString("n")[..8]);

    public WriteQueuePoolTests()
    {
        Directory.CreateDirectory(_directory);
    }

    /// <summary>
    /// Twice as many blocked submitters as the pool has threads still all land.
    /// </summary>
    /// <remarks>
    /// Every pool thread is a caller blocked in Submit, so if waking the consumer needs a pool
    /// thread, nothing ever wakes it. An earlier version of the lane blocked on a Channel's
    /// WaitToReadAsync().AsTask(), which does need one now and then: when a write completes the
    /// wait in the instant between WaitToReadAsync returning and AsTask attaching, the channel
    /// hands the continuation to the pool rather than run it inline. Measured with that consumer
    /// at 30 rounds: starved 2 of 4 runs with AllowSynchronousContinuations, 3 of 3 without.
    /// BlockingCollection.Take, the wait now, needs no pool thread. Rounds repeat the race on
    /// fresh consumers.
    /// </remarks>
    [Fact]
    public void BlockedSubmittersOnACappedPoolStillLand()
    {
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        ThreadPool.GetMinThreads(out int minWorkers, out int _);
        int submitters = minWorkers * 2;

        // Compile the queue before the pool is capped.
        WriteQueueTests.Append(Path.Combine(_directory, "warm.txt"), 0, new WriteQueueTests.Probe());

        Assert.True(ThreadPool.SetMaxThreads(minWorkers, maxIo), "could not cap the pool");

        try
        {
            for (int round = 0; round < 30; round++)
            {
                string path = Path.Combine(_directory, $"capped-{round}.txt");

                // Never disposed: after a timeout the stranded callers still signal it when the
                // pool is uncapped, and signalling a disposed event would end the test host.
                CountdownEvent landed = new(submitters);
                ConcurrentBag<Exception> errors = [];

                for (int i = 0; i < submitters; i++)
                {
                    int index = i;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            WriteQueueTests.Append(path, index, new WriteQueueTests.Probe());
                        }
                        catch (Exception ex)
                        {
                            errors.Add(ex);
                        }
                        finally
                        {
                            landed.Signal();
                        }
                    });
                }

                Assert.True(
                    landed.Wait(TimeSpan.FromSeconds(20)),
                    $"round {round}: {landed.CurrentCount} of {submitters} callers still blocked on a pool of {minWorkers}");
                Assert.Empty(errors);
                Assert.Equal(submitters, WriteQueueTests.CountLines(File.ReadAllText(path)));
            }
        }
        finally
        {
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}

/// <summary>
/// Tests that set process-wide state the write queue reads, run with nothing else alongside.
/// </summary>
/// <remarks>
/// Two kinds: capping the thread pool, and setting WriteQueue.BeforeApplyForTests. The hook only
/// acts on its own test's file, but it is still a static any lane reads, the shape of the
/// LockTimeout race WriteLockTimeoutCollection was made for. That collection is not reused
/// because its name and remarks are about LockTimeout alone.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WriteQueueProcessStateCollection
{
    public const string Name = "WriteQueue.ProcessState";
}
