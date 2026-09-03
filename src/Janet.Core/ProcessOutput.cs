using System.ComponentModel;
using System.Diagnostics;

namespace Janet.Core;

/// <summary>What a child process wrote to each stream, and how it exited.</summary>
public sealed record ProcessCapture(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs a process with both output streams redirected and drains them CONCURRENTLY.
/// </summary>
/// <remarks>
/// The obvious code -- ReadToEnd on stdout, then ReadToEnd on stderr, then WaitForExit -- deadlocks
/// the moment the child writes more than one pipe buffer (4 KB on Windows) to the stream nobody is
/// reading yet: the child blocks on a full stderr pipe, the parent blocks waiting for stdout to
/// close, and neither side can notice. Found 2026-09-01 when dotnet_check sat idle for five minutes
/// under a test run with 70 failures, which wrote 6 KB to stderr, while every run with a handful of
/// failures had stayed under 4 KB and passed. The failure needs volume to appear, so it survives
/// every small test and arrives in production. Both readers are started before anything waits, so
/// neither pipe can fill.
///
/// Cancellation kills the child's whole process tree: dotnet spawns MSBuild nodes and test hosts,
/// and a cancelled check that leaves those running has not been cancelled.
/// </remarks>
public static class ProcessOutput
{
    public static ProcessCapture Capture(ProcessStartInfo info, CancellationToken cancellation)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;

        Process process;
        try
        {
            process = Process.Start(info)
                ?? throw new GraphException($"Could not start {info.FileName}. Is it on PATH?");
        }
        catch (Win32Exception ex)
        {
            throw new GraphException($"Could not start {info.FileName}: {ex.Message}. Is it on PATH?", ex);
        }

        using (process)
        using (cancellation.Register(() => KillQuietly(process)))
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellation);

            process.WaitForExit();

            cancellation.ThrowIfCancellationRequested();

            return new ProcessCapture(
                process.ExitCode,
                stdout.GetAwaiter().GetResult(),
                stderr.GetAwaiter().GetResult());
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or not ours to kill. Either way the cancellation still lands.
        }
    }
}
