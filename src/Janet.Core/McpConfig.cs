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
        if (ReadServers(path) is not JsonObject servers)
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

    /// <summary>
    /// The url declared for ONE named http server in <c>&lt;basePath&gt;/.mcp.json</c>.
    /// </summary>
    /// <remarks>
    /// Exact name only, unlike <see cref="TryReadPort"/>: this answers "does this repository
    /// declare a razorgraph server", and falling back to whichever entry has a url would answer
    /// yes for a repository that declares only janet.
    /// </remarks>
    public static bool TryReadUrl(string basePath, string serverName, out Uri? url)
    {
        url = null;

        if (ReadServers(Path.Combine(basePath, FileName)) is not JsonObject servers)
        {
            return false;
        }

        foreach ((string name, JsonNode? entry) in servers)
        {
            if (!string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry?["url"]?.GetValue<string>() is string declared
                && Uri.TryCreate(declared, UriKind.Absolute, out Uri? parsed))
            {
                url = parsed;
                return true;
            }
        }

        return false;
    }

    private static JsonObject? ReadServers(string path)
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
        catch (System.Text.Json.JsonException)
        {
            // A malformed config is the user's to fix; it must not stop the server starting.
            return null;
        }

        return root?["mcpServers"] as JsonObject;
    }
}
