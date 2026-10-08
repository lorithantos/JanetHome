using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>Serializes a Bicep check, and renders it for a terminal.</summary>
public static class BicepJson
{
    internal static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    internal static readonly JsonSerializerOptions Indented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string Serialize(BicepCheckResult result, bool pretty = false)
    {
        JsonObject root = new()
        {
            ["contract"] = result.Contract,
            ["path"] = result.Path,
            ["kind"] = result.Kind,
            ["succeeded"] = result.Succeeded,
            ["errors"] = result.Errors,
            ["warnings"] = result.Warnings,
            ["notes"] = result.Notes,
            ["diagnostics"] = Diagnostics(result.Diagnostics),
            ["bicepVersion"] = result.BicepVersion,
            ["durationSeconds"] = result.DurationSeconds,
        };

        return root.ToJsonString(pretty ? Indented : Compact);
    }

    /// <summary>The diagnostic list, shared with the deployment envelopes so all three spell it the same way.</summary>
    internal static JsonArray Diagnostics(IReadOnlyList<BicepDiagnostic> diagnostics) =>
        [.. diagnostics.Select(d => (JsonNode)new JsonObject
        {
            ["level"] = d.Level,
            ["code"] = d.Code,
            ["message"] = d.Message,
            ["file"] = d.File,
            ["line"] = d.Line,
            ["column"] = d.Column,
        })];

    public static string Render(BicepCheckResult result)
    {
        StringBuilder text = new();
        text.AppendLine($"{(result.Succeeded ? "builds" : "DOES NOT BUILD")}  {result.Path}");
        text.AppendLine($"  {result.Errors} error(s), {result.Warnings} warning(s), {result.Notes} note(s)  [{result.BicepVersion}, {result.DurationSeconds}s]");
        AppendDiagnostics(text, result.Diagnostics);
        return text.ToString();
    }

    internal static void AppendDiagnostics(StringBuilder text, IReadOnlyList<BicepDiagnostic> diagnostics)
    {
        foreach (BicepDiagnostic d in diagnostics)
        {
            string where = d.Line is null ? string.Empty : $"{Path.GetFileName(d.File)}:{d.Line}:{d.Column} ";
            text.AppendLine($"  {d.Level,-7} {d.Code}  {where}{d.Message}");
        }
    }
}
