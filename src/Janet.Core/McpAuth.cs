using System.Text.Json;
using System.Text.Json.Nodes;
using Janet.Mcp.Security;

namespace Janet.Core;

/// <summary>
/// The auth setting both local MCP servers are launched with, and this machine's MCP bearer token
/// as the janet side reads it.
/// </summary>
/// <remarks>
/// ONE SETTING, read by every launcher. The mode lives in startup-manifest.json as the top-level
/// <c>mcpAuth</c> key ("require", "report" or "off"). The manifest is the seam because it is
/// already the file that decides how a session's servers come up -- its run entries are the
/// Ensure scripts -- and it sits in the JanetBase every launcher already knows: ServerRotation's
/// targets, ServerLink's start, and Ensure-McpServer.ps1 (which reads the same key, so the
/// PowerShell and C# launchers cannot disagree). Absent, no --auth is passed and each server
/// applies its own default, which for janet-mcp is require.
///
/// The token is read only through janet (decision record: thread item 65). razorgraph-mcp gets it
/// by running <see cref="TokenCommand"/>; Claude Code by running `janet mcp-headers`.
/// </remarks>
public static class McpAuth
{
    /// <summary>The file the setting lives in, relative to JanetBase.</summary>
    public const string ManifestFile = "startup-manifest.json";

    /// <summary>The top-level key in <see cref="ManifestFile"/>.</summary>
    public const string Key = "mcpAuth";

    /// <summary>What razorgraph-mcp is told to run to read the token (--auth-token-command).</summary>
    public const string TokenCommand = "janet mcp-token --print";

    /// <summary>The three values, as the servers' --auth option spells them.</summary>
    public static readonly IReadOnlyList<string> Modes = ["require", "report", "off"];

    /// <summary>Parses an --auth value. Case-insensitive.</summary>
    public static bool TryParseMode(string? value, out BearerMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "require": mode = BearerMode.Require; return true;
            case "report": mode = BearerMode.Report; return true;
            case "off": mode = BearerMode.Off; return true;
            default: mode = BearerMode.Require; return false;
        }
    }

    /// <summary>
    /// The mode <c>&lt;janetBase&gt;\startup-manifest.json</c> declares, lower-cased, or null when
    /// the file or the key is absent.
    /// </summary>
    /// <exception cref="GraphException">The manifest is not JSON, or the value is not one of the three.</exception>
    public static string? ReadMode(string janetBase)
    {
        string path = Path.Combine(janetBase, ManifestFile);
        if (!File.Exists(path))
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new GraphException($"{path} is not valid JSON, so the MCP auth mode ('{Key}') cannot be read: {ex.Message}", ex);
        }

        if (root is not JsonObject manifest || manifest[Key] is not JsonNode node)
        {
            return null;
        }

        string? value = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
        if (!TryParseMode(value, out _))
        {
            throw new GraphException(
                $"{path} sets '{Key}' to {node.ToJsonString()}; it must be one of {string.Join(", ", Modes)}.");
        }

        return value!.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The --auth arguments a launcher appends for <paramref name="janetBase"/>'s setting: empty
    /// when none is declared.
    /// </summary>
    public static IReadOnlyList<string> AuthArguments(string janetBase) =>
        ReadMode(janetBase) is string mode ? ["--auth", mode] : [];

    /// <summary>
    /// This machine's token from HKCU\Software\Janet, or null with the reason when there is none
    /// or it cannot be read. Never throws, and never puts the token in <paramref name="error"/>.
    /// </summary>
    public static string? TryReadToken(out string? error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "the MCP token store is Windows-only (DPAPI under HKCU)";
            return null;
        }

        try
        {
            McpTokenStore store = McpTokenStore.Default;
            string? token = store.Read();
            error = token is null ? $"no MCP token at {store.Location}" : null;
            return token;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
