using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>
/// Reads the port a client has been told to dial, from the .mcp.json that told it.
/// </summary>
/// <remarks>
/// The config file is the declaration. A server that derives its own port instead is inventing
/// a second source of truth for something already written down, and the two only ever agree by
/// luck -- the failure being a client dialling an address nothing answers on, with no error.
///
/// So: the port comes from the config where a config exists. Derivation survives only as a
/// bootstrap suggestion for writing the first config, which is the one moment nothing has
/// declared anything yet.
/// </remarks>
public static class McpConfig
{
    public const string FileName = ".mcp.json";

    /// <summary>
    /// Finds the port declared for an http/sse server in <c>&lt;basePath&gt;/.mcp.json</c>.
    /// </summary>
    /// <param name="serverName">Preferred entry name; any url-bearing entry is used otherwise.</param>
    public static bool TryReadPort(string basePath, string serverName, out int port, out string? source)
    {
        port = 0;
        source = null;

        string path = Path.Combine(basePath, FileName);
        if (!File.Exists(path))
        {
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed config is the user's to fix; it must not stop the server starting.
            return false;
        }

        if (root?["mcpServers"] is not JsonObject servers)
        {
            return false;
        }

        // The named entry wins; otherwise the first that declares a url. An entry with no url
        // is a stdio server, which has no port to read.
        IEnumerable<KeyValuePair<string, JsonNode?>> ordered = servers
            .OrderByDescending(e => string.Equals(e.Key, serverName, StringComparison.OrdinalIgnoreCase));

        foreach ((string name, JsonNode? entry) in ordered)
        {
            if (entry?["url"]?.GetValue<string>() is not string url)
            {
                continue;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && parsed.Port > 0)
            {
                port = parsed.Port;
                source = $"{path} ({name})";
                return true;
            }
        }

        return false;
    }
}
