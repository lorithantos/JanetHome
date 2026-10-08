using System.Text.Json.Nodes;

namespace Janet.Core;

/// <summary>Serializes a rotation's state. Every key is always present; null means not applicable.</summary>
public static class ServerRotationJson
{
    public static string Serialize(RotationStatus status, bool pretty = false)
    {
        RotationTarget t = status.Target;
        EnvironmentReport? e = status.ServingEnvironment;

        JsonObject root = new()
        {
            ["contract"] = status.Contract,
            ["server"] = status.Server,
            ["state"] = status.State,
            ["startedAt"] = status.StartedAt,
            ["finishedAt"] = status.FinishedAt,
            ["launcherPid"] = status.LauncherPid,
            ["error"] = status.Error,
            ["currentBuild"] = status.CurrentBuild,
            ["logPath"] = status.LogPath,
            ["logTail"] = new JsonArray([.. status.LogTail.Select(line => (JsonNode)line)]),
            ["target"] = new JsonObject
            {
                ["project"] = t.Project,
                ["root"] = t.Root,
                ["processName"] = t.ProcessName,
                ["serverArguments"] = new JsonArray([.. t.ServerArguments.Select(a => (JsonNode)a)]),
                ["toolProjects"] = new JsonArray([.. t.ToolProjects.Select(p => (JsonNode)p)]),
                ["port"] = t.Port,
            },
            ["servingEnvironment"] = e is null
                ? null
                : new JsonObject
                {
                    ["applied"] = e.Applied,
                    ["skippedBecause"] = e.SkippedBecause,
                    ["dropped"] = new JsonArray([.. e.Dropped.Select(n => (JsonNode)n)]),
                    ["changed"] = new JsonArray([.. e.Changed.Select(n => (JsonNode)n)]),
                    ["keptFromLauncher"] = new JsonArray([.. e.KeptFromLauncher.Select(n => (JsonNode)n)]),
                },
        };

        return root.ToJsonString(pretty ? BicepJson.Indented : BicepJson.Compact);
    }
}
