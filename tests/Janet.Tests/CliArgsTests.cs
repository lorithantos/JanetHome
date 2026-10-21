using System.Text.RegularExpressions;
using Janet.Cli;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The CLI's option parser, and the two sets that decide what an option is.
/// </summary>
/// <remarks>
/// Found 2026-09-16: an option missing from Args.Switches was assumed to take a value, so it
/// swallowed the next argument. `janet thread update ... --no-split --path X`, before --no-split
/// was registered, made the path its value and acted on the machine-wide live list. Nothing
/// referenced Janet.Cli then, so nothing could have caught it. Two pins now: an unknown option
/// is refused by name, and every option Program.cs reads is in the set its reader needs.
/// </remarks>
public partial class CliArgsTests
{
    private static readonly string ProgramSource =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Cli", "Program.cs.txt"));

    /// <summary>The reproduction: a registered switch leaves the next option its own value.</summary>
    [Fact]
    public void ASwitchLeavesTheNextOptionItsValue()
    {
        Args parsed = Args.Parse(["thread", "update", "--topic", "t", "--no-split", "--path", "isolated.json"]);

        Assert.True(parsed.Flag("--no-split"));
        Assert.Equal("isolated.json", parsed.Value("--path"));
    }

    /// <summary>An option the parser does not know is an error naming it, in either form.</summary>
    [Theory]
    [InlineData("--no-such-flag")]
    [InlineData("--no-such-flag=1")]
    public void AnUnknownOptionIsRefusedByName(string unknown)
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => Args.Parse(["thread", "update", "--topic", "t", unknown, "--path", "isolated.json"]));

        Assert.Contains("'--no-such-flag'", refused.Message);
    }

    /// <summary>The retired --index is still known, so Program.cs's refusal saying why is reached.</summary>
    [Fact]
    public void TheRetiredIndexOptionStillParses()
    {
        Assert.True(Args.Parse(["thread", "complete", "--index", "3"]).Has("--index"));
    }

    /// <summary>Every option Program.cs reads with Flag is a switch.</summary>
    /// <remarks>
    /// The case that cost a store: a Flag read whose option is not a switch can never be true,
    /// and before 2026-10-21 it also ate the argument after it.
    /// </remarks>
    [Fact]
    public void EveryFlagTheCliReadsIsASwitch()
    {
        string[] read = Read(FlagRead());

        Assert.True(read.Length >= 20, $"Only {read.Length} Flag reads found; the pattern has drifted from Program.cs.");
        Assert.DoesNotContain(read, o => !Args.Switches.Contains(o));
    }

    /// <summary>Every option Program.cs reads as a value is a value option.</summary>
    [Fact]
    public void EveryValueTheCliReadsIsAValueOption()
    {
        string[] read = [.. Read(ValueRead()).Concat(Read(HelperRead())).Distinct()];

        Assert.True(read.Length >= 30, $"Only {read.Length} value reads found; the pattern has drifted from Program.cs.");
        Assert.DoesNotContain(read, o => !Args.ValueOptions.Contains(o));
    }

    /// <summary>Every option Program.cs tests with Has is known to the parser.</summary>
    [Fact]
    public void EveryPresenceTheCliTestsIsKnown()
    {
        string[] read = Read(HasRead());

        Assert.NotEmpty(read);
        Assert.DoesNotContain(read, o => !Args.Switches.Contains(o) && !Args.ValueOptions.Contains(o));
    }

    /// <summary>No option is both: the parser could only honour one reading of it.</summary>
    [Fact]
    public void NoOptionIsBothASwitchAndAValueOption()
    {
        Assert.DoesNotContain(Args.Switches, Args.ValueOptions.Contains);
    }

    private static string[] Read(Regex pattern) =>
        [.. pattern.Matches(ProgramSource).Select(m => m.Groups["option"].Value).Distinct()];

    [GeneratedRegex("""\.Flag\("(?<option>--[a-z][a-z-]*)"\)""")]
    private static partial Regex FlagRead();

    [GeneratedRegex("""\.(?:Value|Values|Int)\("(?<option>--[a-z][a-z-]*)"[,)]""")]
    private static partial Regex ValueRead();

    // The research verbs read through helpers taking (blob, args, field, option).
    [GeneratedRegex("""\b(?:Required|Optional|List|Set|SetList)\([^;\n]*"(?<option>--[a-z][a-z-]*)"\)""")]
    private static partial Regex HelperRead();

    [GeneratedRegex("""\.Has\("(?<option>--[a-z][a-z-]*)"\)""")]
    private static partial Regex HasRead();
}
