using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Janet.Core;

/// <summary>One MSBuild diagnostic. Line is null when it was reported against a file rather than a position.</summary>
public sealed record Diagnostic(string File, int? Line, string Severity, string Code, string Message);

/// <summary>One warning code, its instances, and how many instances were not listed.</summary>
public sealed record WarningGroup(string Code, int Count, IReadOnlyList<Diagnostic> Instances, int OmittedInstances);

/// <summary>What the previous -New run saw.</summary>
public sealed record WarningBaseline(int Contract, string Target, string Configuration, string SavedAt, IReadOnlyList<Diagnostic> Warnings);

/// <summary>The result of diffing this run's census against the previous one.</summary>
public sealed record BaselineDiff(IReadOnlyList<Diagnostic> NewWarnings, int ResolvedWarningCount);

/// <summary>
/// Parsing and census work for the build check: turning MSBuild's console output into
/// diagnostics, grouping them, and diffing them against a stored baseline.
/// </summary>
/// <remarks>
/// Separated from the process orchestration deliberately. All of this is a pure function of
/// text, so it is testable without running dotnet at all -- which is what lets the parity
/// goldens be recorded from the original script's own functions rather than from a build.
/// </remarks>
public static class DotnetDiagnostics
{
    /// <summary>
    /// MSBuild diagnostics come in two canonical shapes:
    /// <code>
    /// path(line,col): warning CODE: message [project]
    /// path : warning CODE: message [project]
    /// </code>
    /// </summary>
    /// <remarks>
    /// The [project] suffix is why one physical warning appears once per project that compiles
    /// the file -- the WPF temp project triples them -- so instances are deduplicated ignoring
    /// project. The bare form's separator is " : " with a mandatory space before the colon;
    /// that space is the only thing distinguishing it from the drive colon in an absolute
    /// Windows path.
    /// </remarks>
    private static readonly Regex FileDiagnostic = new(
        @"^(?<file>.+?)\((?<line>\d+),\d+\):\s+(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+):\s+(?<message>.*?)(\s+\[[^\]]+\])?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex BareDiagnostic = new(
        @"^(?<file>.+?)\s+:\s+(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+):\s+(?<message>.*?)(\s+\[[^\]]+\])?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// WPF's compile-time temp project carries a fresh random infix on every build
    /// (App_k0iqikfl_wpftmp.csproj). Left alone it defeats deduplication now and reads as
    /// new-and-resolved on every -New diff; its diagnostics duplicate the source project's,
    /// so stripping the infix folds them into it.
    /// </summary>
    private static readonly Regex WpfTempInfix = new(@"_[a-z0-9]+_wpftmp(?=\.)", RegexOptions.Compiled);

    /// <summary>
    /// The envelope format. Bumped to 4 by the status discriminator; to 5 when tests gained
    /// the runner's verdict (runnerExitCode, abort, per-assembly status) after a crashed test
    /// host summed to a passing run -- notes\test-count-blind-spot.md; to 6 when graph gained
    /// 'via' and 'graphId', because a graph can now live in a RazorGraph server rather than a
    /// file and the envelope has to say which convention answered.
    /// </summary>
    public const int Contract = 6;

    /// <summary>
    /// The baseline file's own format, deliberately NOT the envelope's.
    /// </summary>
    /// <remarks>
    /// The original stamped both from one number, so bumping the envelope silently discarded
    /// every baseline on disk -- they read as wrong-contract, which is treated as absent, and
    /// the first -New run after an upgrade quietly loses its comparison. Nothing about the
    /// baseline file changed when the envelope gained a discriminator, so this stays at 3 and
    /// existing baselines keep working.
    /// </remarks>
    public const int BaselineContract = 3;

    /// <summary>Parses build output into diagnostics, deduplicated on everything but project.</summary>
    public static IReadOnlyList<Diagnostic> Read(IEnumerable<string> lines)
    {
        HashSet<string> seen = [];
        List<Diagnostic> found = [];

        foreach (string line in lines)
        {
            Match match = FileDiagnostic.Match(line);
            if (!match.Success)
            {
                match = BareDiagnostic.Match(line);
            }

            if (!match.Success)
            {
                continue;
            }

            Group lineGroup = match.Groups["line"];
            int? lineNumber = lineGroup.Success ? int.Parse(lineGroup.Value) : null;

            Diagnostic diagnostic = new(
                WpfTempInfix.Replace(match.Groups["file"].Value.Trim(), ""),
                lineNumber,
                match.Groups["severity"].Value,
                match.Groups["code"].Value,
                match.Groups["message"].Value);

            if (seen.Add($"{diagnostic.File}|{diagnostic.Line}|{diagnostic.Code}|{diagnostic.Message}"))
            {
                found.Add(diagnostic);
            }
        }

        return found;
    }

    /// <summary>
    /// Groups warnings by code, listing at most <paramref name="cap"/> instances each and saying
    /// how many it left out.
    /// </summary>
    /// <remarks>
    /// Ties broken by code, which the original did not do: PowerShell's Sort-Object is unstable
    /// unless -Stable is passed, so two codes with the same count came back in whatever order
    /// Array.Sort left them. A reader works down this list, and an order that shifts when an
    /// unrelated warning appears is worse than a boring one.
    /// </remarks>
    public static IReadOnlyList<WarningGroup> Group(IEnumerable<Diagnostic> warnings, int cap = 8)
    {
        return
        [
            .. warnings
                .GroupBy(w => w.Code)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.InvariantCultureIgnoreCase)
                .Select(g => new WarningGroup(
                    g.Key,
                    g.Count(),
                    [.. g.Take(cap)],
                    Math.Max(0, g.Count() - cap)))
        ];
    }

    // ---- the baseline ------------------------------------------------------------------------

    /// <summary>
    /// One baseline per target and configuration: Release and Debug censuses differ legitimately
    /// and must not be diffed against each other.
    /// </summary>
    public static string BaselinePath(string resolvedTarget, string configuration)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{resolvedTarget}|{configuration}".ToLowerInvariant()));
        string hex = Convert.ToHexString(digest)[..12].ToLowerInvariant();
        string stem = Path.GetFileNameWithoutExtension(resolvedTarget);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Janet",
            "dotnet-check",
            $"{stem}-{hex}.json");
    }

    /// <summary>
    /// The previous census, or null when absent, unreadable, or stamped with a different
    /// contract. A stale format reads as no baseline, never as a wrong comparison.
    /// </summary>
    public static WarningBaseline? ReadBaseline(string path, int contract)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (root is not JsonObject stored ||
            !stored.TryGetPropertyValue("contract", out JsonNode? stamped) ||
            stamped?.GetValue<int>() != contract ||
            !stored.TryGetPropertyValue("warnings", out JsonNode? warnings) ||
            warnings is not JsonArray listed)
        {
            return null;
        }

        List<Diagnostic> parsed = [];
        foreach (JsonNode? entry in listed)
        {
            if (entry is not JsonObject warning)
            {
                continue;
            }

            int? line = warning["line"] is JsonValue value && value.TryGetValue(out int parsedLine)
                ? parsedLine
                : null;

            parsed.Add(new Diagnostic(
                Text(warning, "file"),
                line,
                "warning",
                Text(warning, "code"),
                Text(warning, "message")));
        }

        return new WarningBaseline(
            contract,
            Text(stored, "target"),
            Text(stored, "configuration"),
            Text(stored, "savedAt"),
            parsed);
    }

    /// <summary>
    /// The key a warning is recognised by across runs.
    /// </summary>
    /// <remarks>
    /// Line is deliberately excluded: an edit that merely moves a warning must not resurrect it
    /// as new. The cost is that the same message twice in one file merges to one key, which is
    /// the cheaper of the two mistakes.
    /// </remarks>
    public static string Key(Diagnostic warning) =>
        $"{warning.File.ToLowerInvariant()}|{warning.Code}|{warning.Message}";

    public static BaselineDiff Compare(IReadOnlyList<Diagnostic> current, WarningBaseline baseline)
    {
        HashSet<string> prior = [.. baseline.Warnings.Select(Key)];
        HashSet<string> now = [];
        List<Diagnostic> fresh = [];

        foreach (Diagnostic warning in current)
        {
            string key = Key(warning);
            now.Add(key);

            if (!prior.Contains(key))
            {
                fresh.Add(warning);
            }
        }

        return new BaselineDiff(fresh, prior.Count(k => !now.Contains(k)));
    }

    public static void SaveBaseline(string path, int contract, string target, string configuration, IReadOnlyList<Diagnostic> warnings, string savedAt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        JsonArray stored = [];
        foreach (Diagnostic warning in warnings)
        {
            stored.Add(new JsonObject
            {
                ["file"] = warning.File,
                ["line"] = warning.Line,
                ["code"] = warning.Code,
                ["message"] = warning.Message,
            });
        }

        JsonObject root = new()
        {
            ["contract"] = contract,
            ["target"] = target,
            ["configuration"] = configuration,
            ["savedAt"] = savedAt,
            ["warnings"] = stored,
        };

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Text(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out JsonNode? value) ? value?.GetValue<string>() ?? "" : "";
}
