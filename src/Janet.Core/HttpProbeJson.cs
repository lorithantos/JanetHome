using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>Serializes a probe's answer for both front ends, and renders it for a terminal.</summary>
/// <remarks>
/// One serializer for http_probe and `janet http-probe`, so the two cannot disagree about the
/// envelope. Every key is always present; a value that does not apply is null rather than absent.
/// </remarks>
public static class HttpProbeJson
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

    public static string Serialize(HttpProbeResult result, bool pretty = false) =>
        HttpProbeJson.ToJson(result).ToJsonString(pretty ? HttpProbeJson.Indented : HttpProbeJson.Compact);

    public static JsonObject ToJson(HttpProbeResult result)
    {
        JsonArray warming = [];
        foreach (int code in result.WarmingStatuses)
        {
            warming.Add(code);
        }

        JsonArray attempts = [];
        foreach (HttpProbeAttempt attempt in result.Attempts)
        {
            attempts.Add(new JsonObject
            {
                ["attempt"] = attempt.Attempt,
                ["status"] = attempt.Status,
                ["elapsedMs"] = attempt.ElapsedMs,
                ["outcome"] = attempt.Outcome,
                ["error"] = attempt.Error,
            });
        }

        JsonArray headers = [];
        foreach (HttpProbeHeader header in result.Headers)
        {
            headers.Add(new JsonObject
            {
                ["name"] = header.Name,
                ["value"] = header.Value,
            });
        }

        return new JsonObject
        {
            ["contract"] = result.Contract,
            ["url"] = result.Url,
            ["method"] = result.Method,
            ["expectStatus"] = result.ExpectStatus,
            ["warmingStatuses"] = warming,
            ["ok"] = result.Ok,
            ["status"] = result.Status,
            ["reason"] = result.Reason,
            ["elapsedMs"] = result.ElapsedMs,
            ["attempts"] = attempts,
            ["headers"] = headers,
            ["headersTruncated"] = result.HeadersTruncated,
            ["body"] = result.Body,
            ["bodyBytes"] = result.BodyBytes,
            ["bodyTruncated"] = result.BodyTruncated,
            ["error"] = result.Error,
        };
    }

    /// <summary>The same answer for a human at a terminal.</summary>
    public static string Render(HttpProbeResult result)
    {
        string verdict = result.Ok ? "OK" : "NOT OK";
        string status = result.Status is int code ? $"{code} {result.Reason}".TrimEnd() : "no response";

        List<string> lines =
        [
            $"{result.Method} {result.Url}",
            $"{verdict}  {status}  ({result.Attempts.Count} attempt(s), {result.ElapsedMs} ms)",
        ];

        if (result.Error is not null)
        {
            lines.Add($"error     {result.Error}");
        }

        foreach (HttpProbeAttempt attempt in result.Attempts)
        {
            string answered = attempt.Status is int seen ? seen.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
            lines.Add($"  #{attempt.Attempt}  {answered,-4} {attempt.Outcome,-10} {attempt.ElapsedMs} ms" +
                (attempt.Error is null ? string.Empty : $"  {attempt.Error}"));
        }

        foreach (HttpProbeHeader header in result.Headers)
        {
            lines.Add($"{header.Name}: {header.Value}");
        }

        if (result.HeadersTruncated)
        {
            lines.Add($"(headers cut at {HttpProbe.MaxHeaders}; name the ones you want)");
        }

        if (result.Body is not null)
        {
            lines.Add(string.Empty);
            lines.Add(result.Body);
            if (result.BodyTruncated)
            {
                lines.Add($"(body cut at {result.BodyBytes} bytes)");
            }
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
