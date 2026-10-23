using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// The --text view of a check: what a reader at a terminal sees instead of the envelope.
/// </summary>
public class DotnetCheckRenderTests
{
    private static string Rendered(Diagnostic error, Diagnostic fresh)
    {
        CheckResult result = new(
            @"D:\Repos\Sample\App.slnx",
            "Debug",
            false,
            new BuildReport(false, 0.3, [error], [], 0, [fresh], 0, null, null),
            null,
            null);

        return DotnetCheckJson.Render(result);
    }

    [Fact]
    public void AFileLevelDiagnosticPrintsItsFileWithoutAnEmptyLineNumber()
    {
        // MSB4126 is about the solution file as a whole, so it has no line. Formatting the null
        // printed "(App.slnx:)", which reads as a line number that went missing.
        string text = Rendered(
            new Diagnostic("App.slnx", null, "error", "MSB4126", "invalid platform"),
            new Diagnostic("App.csproj", null, "warning", "NU1603", "resolved a newer version"));

        Assert.Contains("  error MSB4126: invalid platform (App.slnx)", text);
        Assert.Contains("  NEW NU1603: resolved a newer version (App.csproj)", text);
        Assert.DoesNotContain(":)", text);
    }

    [Fact]
    public void ALineLevelDiagnosticStillPrintsItsLine()
    {
        string text = Rendered(
            new Diagnostic("src/App/Broken.cs", 7, "error", "CS0103", "The name does not exist"),
            new Diagnostic("src/App/Program.cs", 12, "warning", "CS8618", "Non-nullable property"));

        Assert.Contains("  error CS0103: The name does not exist (src/App/Broken.cs:7)", text);
        Assert.Contains("  NEW CS8618: Non-nullable property (src/App/Program.cs:12)", text);
    }

    [Fact]
    public void AnUnauthorizedGraphIsPrintedWithItsDetail()
    {
        // The reading view says nothing about the graph otherwise, which is how two days of
        // refused list_graphs calls read as "absent" without anyone seeing them.
        CheckResult result = new(
            @"D:\Repos\Sample\App.slnx",
            "Debug",
            true,
            new BuildReport(true, 0.3, [], [], 0, null, null, null, null),
            null,
            new GraphState { Via = "razorgraph", Status = "unauthorized", Detail = "razorgraph refused janet with 401." });

        Assert.Contains("GRAPH UNAUTHORIZED razorgraph refused janet with 401.", DotnetCheckJson.Render(result));
    }

    [Fact]
    public void AGraphThatAnsweredIsNotPrinted()
    {
        CheckResult result = new(
            @"D:\Repos\Sample\App.slnx",
            "Debug",
            true,
            new BuildReport(true, 0.3, [], [], 0, null, null, null, null),
            null,
            new GraphState { Via = "razorgraph", Status = "absent" });

        Assert.DoesNotContain("GRAPH", DotnetCheckJson.Render(result));
    }
}
