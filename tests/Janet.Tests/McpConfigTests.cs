using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The port a client dials is declared in .mcp.json. A server that computes its own instead
/// creates a second source of truth for an address already written down, and the two agree
/// only by luck -- the failure being a client dialling somewhere nothing answers, silently.
/// </summary>
public class McpConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "janet-cfg-" + Guid.NewGuid().ToString("n")[..8]);

    public McpConfigTests() => Directory.CreateDirectory(_root);

    private void WriteConfig(string json) =>
        File.WriteAllText(Path.Combine(_root, McpConfig.FileName), json);

    [Fact]
    public void ReadsThePortTheClientWasToldToDial()
    {
        WriteConfig("""
            { "mcpServers": { "janet": { "type": "http", "url": "http://127.0.0.1:7717/" } } }
            """);

        Assert.True(McpConfig.TryReadPort(_root, "janet", out int port, out string? source));
        Assert.Equal(7717, port);
        Assert.Contains("janet", source);
    }

    [Fact]
    public void PrefersTheNamedEntryOverAnyOther()
    {
        WriteConfig("""
            {
              "mcpServers": {
                "other": { "type": "http", "url": "http://127.0.0.1:9001/" },
                "janet": { "type": "http", "url": "http://127.0.0.1:9002/" }
              }
            }
            """);

        Assert.True(McpConfig.TryReadPort(_root, "janet", out int port, out _));
        Assert.Equal(9002, port);
    }

    [Fact]
    public void IgnoresStdioEntriesBecauseTheyHaveNoPort()
    {
        WriteConfig("""
            { "mcpServers": { "janet": { "command": "janet-mcp", "args": ["--base", "X"] } } }
            """);

        Assert.False(McpConfig.TryReadPort(_root, "janet", out _, out _));
    }

    [Fact]
    public void AMalformedConfigDoesNotStopTheServerStarting()
    {
        WriteConfig("{ this is not json");

        // Reported as "nothing declared" rather than thrown: a broken config is the user's to
        // fix, and it must not be able to prevent the tool running at all.
        Assert.False(McpConfig.TryReadPort(_root, "janet", out _, out _));
    }

    [Fact]
    public void NoConfigAtAllIsNotAnError()
    {
        Assert.False(McpConfig.TryReadPort(_root, "janet", out _, out _));
    }

    [Fact]
    public void DerivedPortIsStableAndOutsideTheEphemeralRange()
    {
        int a = GraphLocator.DerivePort(@"D:\Repos\JanetHome");
        int b = GraphLocator.DerivePort(@"d:\repos\janethome\");

        // Same base, same port, regardless of casing or trailing separator -- the derivation
        // is only useful if it is genuinely pure.
        Assert.Equal(a, b);

        // 49152-65535 is what Windows hands out for ephemeral outbound sockets; a stable
        // address must not live there.
        Assert.InRange(a, 20000, 29999);
        Assert.NotEqual(a, GraphLocator.DerivePort(@"D:\Repos\RazorGraphTool"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a leftover temp dir is not worth failing a test over */ }

        GC.SuppressFinalize(this);
    }
}
