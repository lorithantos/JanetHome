using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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
    /// file and the envelope has to say which convention answered; to 7 when the per-assembly
    /// status gained "empty" and tests gained time and provenance -- durationSeconds,
    /// testTimeSeconds, slowest[], resultsDirectory, and per-assembly resultsFile.
    /// <para>
    /// 7 is the correction to 5 as much as an addition. 5's abort detection asked four
    /// independent questions and OR'd them, and three of the four fire on healthy runs, so
    /// every failing suite was labelled aborted and every filter that matched nothing was
    /// labelled a crash. The rule is now positive evidence only; DotnetTests.Classify carries
    /// the measurements. The time and the TRX path are there for the other half of the same
    /// complaint: sessions were bypassing this envelope and re-running dotnet test themselves
    /// to learn things the run had already written down.
    /// </para>
    /// <para>
    /// 8 adds build.diagnosis (2026-09-16), and it is the envelope half of a correctness fix:
    /// dotnet's children no longer inherit a Platform variable (DotnetCheck.StartInfo). The
    /// fix alone would have left the envelope no better at saying WHY a build stopped before
    /// restore. An inherited Platform=x64 had made every non-x64 solution on a machine fail
    /// MSB4126 in 0.3 seconds, and the envelope reported that error verbatim -- precise,
    /// confident, and about a property the tool never passed. diagnosis carries the
    /// provenance: what the tool passed, what it removed from the environment, what the target
    /// declares, and that restore never ran, so errors[] is not a census.
    /// </para>
    /// </summary>
    public const int Contract = 8;

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

    // ---- the diagnosis ------------------------------------------------------------------------

    /// <summary>
    /// MSBuild's code for a solution configuration the target does not declare. It is reported
    /// against the solution, before restore, so a build that fails with it has not looked at
    /// the repository at all.
    /// </summary>
    public const string SolutionConfigurationInvalid = "MSB4126";

    /// <summary>The Configuration|Platform pair MSB4126 names, quoted in its message.</summary>
    /// <remarks>
    /// The pipe is what distinguishes it from the other quoted string in the same message --
    /// MSBuild's own advice suggests /p:Platform="Any CPU" -- so the pattern requires one.
    /// </remarks>
    private static readonly Regex RejectedPair = new(
        @"""(?<configuration>[^""|]*)\|(?<platform>[^""]*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// A sentence naming the provenance of a failure whose cause lies outside the target, or
    /// null when nothing here recognises the errors.
    /// </summary>
    /// <remarks>
    /// A pure function of text, like the rest of this file's parsing half: no process, no file
    /// system, so it is testable against a captured error and nothing else. The platforms the
    /// target declares are READ BY THE CALLER and passed in (<see cref="DeclaredPlatforms"/>),
    /// which is the only part that needs a disk.
    /// <para>
    /// Null means there is nothing to say, never that the build was fine. The envelope depends
    /// on that reading, so a default sentence would be worse than silence: a reader who sees a
    /// diagnosis on every failure stops reading them.
    /// </para>
    /// </remarks>
    /// <param name="errors">The build's errors, as parsed by <see cref="Read"/>.</param>
    /// <param name="target">The resolved target, named in the diagnosis by its file name.</param>
    /// <param name="platformPassed">The platform the tool put on the command line, or null --
    /// which is always, until a platform parameter exists.</param>
    /// <param name="platformScrubbed">The Platform variable removed from the child's
    /// environment, or null when this process carried none.</param>
    /// <param name="declaredPlatforms">What the target declares; null when it was not read.</param>
    public static string? Diagnose(
        IReadOnlyList<Diagnostic> errors,
        string target,
        string? platformPassed,
        string? platformScrubbed,
        IReadOnlyList<string>? declaredPlatforms = null)
    {
        Diagnostic? rejected = errors.FirstOrDefault(e =>
            string.Equals(e.Code, SolutionConfigurationInvalid, StringComparison.OrdinalIgnoreCase));

        if (rejected is null)
        {
            return null;
        }

        string name = Path.GetFileName(target);
        if (string.IsNullOrEmpty(name))
        {
            name = target;
        }

        Match pair = RejectedPair.Match(rejected.Message);
        List<string> said =
        [
            pair.Success
                ? $"{SolutionConfigurationInvalid}: {name} does not declare the solution configuration \"{pair.Groups["configuration"].Value}|{pair.Groups["platform"].Value}\" -- configuration {pair.Groups["configuration"].Value}, platform {pair.Groups["platform"].Value}."
                : $"{SolutionConfigurationInvalid}: {name} does not declare the solution configuration MSBuild was asked for.",

            platformPassed is null
                ? "The check passed NO platform: its command line carries the configuration and nothing else, so the platform came from somewhere else."
                : $"The check passed -p:Platform={platformPassed} on the command line.",

            platformScrubbed is null
                ? $"Its own environment carried no {DotnetCheck.ScrubbedVariable} variable to remove."
                : $"Its own environment carried {DotnetCheck.ScrubbedVariable}={platformScrubbed}, inherited from the shell that started this process, and the check REMOVED it from dotnet's environment before building, so it is not the source of the pair above -- MSBuild promotes environment variables to global properties, which is how a prompt that ran vcvars64.bat once set the platform for every build started from it.",
        ];

        if (declaredPlatforms is not null)
        {
            said.Add(declaredPlatforms.Count == 0
                ? $"{name} declares no platforms of its own, so MSBuild picks the default (Any CPU)."
                : $"{name} declares: {string.Join(", ", declaredPlatforms)}.");
        }

        said.Add("RESTORE NEVER RAN -- this error stops the build before it -- so build.errors[] is not a census of what is wrong with this target.");

        return string.Join(" ", said);
    }

    /// <summary>
    /// The platforms a .sln or .slnx declares, in the order it declares them. Empty for a
    /// target that declares none, and for a .csproj, which has no solution configurations and
    /// accepts any platform it is handed.
    /// </summary>
    /// <remarks>
    /// The impure half of <see cref="Diagnose"/>, kept apart from it so the sentence stays a
    /// function of text. Deliberately a reader and not a chooser: MSBuild resolves its own
    /// default perfectly well when nothing sets Platform, and re-implementing that resolution
    /// would be a new way to produce a confident wrong platform. This exists to NAME what the
    /// target declares, nothing more. An unreadable or malformed file reads as "declares
    /// none": a diagnosis is best effort and must never fail a build.
    /// </remarks>
    public static IReadOnlyList<string> DeclaredPlatforms(string target)
    {
        try
        {
            string extension = Path.GetExtension(target);

            if (string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
            {
                return
                [
                    .. XDocument.Load(target)
                        .Descendants()
                        .Where(e => e.Name.LocalName == "Platform")
                        .Select(e => e.Attribute("Name")?.Value)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => n!)
                        .Distinct(StringComparer.Ordinal)
                ];
            }

            if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase))
            {
                return [.. SolutionPlatforms(File.ReadLines(target))];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Best effort by design: see the remarks.
        }

        return [];
    }

    /// <summary>
    /// The platform half of each entry in a .sln's SolutionConfigurationPlatforms section,
    /// where a line reads "Debug|Any CPU = Debug|Any CPU".
    /// </summary>
    private static IEnumerable<string> SolutionPlatforms(IEnumerable<string> lines)
    {
        bool inside = false;
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("GlobalSection(SolutionConfigurationPlatforms)", StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }

            if (!inside)
            {
                continue;
            }

            if (trimmed.StartsWith("EndGlobalSection", StringComparison.Ordinal))
            {
                yield break;
            }

            int equals = trimmed.IndexOf('=', StringComparison.Ordinal);
            string left = equals < 0 ? trimmed : trimmed[..equals];
            int pipe = left.IndexOf('|', StringComparison.Ordinal);

            if (pipe < 0)
            {
                continue;
            }

            string platform = left[(pipe + 1)..].Trim();

            if (platform.Length > 0 && seen.Add(platform))
            {
                yield return platform;
            }
        }
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
