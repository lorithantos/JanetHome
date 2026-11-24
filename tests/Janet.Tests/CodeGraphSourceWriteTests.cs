using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// What counts as this repository's source when judging whether its graph is stale. A nested
/// checkout is another checkout's source: on 2026-11-24 a `git worktree add` under
/// .claude\worktrees made the main checkout's graph read stale at the worktree's creation time.
/// </summary>
public class CodeGraphSourceWriteTests : IDisposable
{
    private static readonly DateTime Older = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 11, 24, 19, 0, 0, DateTimeKind.Utc);

    private readonly DirectoryInfo repository = Directory.CreateTempSubdirectory("janet-nsw-");

    public CodeGraphSourceWriteTests()
    {
        // The root is itself a checkout: its own .git must not exclude it.
        Directory.CreateDirectory(Path.Combine(repository.FullName, ".git"));
        Write(Older, "src", "Program.cs");
    }

    public void Dispose() => repository.Delete(recursive: true);

    [Fact]
    public void ALinkedWorktreeBelowTheRootIsNotThisRepositorysSource()
    {
        string worktree = Directory.CreateDirectory(Path.Combine(repository.FullName, ".claude", "worktrees", "feature")).FullName;
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: C:/elsewhere/.git/worktrees/feature\n");
        Write(Newer, ".claude", "worktrees", "feature", "src", "Program.cs");

        Assert.Equal(Older, CodeGraph.NewestSourceWrite(repository.FullName));
    }

    [Fact]
    public void ANestedCloneBelowTheRootIsNotThisRepositorysSource()
    {
        Directory.CreateDirectory(Path.Combine(repository.FullName, "vendor", "lib", ".git"));
        Write(Newer, "vendor", "lib", "Lib.cs");

        Assert.Equal(Older, CodeGraph.NewestSourceWrite(repository.FullName));
    }

    [Fact]
    public void AnOrdinaryFolderStillCountsWhateverItIsCalled()
    {
        // The rule is "a nested checkout", not a folder name: .claude without a .git is source.
        Write(Newer, ".claude", "worktrees", "loose", "Scratch.cs");

        Assert.Equal(Newer, CodeGraph.NewestSourceWrite(repository.FullName));
    }

    [Fact]
    public void BuildOutputIsStillNotSource()
    {
        Write(Newer, "src", "obj", "Debug", "Generated.cs");
        Write(Newer, "src", "bin", "Debug", "Copied.cs");

        Assert.Equal(Older, CodeGraph.NewestSourceWrite(repository.FullName));
    }

    private void Write(DateTime when, params string[] relative)
    {
        string path = Path.Combine([repository.FullName, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// source\n");
        File.SetLastWriteTimeUtc(path, when);
    }
}
