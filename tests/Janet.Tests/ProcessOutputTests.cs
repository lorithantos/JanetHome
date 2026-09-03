using System.Diagnostics;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// A child that writes more than a pipe buffer to BOTH streams must still come back.
/// </summary>
/// <remarks>
/// The bug this pins needed volume to appear: reading stdout to the end and then stderr worked
/// for every run that wrote under 4 KB of stderr and deadlocked on the first that wrote 6 KB --
/// a test run with 70 failures, 2026-09-01. The child here writes 64 KB to each stream, stderr
/// first, which is the order that fills the unread pipe. With the sequential reads this test
/// never returns; the wait turns that into a failure with a name.
/// </remarks>
public class ProcessOutputTests
{
    [Fact]
    public async Task BothStreamsDrainEvenWhenEachExceedsAPipeBuffer()
    {
        ProcessStartInfo info = new() { FileName = "pwsh" };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(
            "$e = [string]::new('e', 65536); $o = [string]::new('o', 65536); " +
            "[Console]::Error.Write($e); [Console]::Out.Write($o); exit 3");

        Task<ProcessCapture> run = Task.Run(() => ProcessOutput.Capture(info, CancellationToken.None));
        Task finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(90)));

        Assert.True(ReferenceEquals(finished, run), "Capture did not return: the pipes deadlocked.");

        ProcessCapture captured = await run;

        Assert.Equal(3, captured.ExitCode);
        Assert.Equal(65536, captured.StandardOutput.Count(c => c == 'o'));
        Assert.Equal(65536, captured.StandardError.Count(c => c == 'e'));
    }

    [Fact]
    public void AMissingExecutableIsNamedRatherThanThrownRaw()
    {
        ProcessStartInfo info = new() { FileName = "no-such-executable-janet-" + Guid.NewGuid().ToString("n") };

        GraphException ex = Assert.Throws<GraphException>(() => ProcessOutput.Capture(info, CancellationToken.None));

        Assert.Contains(info.FileName, ex.Message, StringComparison.Ordinal);
    }
}
