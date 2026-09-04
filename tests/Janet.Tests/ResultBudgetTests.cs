using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The size a tool result may reach before it is refused, and what the refusal says.
/// </summary>
/// <remarks>
/// Refusal, never truncation, is the property under test: every assertion on the message is
/// there because a caller told only "too large" has nowhere to go. Shares a collection with the
/// Surfaced tests because both read the environment override, and xunit runs collections in
/// parallel with each other.
/// </remarks>
[Collection(ResultBudgetCollection.Name)]
public class ResultBudgetTests : IDisposable
{
    private readonly string? _saved = Environment.GetEnvironmentVariable(ResultBudget.EnvironmentVariable);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(ResultBudget.EnvironmentVariable, _saved);

    [Fact]
    public void TheDefaultIsAHundredThousandCharacters()
    {
        // Claude Code's default ceiling is 25,000 tokens at roughly four characters each; a
        // 26,613-character report fit and a 174,129-character show did not. The number is
        // load-bearing enough to pin: a "small" change here changes what every client receives.
        Assert.Equal(100_000, ResultBudget.Default);
    }

    [Fact]
    public void WithinBudgetIsNull()
    {
        string text = new('x', 100_000);

        Assert.Null(ResultBudget.Refusal("thread_show", text, "pass topic.", 100_000));
    }

    [Fact]
    public void OneCharacterOverIsRefusedNamingToolSizeBudgetAndHint()
    {
        string text = new('x', 100_001);

        string? refusal = ResultBudget.Refusal("thread_show", text, "pass topic for one item.", 100_000);

        Assert.NotNull(refusal);
        Assert.StartsWith("thread_show returned 100,001 characters", refusal);
        Assert.Contains("the result budget is 100,000 (JANET_RESULT_BUDGET)", refusal);
        Assert.Contains("refused rather than cut", refusal);
        Assert.Contains("Narrow the call: pass topic for one item.", refusal);
        Assert.Contains("The `janet` CLI twin has no result limit", refusal);
    }

    [Fact]
    public void TheDefaultArmReadsTheBudgetInForce()
    {
        Environment.SetEnvironmentVariable(ResultBudget.EnvironmentVariable, "10");

        Assert.Null(ResultBudget.Refusal("research_query", new string('x', 10), "hint"));
        Assert.Contains("the result budget is 10 ", ResultBudget.Refusal("research_query", new string('x', 11), "hint"));
    }

    [Theory]
    [InlineData("50000", 50_000)]
    [InlineData(" 250000 ", 250_000)]
    [InlineData("1", 1)]
    public void APositiveIntegerOverrideIsHonoured(string raw, int expected)
    {
        Assert.Equal(expected, ResultBudget.Resolve(raw));

        Environment.SetEnvironmentVariable(ResultBudget.EnvironmentVariable, raw);
        Assert.Equal(expected, ResultBudget.Current);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("lots")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData("1e5")]
    [InlineData("100,000")]
    [InlineData("99999999999999999999")]
    public void AnythingElseFallsBackToTheDefault(string? raw)
    {
        // There is deliberately no spelling of "unbounded": a typo in an environment must land
        // on the default, not on nothing.
        Assert.Equal(ResultBudget.Default, ResultBudget.Resolve(raw));

        Environment.SetEnvironmentVariable(ResultBudget.EnvironmentVariable, raw);
        Assert.Equal(ResultBudget.Default, ResultBudget.Current);
    }
}

/// <summary>
/// Serialises the tests that read or set <see cref="ResultBudget.EnvironmentVariable"/>.
/// </summary>
[CollectionDefinition(Name)]
public class ResultBudgetCollection
{
    public const string Name = "ResultBudget environment";
}
