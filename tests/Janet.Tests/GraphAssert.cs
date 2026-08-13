using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>Comparisons whose failure message says which line moved, not just that one did.</summary>
public static class GraphAssert
{
    /// <summary>
    /// Byte equality between a produced file and its golden, reported as the first differing
    /// line.
    /// </summary>
    /// <remarks>
    /// "Assert.Equal on two 15KB strings" is technically the same check and useless to read.
    /// </remarks>
    public static void SameFile(string goldenPath, string actualPath, string label)
    {
        string expected = File.ReadAllText(goldenPath);
        string actual = File.ReadAllText(actualPath);

        if (expected == actual)
        {
            return;
        }

        SameLines(label, expected, actual, trimTrailing: false);

        // Identical line by line and still unequal: the difference is in the line endings, and
        // that matters here -- the writer is supposed to preserve the file's own.
        Assert.Fail(
            $"[{label}] {goldenPath} and {actualPath} differ only in line endings or trailing bytes " +
            $"({expected.Length} vs {actual.Length} characters).");
    }

    /// <summary>Line-by-line comparison, reporting the first difference with both sides.</summary>
    public static void SameLines(string label, string expected, string actual, bool trimTrailing = true)
    {
        string[] expectedLines = Split(expected, trimTrailing);
        string[] actualLines = Split(actual, trimTrailing);

        for (int i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
        {
            string e = i < expectedLines.Length ? expectedLines[i] : "(missing)";
            string a = i < actualLines.Length ? actualLines[i] : "(missing)";

            Assert.True(e == a, $"[{label}] line {i + 1} differs:\n  golden: {e}\n  actual: {a}");
        }

        Assert.Equal(expectedLines.Length, actualLines.Length);
    }

    /// <summary>
    /// A write must touch its own node and nothing else -- that is the point of splicing rather
    /// than reserializing.
    /// </summary>
    public static void OnlyOneNodeChanged(string changedGraph, string expectedId)
    {
        string[] original = File.ReadAllLines(Fixture.Layout);
        string[] changed = File.ReadAllLines(changedGraph);

        Assert.Equal(original.Length, changed.Length);

        List<int> differing = [.. Enumerable.Range(0, original.Length).Where(i => original[i] != changed[i])];

        Assert.NotEmpty(differing);

        string text = File.ReadAllText(changedGraph);
        (int start, int end) = NodeText.FindSpan(text, expectedId);

        int firstLineOfNode = text[..start].Count(c => c == '\n');
        int lastLineOfNode = text[..end].Count(c => c == '\n');

        Assert.All(differing, line => Assert.InRange(line, firstLineOfNode, lastLineOfNode));
    }

    private static string[] Split(string text, bool trimTrailing)
    {
        string[] lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        // PowerShell's console writer pads differently from ours, and the contract for the
        // formatted view is its content and order, not invisible spaces.
        return trimTrailing ? [.. lines.Select(l => l.TrimEnd())] : lines;
    }
}
