using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The default graph must be the candidate while the port is staged.
/// </summary>
/// <remarks>
/// This is a safety test, not a convenience one. The cutover computes what to integrate by
/// diffing the live graph against a recorded seed hash; a stray write to the live file through
/// the new code path corrupts that arithmetic silently, and the swap then integrates the wrong
/// set. One forgotten --graph flag is all it would take.
/// <para>
/// In ProcessEnvironmentCollection because <see cref="RefusesWithoutABase"/> clears JanetBase
/// and JANET_BASE on this process.
/// </para>
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public class GraphLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "janet-locator-" + Guid.NewGuid().ToString("n")[..8]);

    public GraphLocatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PrefersTheCandidateWhileItExists()
    {
        File.WriteAllText(Path.Combine(_root, "research.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "research.candidate.json"), "{}");

        Assert.Equal(
            Path.Combine(_root, "research.candidate.json"),
            GraphLocator.Resolve(basePath: _root));
    }

    [Fact]
    public void FallsBackToTheLiveGraphAfterCutover()
    {
        // After the swap the candidate is gone, and the special case retires itself.
        File.WriteAllText(Path.Combine(_root, "research.json"), "{}");

        Assert.Equal(
            Path.Combine(_root, "research.json"),
            GraphLocator.Resolve(basePath: _root));
    }

    [Fact]
    public void TargetingTheLiveGraphStillWorksWhenSaidOutLoud()
    {
        File.WriteAllText(Path.Combine(_root, "research.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "research.candidate.json"), "{}");

        Assert.Equal(
            Path.Combine(_root, "research.json"),
            GraphLocator.Resolve("research.json", _root));
    }

    [Fact]
    public void RefusesWithoutABase()
    {
        // Both saved and both restored: on Windows they are one variable, but on Linux they are
        // two, and restoring only the first leaked a cleared JANET_BASE to every later test.
        string? savedJanetBase = Environment.GetEnvironmentVariable("JanetBase");
        string? savedJanetBaseUpper = Environment.GetEnvironmentVariable("JANET_BASE");
        try
        {
            Environment.SetEnvironmentVariable("JanetBase", null);
            Environment.SetEnvironmentVariable("JANET_BASE", null);
            Assert.Throws<GraphException>(() => GraphLocator.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable("JANET_BASE", savedJanetBaseUpper);
            Environment.SetEnvironmentVariable("JanetBase", savedJanetBase);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a leftover temp dir is not worth failing a test over */ }

        GC.SuppressFinalize(this);
    }
}
