using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// Serializes a check in the format declared by contracts\dotnet-check.schema.json.
/// </summary>
/// <remarks>
/// Contract 4, a tagged union on 'status'. The discriminator is not decoration: a caller that
/// has not read it has no business reading anything else, because the running arm carries a
/// handle and none of the answer.
///
/// Three fields mean something specific when null, and the schema says so in prose because JSON
/// Schema cannot: newWarnings null is NO COMPARISON HAPPENED rather than none were new, tests
/// null is NOT RUN rather than a suite with no tests, and graph null is NOT APPLICABLE rather
/// than a graph that is missing.
/// </remarks>
public static class DotnetCheckJson
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions Indented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string Serialize(CheckResult result, bool pretty = false)
    {
        JsonObject root = new()
        {
            ["status"] = "complete",
            ["contract"] = DotnetDiagnostics.Contract,
            ["target"] = result.Target,
            ["configuration"] = result.Configuration,
            ["succeeded"] = result.Succeeded,
            ["build"] = Build(result.Build),
            ["tests"] = Tests(result.Tests),
            ["graph"] = Graph(result.Graph),
        };

        return root.ToJsonString(pretty ? Indented : Compact);
    }

    public static string Serialize(CheckPending pending, bool pretty = false)
    {
        JsonObject root = new()
        {
            ["status"] = "running",
            ["contract"] = DotnetDiagnostics.Contract,
            ["target"] = pending.Target,
            ["configuration"] = pending.Configuration,
            ["handle"] = pending.Handle,
            ["startedAt"] = pending.StartedAt,
            ["elapsedSeconds"] = pending.ElapsedSeconds,
        };

        return root.ToJsonString(pretty ? Indented : Compact);
    }

    private static JsonObject Build(BuildReport build)
    {
        JsonArray errors = [];
        foreach (Diagnostic error in build.Errors)
        {
            errors.Add(Diagnostic(error));
        }

        JsonArray warnings = [];
        foreach (WarningGroup group in build.Warnings)
        {
            JsonArray instances = [];
            foreach (Diagnostic instance in group.Instances)
            {
                instances.Add(new JsonObject
                {
                    ["file"] = instance.File,
                    ["line"] = instance.Line,
                    ["message"] = instance.Message,
                });
            }

            warnings.Add(new JsonObject
            {
                ["code"] = group.Code,
                ["count"] = group.Count,
                ["instances"] = instances,
                ["omittedInstances"] = group.OmittedInstances,
            });
        }

        JsonNode? newWarnings = null;
        if (build.NewWarnings is not null)
        {
            JsonArray fresh = [];
            foreach (Diagnostic warning in build.NewWarnings)
            {
                fresh.Add(Diagnostic(warning));
            }

            newWarnings = fresh;
        }

        return new JsonObject
        {
            ["succeeded"] = build.Succeeded,
            ["durationSeconds"] = build.DurationSeconds,
            ["errors"] = errors,
            ["warnings"] = warnings,
            ["warningCount"] = build.WarningCount,
            ["newWarnings"] = newWarnings,
            ["resolvedWarningCount"] = build.ResolvedWarningCount,
            ["baseline"] = build.Baseline is null
                ? null
                : new JsonObject
                {
                    ["path"] = build.Baseline.Path,
                    ["comparedTo"] = build.Baseline.ComparedTo,
                    ["saved"] = build.Baseline.Saved,
                },
        };
    }

    private static JsonObject Diagnostic(Diagnostic diagnostic) => new()
    {
        ["file"] = diagnostic.File,
        ["line"] = diagnostic.Line,
        ["code"] = diagnostic.Code,
        ["message"] = diagnostic.Message,
    };

    private static JsonNode? Tests(TestRun? tests)
    {
        if (tests is null)
        {
            return null;
        }

        JsonArray failures = [];
        foreach (TestFailure failure in tests.Failures)
        {
            JsonArray stack = [];
            foreach (string frame in failure.Stack)
            {
                stack.Add(frame);
            }

            failures.Add(new JsonObject
            {
                ["test"] = failure.Test,
                ["message"] = failure.Message,
                ["stack"] = stack,
            });
        }

        JsonArray assemblies = [];
        foreach (TestAssembly assembly in tests.Assemblies)
        {
            assemblies.Add(new JsonObject
            {
                ["name"] = assembly.Name,
                ["total"] = assembly.Total,
                ["passed"] = assembly.Passed,
                ["failed"] = assembly.Failed,
                ["skipped"] = assembly.Skipped,
            });
        }

        return new JsonObject
        {
            ["succeeded"] = tests.Succeeded,
            ["total"] = tests.Total,
            ["passed"] = tests.Passed,
            ["failed"] = tests.Failed,
            ["skipped"] = tests.Skipped,
            ["failures"] = failures,
            ["assemblies"] = assemblies,
        };
    }

    private static JsonNode? Graph(GraphState? graph) => graph is null
        ? null
        : new JsonObject
        {
            ["path"] = graph.Path,
            ["builtAt"] = graph.BuiltAt,
            ["newestSourceAt"] = graph.NewestSourceAt,
            ["status"] = graph.Status,
            ["refreshed"] = graph.Refreshed,
            ["canRefresh"] = graph.CanRefresh,
        };

    // ---- the reading view ---------------------------------------------------------------

    public static string Render(CheckResult result)
    {
        StringBuilder text = new();
        text.AppendLine($"{(result.Succeeded ? "PASS" : "FAIL")}  {result.Target} ({result.Configuration})");

        BuildReport build = result.Build;
        text.AppendLine($"build {(build.Succeeded ? "succeeded" : "FAILED")} in {build.DurationSeconds}s, {build.WarningCount} warning(s)");

        foreach (Diagnostic error in build.Errors)
        {
            text.AppendLine($"  error {error.Code}: {error.Message} ({error.File}:{error.Line})");
        }

        foreach (WarningGroup group in build.Warnings)
        {
            text.AppendLine($"  warning {group.Code} x{group.Count}");
        }

        if (build.NewWarnings is not null)
        {
            text.AppendLine($"new warnings: {build.NewWarnings.Count}, resolved: {build.ResolvedWarningCount}");

            foreach (Diagnostic fresh in build.NewWarnings)
            {
                text.AppendLine($"  NEW {fresh.Code}: {fresh.Message} ({fresh.File}:{fresh.Line})");
            }
        }

        if (result.Tests is null)
        {
            text.AppendLine("tests: not run");

            return text.ToString();
        }

        TestRun tests = result.Tests;
        text.AppendLine($"tests: {tests.Passed}/{tests.Total} passed, {tests.Failed} failed, {tests.Skipped} skipped");

        foreach (TestFailure failure in tests.Failures)
        {
            text.AppendLine($"  FAIL {failure.Test}");
            text.AppendLine($"       {failure.Message}");

            foreach (string frame in failure.Stack)
            {
                text.AppendLine($"       {frame}");
            }
        }

        return text.ToString();
    }

    public static string Render(CheckPending pending) =>
        $"RUNNING  {pending.Target} ({pending.Configuration}), {pending.ElapsedSeconds}s so far." + Environment.NewLine +
        $"Poll with the handle: {pending.Handle}" + Environment.NewLine;
}
