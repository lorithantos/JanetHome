using System.Reflection;
using Janet.Core;
using Janet.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Holds the server's tool surface to the narrowing table, and the filter to refusing rather
/// than cutting.
/// </summary>
/// <remarks>
/// The table is keyed by MCP tool name and the tools are declared by attribute, so nothing in
/// the compiler ties one to the other. This is the tie, in the shape Test-OutputContracts uses
/// for its samplers: enumerate the declared surface, fail on a name with no row, fail on a row
/// with no name. Reflection over the real assembly rather than a hand-kept list, because a
/// hand-kept list is a second copy of the surface and drifts the same way.
/// </remarks>
[Collection(ResultBudgetCollection.Name)]
public class ToolSurfaceTests : IDisposable
{
    private readonly string? _saved = Environment.GetEnvironmentVariable(ResultBudget.EnvironmentVariable);

    public ToolSurfaceTests() =>
        Environment.SetEnvironmentVariable(ResultBudget.EnvironmentVariable, null);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(ResultBudget.EnvironmentVariable, _saved);

    /// <summary>Every tool name the server would register, read from the attributes.</summary>
    /// <remarks>
    /// An attribute with no Name is still a tool: the SDK registers it under the METHOD name.
    /// Reading only the attribute's Name would drop exactly that tool from this set, and a tool
    /// absent from both the set and the table passes every assertion here while being refused at
    /// runtime with the generic hint -- the one case this test exists to prevent. Found in review
    /// 2026-09-03; every tool in the repo names itself today, so nothing manifested.
    /// </remarks>
    private static IReadOnlySet<string> DeclaredTools() =>
        typeof(Surfaced).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(method => (Method: method, Tool: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(pair => pair.Tool is not null)
            .Select(pair => pair.Tool!.Name ?? pair.Method.Name)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void TheSurfaceIsNotEmpty()
    {
        // Guards the two tests below against passing vacuously: an empty set has a row for
        // every member of itself.
        Assert.True(DeclaredTools().Count >= 14, "fewer than 14 tools found -- did the reflection stop reaching them?");
    }

    [Fact]
    public void EveryToolHasANarrowingHint()
    {
        string[] missing = [.. DeclaredTools().Except(Narrowing.Hints.Keys).Order(StringComparer.Ordinal)];

        Assert.True(
            missing.Length == 0,
            "tools with no row in Narrowing.Hints: " + string.Join(", ", missing) +
            " -- a result over budget from one of these would be refused with nowhere to go.");
    }

    [Fact]
    public void EveryHintNamesARealTool()
    {
        string[] orphans = [.. Narrowing.Hints.Keys.Except(DeclaredTools()).Order(StringComparer.Ordinal)];

        Assert.True(
            orphans.Length == 0,
            "rows in Narrowing.Hints for tools that do not exist: " + string.Join(", ", orphans));
    }

    [Fact]
    public void NoHintIsBlank()
    {
        // A row exists so the refusal can say what to do; an empty row satisfies the
        // conformance test and says nothing.
        foreach ((string tool, string hint) in Narrowing.Hints)
        {
            Assert.False(string.IsNullOrWhiteSpace(hint), tool + " has a blank hint");
        }
    }

    [Fact]
    public void AResultWithinBudgetPassesThrough()
    {
        CallToolResult result = TextResult(new string('x', ResultBudget.Default));

        Assert.Null(Surfaced.Refusal("thread_show", result));
    }

    [Fact]
    public void AResultOverBudgetIsRefusedWithTheToolsOwnHint()
    {
        CallToolResult result = TextResult(new string('x', ResultBudget.Default + 1));

        string? refusal = Surfaced.Refusal("thread_show", result);

        Assert.NotNull(refusal);
        Assert.StartsWith("thread_show returned 100,001 characters", refusal);
        Assert.Contains(Narrowing.Hints["thread_show"], refusal);
        Assert.Contains("`janet` CLI twin has no result limit", refusal);
        // And the text itself is not in the refusal: nothing was cut down and passed along.
        Assert.DoesNotContain("xxxxxxxxxx", refusal);
    }

    [Fact]
    public void TheSizeIsTheSumOfEveryTextBlock()
    {
        // Two blocks of half the budget each fit; one more character does not. A measurement
        // of the first block alone would let a multi-block result through unbounded.
        int half = ResultBudget.Default / 2;
        CallToolResult fits = new() { Content = [new TextContentBlock { Text = new string('a', half) }, new TextContentBlock { Text = new string('b', half) }] };
        CallToolResult over = new() { Content = [new TextContentBlock { Text = new string('a', half) }, new TextContentBlock { Text = new string('b', half + 1) }] };

        Assert.Null(Surfaced.Refusal("research_query", fits));
        Assert.NotNull(Surfaced.Refusal("research_query", over));
    }

    [Fact]
    public void AnUnknownToolIsStillRefusedWithAGenericHint()
    {
        // The conformance test above should make this unreachable; if it is reached anyway,
        // the refusal must still be a refusal and must say that the row is missing.
        string? refusal = Surfaced.Refusal("not_a_tool", TextResult(new string('x', ResultBudget.Default + 1)));

        Assert.NotNull(refusal);
        Assert.Contains("no narrowing hint recorded", refusal);
    }

    private static CallToolResult TextResult(string text) =>
        new() { Content = [new TextContentBlock { Text = text }] };
}
