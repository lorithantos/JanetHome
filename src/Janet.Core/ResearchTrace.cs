using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// Leaves a trace that the catalog was consulted, so Invoke-ResearchGuard.ps1 can tell
/// "asked and found nothing" from "never asked".
/// </summary>
/// <remarks>
/// The two look identical from the outside, and only one of them is a mistake. Get-Research.ps1
/// has written this file since the guard was added; the port did not, which meant a session
/// that queried through the CLI or MCP and then wrote a new script was blocked by a guard
/// insisting it had never asked. An ENFORCED rule that silently stops enforcing is worse than
/// one that was never claimed.
///
/// Best-effort by design: a retrieval tool must never fail because a temp file could not be
/// written, and the guard treats a missing trace as "not consulted" anyway.
///
/// The file is shared by every session on the machine, so one session's query clears another's
/// guard. That is inherited from the PowerShell and accepted rather than solved: the check is a
/// prompt to think, not a security boundary.
/// </remarks>
public static class ResearchTrace
{
    private const int Keep = 10;

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string Path =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "janet-research-trace.json");

    /// <summary>Describes what was asked, in the same shape Get-Research.ps1 records.</summary>
    public static string Describe(CatalogQueryOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Query)) { return options.Query; }
        if (options.Tag.Count > 0) { return "tag:" + string.Join(',', options.Tag); }
        if (options.Id.Count > 0) { return "id:" + string.Join(',', options.Id); }
        if (!string.IsNullOrWhiteSpace(options.Kind)) { return "kind:" + options.Kind; }
        return "orientation";
    }

    public static void Record(CatalogQueryOptions options) => Record(Describe(options));

    public static void Record(string asked)
    {
        try
        {
            string nowUtc = DateTime.UtcNow.ToString("o");

            JsonArray recent = [];
            recent.Add(new JsonObject { ["t"] = nowUtc, ["q"] = asked });

            if (File.Exists(Path) &&
                JsonNode.Parse(File.ReadAllText(Path)) is JsonObject existing &&
                existing["recent"] is JsonArray previous)
            {
                foreach (JsonNode? entry in previous.Take(Keep - 1))
                {
                    recent.Add(entry?.DeepClone());
                }
            }

            JsonObject trace = new()
            {
                ["lastUtc"] = nowUtc,
                ["recent"] = recent,
            };

            File.WriteAllText(Path, trace.ToJsonString(Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Deliberately swallowed. A query that failed because the temp directory was
            // read-only would be a worse tool than one whose guard occasionally re-fires.
        }
    }
}
