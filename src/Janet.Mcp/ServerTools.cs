using System.ComponentModel;
using Janet.Core;
using ModelContextProtocol.Server;

namespace Janet.Mcp;

/// <summary>
/// Rotating the MCP servers themselves -- janet-mcp and razorgraph-mcp -- without a shell.
/// </summary>
/// <remarks>
/// Two tools, read and write, for the reason AzureTools gives: the permission prompt is per tool,
/// so reading a rotation's outcome can be allow-listed while starting one is still asked about.
/// </remarks>
[McpServerToolType]
public static class ServerTools
{
    [McpServerTool(Name = "server_rotate")]
    [Description(
        "RESTARTS A SERVER. Rotates janet-mcp or razorgraph-mcp onto a fresh build of its source: " +
        "runs scripts\\Update-McpServer.ps1 (publish to a new build directory, repoint the " +
        "'current' junction, restart the HTTP server, wait for it to listen, and for janet " +
        "reinstall the janet CLI). The script runs DETACHED and this answers state 'started' at " +
        "once, because rotating janet replaces the very process answering. Attached sessions " +
        "reconnect on their NEXT call to that server -- make one, then read the outcome with " +
        "server_rotation. The new server starts with a clean environment (registry machine + " +
        "user, logon variables, JANET_*), never the launching terminal's. Refused while a " +
        "rotation of the same server is still running.")]
    public static string Rotate(
        GraphContext context,
        [Description("janet or razorgraph.")]
        string server) =>
        ServerRotationJson.Serialize(ServerRotation.Start(server, JanetBase(context)));

    [McpServerTool(Name = "server_rotation")]
    [Description(
        "READ-ONLY. The last rotation of janet-mcp or razorgraph-mcp: state (never, started, " +
        "running, succeeded, failed, or unknown when the launcher vanished without a result), " +
        "the script's error verbatim when it failed (a failed publish names the locked file), " +
        "the build the 'current' junction points at, and the last lines of the rotation log. " +
        "'servingEnvironment' says which variables the server answering THIS call dropped from " +
        "its launcher's environment -- by name only, never values.")]
    public static string Rotation(
        GraphContext context,
        [Description("janet or razorgraph.")]
        string server) =>
        ServerRotationJson.Serialize(ServerRotation.Status(server, JanetBase(context)));

    private static string JanetBase(GraphContext context) =>
        Path.GetDirectoryName(context.GraphPath)
            ?? throw new GraphException($"Cannot tell the JanetHome checkout from {context.GraphPath}.");
}
