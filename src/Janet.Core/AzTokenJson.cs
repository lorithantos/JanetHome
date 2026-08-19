using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>Serializes a token answer, and renders it for a terminal.</summary>
public static class AzTokenJson
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

    public static string Serialize(AzTokenResult result, bool pretty = false)
    {
        JsonObject root = new()
        {
            ["contract"] = result.Contract,
            ["requested"] = result.Requested,
            ["scope"] = result.Scope,
            ["resource"] = result.Resource,
            ["tenant"] = result.Tenant,
            ["tokenType"] = result.TokenType,

            // Round-trip format, not the local one: a token's lifetime is the same fact in every
            // timezone, and a reader that has to guess which one this was written in has been
            // handed an ambiguity instead of a timestamp.
            //
            // UtcDateTime rather than ToUniversalTime(): both are UTC, but "o" on a
            // DateTimeOffset writes the offset as +00:00 while the same format on a UTC DateTime
            // writes Z. Every other timestamp this codebase emits is the Z form, and a format
            // that is right on its own and different from its neighbours is a papercut for
            // whoever writes the parser.
            ["expiresOn"] = result.ExpiresOn.UtcDateTime.ToString("o"),
            ["expiresInSeconds"] = result.ExpiresInSeconds,
            ["cached"] = result.Cached,
            ["servedBy"] = result.ServedBy == ServedBy.Server ? "server" : "process",
        };

        // The key is present only when the value is. An always-present "token": null invites a
        // reader to treat null as "there is no token", when the truth is "you did not ask for
        // it" -- and those two want different actions from whoever hit the difference.
        if (result.Token is not null)
        {
            root["token"] = result.Token;
        }

        return root.ToJsonString(pretty ? Indented : Compact);
    }

    /// <summary>
    /// Reads an envelope back into a result.
    /// </summary>
    /// <remarks>
    /// Exists for one caller: the CLI, receiving an envelope a janet-mcp serialized. Parsing it
    /// back rather than passing the bytes through is what lets the CLI stamp servedBy and render
    /// --text from the same object as a local answer, so the two paths cannot present
    /// differently. A missing key is a malformed envelope and throws, because the alternative is
    /// a default that quietly describes a token nobody issued.
    /// </remarks>
    public static AzTokenResult Parse(string json)
    {
        JsonObject root = JsonNode.Parse(json) as JsonObject
            ?? throw new GraphException("The token envelope is not a JSON object.");

        // The server's considered refusal, forwarded verbatim. Re-deriving it locally would
        // reach the identical message a second and slower time.
        if (root["error"]?.GetValue<string>() is string error)
        {
            throw new GraphException(error);
        }

        string Required(string key) =>
            root[key]?.GetValue<string>() ?? throw new GraphException($"The token envelope has no '{key}'.");

        return new AzTokenResult
        {
            Requested = Required("requested"),
            Scope = Required("scope"),
            Resource = root["resource"]?.GetValue<string>(),
            Tenant = root["tenant"]?.GetValue<string>(),
            TokenType = Required("tokenType"),
            ExpiresOn = DateTimeOffset.Parse(Required("expiresOn"), System.Globalization.CultureInfo.InvariantCulture),
            ExpiresInSeconds = root["expiresInSeconds"]?.GetValue<int>()
                ?? throw new GraphException("The token envelope has no 'expiresInSeconds'."),
            Cached = root["cached"]?.GetValue<bool>() ?? false,
            Token = root["token"]?.GetValue<string>(),
        };
    }

    /// <summary>The same answer for a human at a terminal.</summary>
    public static string Render(AzTokenResult result)
    {
        List<string> lines =
        [
            $"scope     {result.Scope}" + (result.Resource is null ? string.Empty : $"  ({result.Resource})"),
            $"tenant    {result.Tenant ?? "(whatever az is signed in to)"}",
            $"expires   {result.ExpiresOn.ToUniversalTime():u} -- in {Humanize(result.ExpiresInSeconds)}",
            $"source    {Source(result)}",
        ];

        lines.Add(result.Token is null

            // Naming the flag is the point: the default is a deliberate omission, and a reader
            // who does not know how to opt out will read the absence as a failure.
            ? "token     (not shown -- pass --raw)"
            : $"token     {result.Token}");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>Where the answer came from, in the order a reader wants it.</summary>
    /// <remarks>
    /// Borrowed-from-a-server is worth saying before cached-or-not, because it is the fact that
    /// explains an implausibly fast CLI call. "cached" alone would look like the CLI had a cache
    /// it cannot have.
    /// </remarks>
    private static string Source(AzTokenResult result) => result.ServedBy switch
    {
        ServedBy.Server => result.Cached ? "janet-mcp cache" : "janet-mcp, freshly acquired",
        _ => result.Cached ? "process cache" : "az account get-access-token",
    };

    private static string Humanize(int seconds) =>
        seconds >= 3600
            ? $"{seconds / 3600}h {seconds % 3600 / 60}m"
            : seconds >= 60 ? $"{seconds / 60}m" : $"{seconds}s";
}
