using Xunit;

namespace Janet.Tests;

/// <summary>
/// Where the frozen graphs and the recorded answers live, and how a test gets a writable copy.
/// </summary>
/// <remarks>
/// Everything here resolves beside the test assembly, never in the repository. The tests used
/// to read the live research.json, which meant their expectations changed whenever anyone added
/// a node -- a suite that rewrites its own baseline is not a baseline.
/// </remarks>
public static class Fixture
{
    /// <summary>The whole corpus. Ranking is tuned against all of it; a subset ranks differently.</summary>
    public static string Corpus { get; } = Resolve("Fixtures", "research.json");

    /// <summary>
    /// Ten nodes lifted from the corpus as exact text, keeping the comment keys, the blank line
    /// between groups and the field order. The writers assert byte equality, so the fixture has
    /// to carry the real file's layout or the goldens would freeze a layout it never had.
    /// </summary>
    public static string Layout { get; } = Resolve("Fixtures", "layout.json");

    /// <summary>What the original PowerShell answered, recorded by tests/Janet.Goldens.</summary>
    public static string Golden(string kind, string label, string extension) =>
        Resolve("Goldens", kind, Cases.Slug(label) + extension);

    public static string ReadGolden(string kind, string label, string extension) =>
        File.ReadAllText(Golden(kind, label, extension));

    private static string Resolve(params string[] parts)
    {
        string path = Path.Combine([AppContext.BaseDirectory, .. parts]);

        Assert.True(File.Exists(path), $"missing fixture or golden: {path}");

        return path;
    }
}

/// <summary>
/// A writable copy of a fixture graph, deleted with the test class.
/// </summary>
/// <remarks>
/// Every write test needs its own copy: they mutate the file, and sharing one would make the
/// result depend on the order xunit happened to run them in.
/// </remarks>
public sealed class Sandbox : IDisposable
{
    private readonly List<string> _directories = [];

    /// <summary>A private directory holding a copy of the layout fixture as research.json.</summary>
    public string CopyOfLayout()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "janet-tests", Guid.NewGuid().ToString("n")[..8]);

        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        string graph = Path.Combine(directory, "research.json");
        File.Copy(Fixture.Layout, graph);

        return graph;
    }

    public void Dispose()
    {
        foreach (string directory in _directories.Where(Directory.Exists))
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* a leftover temp directory is not worth failing a test over */ }
        }
    }
}
