using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Janet.Core;
using Janet.Mcp.Security;
using Microsoft.Win32;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// `janet mcp-token` and `janet mcp-headers` against a store on a throwaway key,
/// HKCU\Software\Janet.Tests\&lt;guid&gt;, removed afterwards. The real HKCU\Software\Janet is never
/// read or written here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class McpTokenVerbTests : IDisposable
{
    private const string Parent = @"Software\Janet.Tests";

    private readonly string _subKey = Parent + @"\" + Guid.NewGuid().ToString("n");
    private readonly McpTokenStore _store;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public McpTokenVerbTests()
    {
        _store = new McpTokenStore(_subKey);
        Assert.NotEqual(McpTokenStore.DefaultSubKey, _store.SubKey);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);

        // The parent too, once no other test's key is under it.
        using (RegistryKey? parent = Registry.CurrentUser.OpenSubKey(Parent))
        {
            if (parent is null || parent.SubKeyCount > 0 || parent.ValueCount > 0)
            {
                return;
            }
        }

        try { Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false); }
        catch (InvalidOperationException) { /* another test created a key under it meanwhile */ }
    }

    [Fact]
    public void HeadersPrintsExactlyTheHeaderObjectAHeadersHelperMustEmit()
    {
        string token = _store.Ensure();

        int exit = McpTokenVerb.Headers(_store, _out, _err);

        Assert.Equal(0, exit);
        Assert.Equal("{\"Authorization\":\"Bearer " + token + "\"}", _out.ToString());
        Assert.Equal(string.Empty, _err.ToString());
    }

    [Fact]
    public void HeadersWithNoTokenPrintsNothingAndExitsNonZeroNamingEnsure()
    {
        int exit = McpTokenVerb.Headers(_store, _out, _err);

        Assert.NotEqual(0, exit);
        Assert.Equal(string.Empty, _out.ToString());
        Assert.Contains("janet mcp-token --ensure", _err.ToString());
    }

    [Fact]
    public void EnsureCreatesATokenAndPrintsAStatusWithoutIt()
    {
        int exit = McpTokenVerb.Token(ensure: true, print: false, rotate: false, _store, _out, _err);
        string? token = _store.Read();

        Assert.Equal(0, exit);
        Assert.NotNull(token);
        JsonObject status = Assert.IsType<JsonObject>(JsonNode.Parse(_out.ToString()));
        Assert.True(status["ok"]!.GetValue<bool>());
        Assert.True(status["created"]!.GetValue<bool>());
        Assert.Equal(_store.Location, status["location"]!.GetValue<string>());
        Assert.DoesNotContain(token, _out.ToString());
        Assert.Single(_out.ToString().TrimEnd().Split('\n'));
    }

    [Fact]
    public void EnsureLeavesAnExistingTokenAlone()
    {
        string before = _store.Ensure();

        int exit = McpTokenVerb.Token(ensure: true, print: false, rotate: false, _store, _out, _err);

        Assert.Equal(0, exit);
        Assert.Equal(before, _store.Read());
        Assert.False(JsonNode.Parse(_out.ToString())!["created"]!.GetValue<bool>());
        Assert.DoesNotContain(before, _out.ToString());
    }

    [Fact]
    public void PrintWritesOnlyTheTokenToStdout()
    {
        string token = _store.Ensure();

        int exit = McpTokenVerb.Token(ensure: false, print: true, rotate: false, _store, _out, _err);

        Assert.Equal(0, exit);
        Assert.Equal(token, _out.ToString().TrimEnd());
        Assert.Equal(string.Empty, _err.ToString());
    }

    [Fact]
    public void PrintWithNoTokenExitsOneNamingEnsure()
    {
        int exit = McpTokenVerb.Token(ensure: false, print: true, rotate: false, _store, _out, _err);

        Assert.Equal(1, exit);
        Assert.Equal(string.Empty, _out.ToString());
        Assert.Contains("--ensure", _err.ToString());
    }

    [Fact]
    public void RotateReplacesTheTokenAndSaysWhenServersPickItUp()
    {
        string before = _store.Ensure();

        int exit = McpTokenVerb.Token(ensure: false, print: false, rotate: true, _store, _out, _err);
        string? after = _store.Read();

        Assert.Equal(0, exit);
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
        Assert.Contains("within 30 seconds", _out.ToString());
        Assert.DoesNotContain(after, _out.ToString());
        Assert.DoesNotContain(before, _out.ToString());
    }

    [Fact]
    public void AnUnreadableValueIsReportedAndNotReplacedByEnsure()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(_subKey))
        {
            key.SetValue(McpTokenStore.ValueName, "not a blob", RegistryValueKind.String);
        }

        int exit = McpTokenVerb.Token(ensure: true, print: false, rotate: false, _store, _out, _err);

        Assert.Equal(1, exit);
        Assert.False(JsonNode.Parse(_out.ToString())!["ok"]!.GetValue<bool>());
        using RegistryKey reread = Registry.CurrentUser.OpenSubKey(_subKey)!;
        Assert.Equal("not a blob", reread.GetValue(McpTokenStore.ValueName));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void AnythingButExactlyOneActionIsAUsageError(bool ensure, bool print, bool rotate)
    {
        int exit = McpTokenVerb.Token(ensure, print, rotate, _store, _out, _err);

        Assert.Equal(2, exit);
        Assert.Null(_store.Read());
    }
}
