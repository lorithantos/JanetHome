using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>One finding from the Bicep compiler or its linter.</summary>
/// <param name="Level">error, warning or note -- SARIF's vocabulary, which is what Bicep emits.</param>
/// <param name="Code">The rule: a BCPnnn compiler code, or a linter rule name such as no-unused-params.</param>
/// <param name="Column">1-based, in UTF-16 code units, as Bicep reports it.</param>
public sealed record BicepDiagnostic(string Level, string Code, string Message, string? File, int? Line, int? Column);

/// <summary>What a Bicep build said about one file.</summary>
public sealed record BicepCheckResult
{
    /// <summary>Format version. See contracts\bicep-check.schema.json.</summary>
    public const int ContractVersion = 1;

    public int Contract => ContractVersion;

    public required string Path { get; init; }

    /// <summary>template for a .bicep, parameters for a .bicepparam (which builds its template too).</summary>
    public required string Kind { get; init; }

    /// <summary>True when the build produced output: no error-level diagnostic. Warnings do not fail it.</summary>
    public required bool Succeeded { get; init; }

    public required int Errors { get; init; }

    public required int Warnings { get; init; }

    public required int Notes { get; init; }

    public required IReadOnlyList<BicepDiagnostic> Diagnostics { get; init; }

    public required string BicepVersion { get; init; }

    public required double DurationSeconds { get; init; }
}

/// <summary>
/// A compile for deployment: the ARM template and parameter values when it built, and what the
/// compiler said either way.
/// </summary>
/// <remarks>
/// A template that does not compile is a RESULT here, not an exception: its diagnostics travel
/// in the same envelope as everything else the caller asked for, so a failed what-if reads as
/// "these three errors, at these lines" in fields rather than as a message to be re-parsed.
/// </remarks>
/// <param name="Template">Null exactly when the compile failed.</param>
/// <param name="Parameters">The inner parameters object -- name to { value } -- ready for a deployment body. Empty when the compile failed.</param>
public sealed record BicepCompiled(
    string Path,
    bool Succeeded,
    IReadOnlyList<BicepDiagnostic> Diagnostics,
    JsonObject? Template,
    JsonObject Parameters);

/// <summary>
/// The Bicep CLI, run as a child process and read as SARIF rather than scraped as text.
/// </summary>
/// <remarks>
/// WHY THE CLI: Bicep has no supported in-process compiler API. The CLI is the product, it is
/// already what `az` itself shells out to, and the winget install puts it on PATH.
///
/// WHY SARIF: the default diagnostic format is one line per finding with the path, position and
/// message run together, and a message can itself contain parentheses and colons. SARIF is the
/// same information already separated into fields. It goes to STDERR, while --stdout sends the
/// compiled JSON to stdout, so one run yields both without a temporary file.
///
/// The linter runs as part of build: its rules arrive as ordinary diagnostics (no-unused-params
/// and the like) beside the compiler's BCP codes, so a separate `bicep lint` pass would only
/// repeat them.
/// </remarks>
public static class Bicep
{
    /// <summary>How long one compile may take before it is abandoned.</summary>
    /// <remarks>
    /// Declared rather than inherited, for the reason AzTokenCache.ProcessTimeout gives: a child
    /// process that hangs produces no output and no diagnosis. A large template compiles in a
    /// few seconds; restoring registry modules can take longer, which is what the margin is for.
    /// </remarks>
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The executable. Overridable so tests and unusual installs can point elsewhere.</summary>
    public static string Executable { get; set; } =
        Environment.GetEnvironmentVariable("JANET_BICEP") is { Length: > 0 } configured ? configured : "bicep";

    public static BicepCheckResult Check(string path)
    {
        string full = RequireFile(path);
        bool parameters = IsParameters(full);
        Stopwatch clock = Stopwatch.StartNew();

        ProcessCapture capture = Run(parameters ? "build-params" : "build", full, "--stdout", "--diagnostics-format", "sarif");
        IReadOnlyList<BicepDiagnostic> diagnostics = ParseSarif(capture.StandardError);

        return new BicepCheckResult
        {
            Path = full,
            Kind = parameters ? "parameters" : "template",

            // The exit code, not the diagnostic count, decides. Bicep exits non-zero exactly
            // when it produced no output; a SARIF document that failed to parse would otherwise
            // read as a clean build with nothing to say.
            Succeeded = capture.ExitCode == 0,
            Errors = diagnostics.Count(d => d.Level == "error"),
            Warnings = diagnostics.Count(d => d.Level == "warning"),
            Notes = diagnostics.Count(d => d.Level == "note"),
            Diagnostics = diagnostics,
            BicepVersion = Version(),
            DurationSeconds = Math.Round(clock.Elapsed.TotalSeconds, 2),
        };
    }

    /// <summary>
    /// Compiles a .bicepparam (with the template it names) or a bare .bicep into what an ARM
    /// deployment body carries.
    /// </summary>
    /// <param name="overrides">name=value pairs laid over the file's parameters. A value that
    /// parses as JSON is taken as JSON (numbers, booleans, arrays, objects); anything else is a
    /// string. That is the rule `az deployment ... -p name=value` follows.</param>
    public static BicepCompiled Compile(string path, IReadOnlyList<string>? overrides = null)
    {
        string full = RequireFile(path);
        bool parameters = IsParameters(full);

        ProcessCapture capture = Run(parameters ? "build-params" : "build", full, "--stdout", "--diagnostics-format", "sarif");
        IReadOnlyList<BicepDiagnostic> diagnostics = ParseSarif(capture.StandardError);

        if (capture.ExitCode != 0)
        {
            // A failed exit with nothing parsed would otherwise be a failure with no reason
            // attached. Whatever bicep wrote becomes the one diagnostic.
            return new BicepCompiled(
                full,
                false,
                diagnostics.Count > 0
                    ? diagnostics
                    : [new BicepDiagnostic("error", "(bicep)", capture.StandardError.Trim(), full, null, null)],
                null,
                []);
        }

        JsonObject template;
        JsonObject values = [];

        if (parameters)
        {
            // build-params --stdout prints { parametersJson, templateJson, templateSpecId }, each
            // of the first two a JSON document held as a STRING, so each is parsed a second time.
            JsonObject envelope = ParseObject(capture.StandardOutput, "bicep build-params output");
            template = ParseObject(envelope["templateJson"]?.GetValue<string>(), "templateJson");
            JsonObject parameterFile = ParseObject(envelope["parametersJson"]?.GetValue<string>(), "parametersJson");
            values = parameterFile["parameters"] as JsonObject ?? [];

            // Detached so the object can be re-parented into a deployment body later.
            parameterFile.Remove("parameters");
        }
        else
        {
            template = ParseObject(capture.StandardOutput, "bicep build output");
        }

        foreach (string pair in overrides ?? [])
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                throw new GraphException($"Override '{pair}' is not name=value.");
            }

            string name = pair[..equals].Trim();
            string raw = pair[(equals + 1)..];
            values[name] = new JsonObject { ["value"] = OverrideValue(raw) };
        }

        return new BicepCompiled(full, true, diagnostics, template, values);
    }

    /// <summary>Reads Bicep's SARIF into diagnostics. Exposed for the tests, which feed it recorded output.</summary>
    /// <remarks>
    /// A result with no level is a WARNING: that is SARIF's default, and Bicep relies on it --
    /// linter findings carry no level at all, while compiler errors say "error" explicitly.
    /// Empty or non-SARIF text yields no diagnostics rather than throwing, because the exit code
    /// is what decides success; the diagnostics only explain it.
    /// </remarks>
    public static IReadOnlyList<BicepDiagnostic> ParseSarif(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0)
        {
            return [];
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text[start..]);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }

        List<BicepDiagnostic> found = [];

        foreach (JsonNode? run in root?["runs"] as JsonArray ?? [])
        {
            foreach (JsonNode? result in run?["results"] as JsonArray ?? [])
            {
                if (result is null)
                {
                    continue;
                }

                JsonNode? location = (result["locations"] as JsonArray)?.FirstOrDefault()?["physicalLocation"];
                JsonNode? region = location?["region"];

                found.Add(new BicepDiagnostic(
                    Level: result["level"]?.GetValue<string>() ?? "warning",
                    Code: result["ruleId"]?.GetValue<string>() ?? "(no rule)",
                    Message: result["message"]?["text"]?.GetValue<string>() ?? string.Empty,
                    File: FilePath(location?["artifactLocation"]?["uri"]?.GetValue<string>()),
                    Line: region?["startLine"]?.GetValue<int>(),
                    Column: region?["charOffset"]?.GetValue<int>()));
            }
        }

        return found;
    }

    private static string? FilePath(string? uri) =>
        uri is not null && Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.IsFile
            ? parsed.LocalPath
            : uri;

    private static JsonNode? OverrideValue(string raw)
    {
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(raw);
        }
    }

    private static JsonObject ParseObject(string? text, string what)
    {
        try
        {
            return JsonNode.Parse(text ?? string.Empty) as JsonObject
                ?? throw new GraphException($"{what} is not a JSON object.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new GraphException($"{what} does not parse: {ex.Message}", ex);
        }
    }

    private static bool IsParameters(string path) =>
        path.EndsWith(".bicepparam", StringComparison.OrdinalIgnoreCase);

    private static string RequireFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new GraphException("Pass the path of a .bicep or .bicepparam file.");
        }

        string full = System.IO.Path.GetFullPath(path);

        if (!File.Exists(full))
        {
            throw new GraphException($"No such file: {full}");
        }

        if (!full.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase) && !IsParameters(full))
        {
            throw new GraphException($"{full} is neither a .bicep nor a .bicepparam file.");
        }

        return full;
    }

    private static string Version()
    {
        ProcessCapture capture = Run("--version");
        return capture.StandardOutput.Trim() is { Length: > 0 } text ? text : "(unknown)";
    }

    private static ProcessCapture Run(params string[] arguments)
    {
        ProcessStartInfo info = new(Executable);
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using CancellationTokenSource timeout = new(ProcessTimeout);
        try
        {
            return ProcessOutput.Capture(info, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new GraphException($"bicep {string.Join(' ', arguments)} did not finish within {ProcessTimeout.TotalSeconds:0}s and was stopped.");
        }
        catch (GraphException ex)
        {
            throw new GraphException(
                $"{ex.Message} The Bicep CLI is installed with 'winget install Microsoft.Bicep'; set JANET_BICEP to use one elsewhere.",
                ex);
        }
    }
}
