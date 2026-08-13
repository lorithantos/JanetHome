using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Janet.Tests;

namespace Janet.Goldens;

/// <summary>
/// Records what the original PowerShell scripts answer, so the tests can assert against it
/// without PowerShell.
/// </summary>
/// <remarks>
/// Run by hand, not by the build:
///
///     dotnet run --project tests/Janet.Goldens -- --ref HEAD
///
/// The scripts it runs are pulled out of git at the given ref, never taken from the working
/// tree -- the working copies are shims over the CLI now, and comparing the port against a shim
/// over itself is a test that cannot fail. The ref is recorded in meta.json beside the goldens,
/// with a hash of each script, so what a golden stands for is checkable rather than remembered.
///
/// There is deliberately no mode that regenerates goldens from Janet.Core. A golden that the
/// implementation writes is a golden that agrees with the implementation by construction. If
/// the behaviour is ever meant to change, the golden changes by hand, in a diff someone reads.
/// </remarks>
public static class Program
{
    private static readonly string[] OriginalScripts =
    [
        "Get-Research.ps1",
        "Add-ResearchNode.ps1",
        "Update-ResearchNode.ps1",
        "Rename-ResearchNode.ps1",

        // The thread-item entry points dot-source their shared helpers from $PSScriptRoot, so
        // the extract has to carry that file too or none of them run.
        "ThreadItems.Common.ps1",
        "Add-ThreadItem.ps1",
        "Update-ThreadItem.ps1",
        "Complete-ThreadItem.ps1",
        "Set-ActiveThread.ps1",
        "Show-ThreadItems.ps1",
    ];

    /// <summary>Files that define functions rather than doing anything, so the shim check does not apply.</summary>
    private static readonly string[] NotEntryPoints = ["ThreadItems.Common.ps1"];

    /// <summary>
    /// Places where the port deliberately does NOT do what the original did.
    /// </summary>
    /// <remarks>
    /// A golden that the implementation is allowed to edit is not a golden, so this table is
    /// deliberately awkward: each entry names the case, states the reason in full, is printed
    /// while generating, is listed in meta.json beside the goldens, and throws if the text it
    /// expects to correct is not there. Nothing gets corrected quietly, and nothing gets
    /// corrected without a sentence saying why.
    /// </remarks>
    private static readonly Dictionary<string, (string Reason, Func<string, string> Apply)> Corrections = new()
    {
        ["add"] =
        (
            "Add-ResearchNode.ps1 wrote a new node's 'tags' before its 'caveats'. Of the graph's "
          + "51 nodes carrying both, 37 have caveats first -- the hand-authored ones -- and the 14 "
          + "the other way round are the ones this script wrote, one at a time. The port emits "
          + "caveats first, so the golden is corrected rather than freezing an inconsistency the "
          + "original was still spreading.",
            MoveTagsAfterCaveats
        ),
    };

    public static int Main(string[] rawArgs)
    {
        string gitRef = Argument(rawArgs, "--ref") ?? "HEAD";
        bool keepWork = rawArgs.Contains("--keep-work");

        string repoRoot = FindRepoRoot();
        string fixtures = Path.Combine(repoRoot, "tests", "Janet.Tests", "Fixtures");
        string goldens = Argument(rawArgs, "--out")
            ?? Path.Combine(repoRoot, "tests", "Janet.Tests", "Goldens");

        string corpus = Path.Combine(fixtures, "research.json");
        string layout = Path.Combine(fixtures, "layout.json");
        string threadSeed = Path.Combine(fixtures, "threads.json");

        foreach (string required in (string[])[corpus, layout, threadSeed])
        {
            if (!File.Exists(required))
            {
                Console.Error.WriteLine($"fixture missing: {required}");
                return 1;
            }
        }

        string work = Path.Combine(Path.GetTempPath(), "janet-goldens", Guid.NewGuid().ToString("n")[..8]);
        string scripts = Path.Combine(work, "scripts");
        Directory.CreateDirectory(scripts);

        try
        {
            Console.WriteLine($"repo:   {repoRoot}");
            Console.WriteLine($"ref:    {gitRef}");
            Console.WriteLine($"work:   {work}");

            string commit = Extract(repoRoot, gitRef, scripts);
            Console.WriteLine($"commit: {commit}");

            Reset(goldens);

            int queries = WriteQueries(scripts, corpus, goldens, repoRoot);
            int texts = WriteTextViews(scripts, corpus, goldens, repoRoot);
            int writes = WriteWrites(scripts, layout, goldens, work, repoRoot);
            int threads = WriteThreads(scripts, threadSeed, goldens, work, repoRoot);

            WriteMeta(goldens, gitRef, commit, scripts, corpus, layout);

            Console.WriteLine();
            Console.WriteLine(
                $"{queries} query, {texts} text, {writes} write, {threads} thread goldens -> {goldens}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            if (!keepWork && Directory.Exists(work))
            {
                try { Directory.Delete(work, recursive: true); }
                catch (IOException) { /* a leftover temp directory is not worth a failed run */ }
            }
        }
    }

    private static int WriteQueries(string scripts, string corpus, string goldens, string repoRoot)
    {
        string getResearch = Path.Combine(scripts, "Get-Research.ps1");
        string directory = Path.Combine(goldens, "query");
        Directory.CreateDirectory(directory);

        foreach (Cases.Read query in Cases.Queries)
        {
            string output = Ps.Run(getResearch, [.. query.Args, "-Path", corpus, "-NoTrace"], repoRoot);
            Save(Path.Combine(directory, Cases.Slug(query.Label) + ".json"), output);
            Console.WriteLine($"  query  {query.Label}");
        }

        return Cases.Queries.Length;
    }

    private static int WriteTextViews(string scripts, string corpus, string goldens, string repoRoot)
    {
        string getResearch = Path.Combine(scripts, "Get-Research.ps1");
        string directory = Path.Combine(goldens, "text");
        Directory.CreateDirectory(directory);

        foreach (Cases.Read view in Cases.TextViews)
        {
            string output = Ps.Run(getResearch, [.. view.Args, "-Text", "-Path", corpus, "-NoTrace"], repoRoot);
            Save(Path.Combine(directory, Cases.Slug(view.Label) + ".txt"), output);
            Console.WriteLine($"  text   {view.Label}");
        }

        return Cases.TextViews.Length;
    }

    /// <summary>
    /// Each write runs against its own copy of the layout fixture, and the copy itself is the
    /// golden -- the file after the operation, not a description of it.
    /// </summary>
    private static int WriteWrites(string scripts, string layout, string goldens, string work, string repoRoot)
    {
        string directory = Path.Combine(goldens, "write");
        Directory.CreateDirectory(directory);

        foreach (Cases.Write write in Cases.Writes)
        {
            string slug = Cases.Slug(write.Label);
            string sandbox = Path.Combine(work, "write", slug);
            Directory.CreateDirectory(sandbox);

            string graph = Path.Combine(sandbox, "research.json");
            File.Copy(layout, graph);

            Ps.Run(Path.Combine(scripts, write.Script), [.. write.Args, "-GraphPath", graph], repoRoot);

            // Copied as bytes: line endings are part of what byte equality asserts.
            string golden = Path.Combine(directory, slug + ".json");
            File.Copy(graph, golden, overwrite: true);

            Console.WriteLine($"  write  {write.Label}");

            if (Corrections.TryGetValue(slug, out (string Reason, Func<string, string> Apply) correction))
            {
                Correct(golden, correction.Apply);
                Console.WriteLine($"         corrected: {correction.Reason}");
            }
        }

        return Cases.Writes.Length;
    }

    /// <summary>
    /// Runs each thread-item case against a fresh copy of the seed list.
    /// </summary>
    /// <remarks>
    /// Both halves are recorded: the list after the operation, byte for byte, and what the
    /// script printed. The file is the state machine; the stdout is the contract every caller
    /// reads, and Show-ThreadItems' envelope is consumed by startup itself. A port could match
    /// one and be wrong about the other.
    /// </remarks>
    private static int WriteThreads(string scripts, string seed, string goldens, string work, string repoRoot)
    {
        string listDirectory = Path.Combine(goldens, "thread");
        string outputDirectory = Path.Combine(goldens, "thread-output");
        Directory.CreateDirectory(listDirectory);
        Directory.CreateDirectory(outputDirectory);

        foreach (Cases.Write operation in Cases.Threads)
        {
            string slug = Cases.Slug(operation.Label);
            string sandbox = Path.Combine(work, "thread", slug);
            Directory.CreateDirectory(sandbox);

            string list = Path.Combine(sandbox, "thread-stack.json");
            File.Copy(seed, list);

            string printed = Ps.Run(
                Path.Combine(scripts, operation.Script), [.. operation.Args, "-Path", list], repoRoot);

            File.Copy(list, Path.Combine(listDirectory, slug + ".json"), overwrite: true);
            Save(Path.Combine(outputDirectory, slug + ".json"), printed);

            Console.WriteLine($"  thread {operation.Label}");
        }

        return Cases.Threads.Length;
    }

    /// <summary>
    /// Pulls the scripts out of git at the given ref and returns the commit they came from.
    /// </summary>
    /// <remarks>
    /// Through the repo's own git.ps1: git is not on PATH in an agent shell, and that shim is
    /// where the answer to "where is git" already lives.
    /// </remarks>
    private static string Extract(string repoRoot, string gitRef, string destination)
    {
        string git = Path.Combine(repoRoot, "scripts", "git.ps1");

        foreach (string script in OriginalScripts)
        {
            string target = Path.Combine(destination, script);

            // Piped straight to a file inside pwsh, so the content is never decoded and
            // re-encoded across the process boundary on its way to disk.
            Ps.Invoke(
                $"& (& {Ps.Quote(git)}) show {Ps.Quote($"{gitRef}:scripts/{script}")} | " +
                $"Set-Content -LiteralPath {Ps.Quote(target)} -Encoding UTF8",
                repoRoot);

            if (!File.Exists(target) || new FileInfo(target).Length == 0)
            {
                throw new InvalidOperationException($"{script} is not in the tree at {gitRef}");
            }

            if (!NotEntryPoints.Contains(script, StringComparer.Ordinal) &&
                File.ReadLines(target).FirstOrDefault()?.Contains("JANET-SHIM", StringComparison.Ordinal) == true)
            {
                throw new InvalidOperationException(
                    $"{script} at {gitRef} is already a shim over the CLI; goldens taken from it would " +
                    "compare the port with itself. Pass --ref for a commit before the shims landed.");
            }
        }

        return Ps.Invoke($"& (& {Ps.Quote(git)}) rev-parse {Ps.Quote(gitRef)}", repoRoot).Trim();
    }

    private static void WriteMeta(
        string goldens, string gitRef, string commit, string scripts, string corpus, string layout)
    {
        Dictionary<string, string> hashes = [];

        foreach (string script in OriginalScripts)
        {
            hashes[script] = Hash(Path.Combine(scripts, script));
        }

        var meta = new
        {
            note = "Generated by tests/Janet.Goldens. Do not hand-edit except to accept a "
                 + "deliberate behaviour change, and say so in the commit message.",
            gitRef,
            commit,
            scripts = hashes,
            fixtures = new Dictionary<string, string>
            {
                ["research.json"] = Hash(corpus),
                ["layout.json"] = Hash(layout),
            },
            corrections = Corrections.ToDictionary(c => c.Key, c => c.Value.Reason),
        };

        Save(
            Path.Combine(goldens, "meta.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>
    /// Rewrites a golden in place, preserving its bytes' encoding and line endings.
    /// </summary>
    /// <remarks>
    /// A correction that also silently changed CRLF to LF, or added a BOM, would break the byte
    /// equality the write tests assert and look like a writer bug.
    /// </remarks>
    private static void Correct(string path, Func<string, string> apply)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        string text = new UTF8Encoding(false).GetString(bom ? bytes[3..] : bytes);

        File.WriteAllText(path, apply(text), new UTF8Encoding(bom));
    }

    /// <summary>Moves the last node's "tags" line to just after its "caveats" array.</summary>
    private static string MoveTagsAfterCaveats(string text)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = [.. text.Split(newline)];

        int tags = lines.FindLastIndex(l => l.StartsWith("      \"tags\": ", StringComparison.Ordinal));
        int caveats = lines.FindLastIndex(l => l.Equals("      \"caveats\": [", StringComparison.Ordinal));

        if (tags < 0 || caveats < 0 || caveats < tags)
        {
            throw new InvalidOperationException(
                "expected a 'tags' line followed by a 'caveats' array in the added node; the "
              + "correction no longer applies and would have done nothing.");
        }

        int close = lines.FindIndex(caveats, l => l.Equals("      ],", StringComparison.Ordinal));

        if (close < 0 || !lines[tags].EndsWith(','))
        {
            throw new InvalidOperationException("the caveats array or the tags line is not shaped as expected.");
        }

        string moved = lines[tags];
        lines.RemoveAt(tags);
        lines.Insert(close, moved);

        return string.Join(newline, lines);
    }

    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// Empties the golden directory first, so a case removed from the list stops having a
    /// golden rather than leaving one behind that nothing asserts against.
    /// </summary>
    private static void Reset(string goldens)
    {
        if (Directory.Exists(goldens))
        {
            Directory.Delete(goldens, recursive: true);
        }

        Directory.CreateDirectory(goldens);
    }

    /// <summary>
    /// Writes a captured answer with CRLF, matching the repo's file-encoding gate.
    /// </summary>
    /// <remarks>
    /// Normalised rather than passed through: PowerShell's line endings depend on how it was
    /// invoked, and a golden whose endings drift produces a whole-file diff that says nothing.
    /// CRLF because Test-FileEncoding.ps1 fails a bare LF, and a regeneration that leaves the
    /// tree failing its own gate is a regeneration nobody will run.
    /// </remarks>
    private static void Save(string path, string content) =>
        File.WriteAllText(
            path,
            content.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            new UTF8Encoding(false));

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JanetHome.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("could not find JanetHome.slnx above the running assembly");
    }

    private static string? Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
