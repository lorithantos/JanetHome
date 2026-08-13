using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The trace is what lets Invoke-ResearchGuard.ps1 distinguish "asked and found nothing" from
/// "never asked". Get-Research.ps1 has always written it; the port did not, which turned an
/// ENFORCED rule into one that silently stopped enforcing for CLI- and MCP-only sessions.
/// </summary>
public class ResearchTraceTests
{
    [Theory]
    [InlineData("thread items", null, null, null, "thread items")]
    [InlineData(null, "agent", null, null, "tag:agent")]
    [InlineData(null, null, "script.get-research", null, "id:script.get-research")]
    [InlineData(null, null, null, "script", "kind:script")]
    [InlineData(null, null, null, null, "orientation")]
    public void DescribesWhatWasAskedTheSameWayThePowerShellDoes(
        string? query, string? tag, string? id, string? kind, string expected)
    {
        CatalogQueryOptions options = new()
        {
            Query = query,
            Tag = tag is null ? [] : [tag],
            Id = id is null ? [] : [id],
            Kind = kind,
        };

        Assert.Equal(expected, ResearchTrace.Describe(options));
    }

    [Fact]
    public void QueryTakesPrecedenceOverEveryOtherSelector()
    {
        // Matches the PowerShell's if/elseif ladder: Query, then Tag, then Id, then Kind.
        CatalogQueryOptions options = new()
        {
            Query = "free text",
            Tag = ["agent"],
            Id = ["script.get-research"],
            Kind = "script",
        };

        Assert.Equal("free text", ResearchTrace.Describe(options));
    }

    [Fact]
    public void RecordWritesTheShapeTheGuardReads()
    {
        ResearchTrace.Record("parity-probe");

        JsonNode trace = JsonNode.Parse(File.ReadAllText(ResearchTrace.Path))!;

        Assert.NotNull(trace["lastUtc"]);
        Assert.Equal("parity-probe", trace["recent"]!.AsArray()[0]!["q"]!.GetValue<string>());
        Assert.NotNull(trace["recent"]!.AsArray()[0]!["t"]);
    }

    [Fact]
    public void RecordKeepsTheMostRecentTenAndPrependsTheNewest()
    {
        for (int i = 0; i < 14; i++)
        {
            ResearchTrace.Record($"probe-{i}");
        }

        JsonNode trace = JsonNode.Parse(File.ReadAllText(ResearchTrace.Path))!;
        JsonArray recent = trace["recent"]!.AsArray();

        Assert.Equal(10, recent.Count);
        Assert.Equal("probe-13", recent[0]!["q"]!.GetValue<string>());
        Assert.Equal("probe-4", recent[^1]!["q"]!.GetValue<string>());
    }
}
