using System.ComponentModel;
using Janet.Core;
using ModelContextProtocol.Server;

namespace Janet.Mcp;

/// <summary>Bicep, compiled and linted, reported as one structured answer.</summary>
[McpServerToolType]
public static class BicepTools
{
    [McpServerTool(Name = "bicep_check")]
    [Description(
        "Build a .bicep or .bicepparam file and get back every compiler and linter finding as " +
        "structured JSON -- level, code, message, file, line, column -- in ONE envelope with the " +
        "outcome, instead of the compiled template on stdout and a text stream of findings on " +
        "stderr. The linter runs as part of the build, so its rules (no-unused-params and the " +
        "like) arrive beside the compiler's BCP codes; there is no separate lint pass to run. " +
        "'succeeded' is the compiler's own verdict: false exactly when an error stopped it, and " +
        "warnings never fail it. A .bicepparam builds the template it names as well. Needs the " +
        "Bicep CLI on PATH (winget install Microsoft.Bicep), or JANET_BICEP pointing at one.")]
    public static string Check(
        [Description("Path to a .bicep or .bicepparam file.")]
        string path) =>
        BicepJson.Serialize(Bicep.Check(path));
}
