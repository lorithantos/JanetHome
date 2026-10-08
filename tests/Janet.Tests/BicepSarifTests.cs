using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Bicep's SARIF, read from output recorded with bicep 0.47.16 -- no Bicep CLI needed.
/// </summary>
/// <remarks>
/// Fixtures\bicep\warn.sarif.json and broken.sarif.json are what `bicep build --stdout
/// --diagnostics-format sarif` wrote to STDERR for warn.bicep and broken.bicep beside them, with
/// only the artifact URIs rewritten to file:///C:/Fixture/... so the expectations do not depend
/// on where the repository is cloned.
/// </remarks>
public class BicepSarifTests
{
    private static IReadOnlyList<BicepDiagnostic> Recorded(string name) =>
        Bicep.ParseSarif(File.ReadAllText(Fixture.Resolve("Fixtures", "bicep", name)));

    [Fact]
    public void ALinterFindingWithNoLevelIsAWarning()
    {
        BicepDiagnostic diagnostic = Assert.Single(Recorded("warn.sarif.json"));

        // Bicep writes no "level" for linter findings, relying on SARIF's default.
        Assert.Equal("warning", diagnostic.Level);
        Assert.Equal("no-unused-params", diagnostic.Code);
        Assert.StartsWith("Parameter \"unused\" is declared but never used.", diagnostic.Message);
    }

    [Fact]
    public void APositionIsTheOneBasedLineAndColumnBicepReported()
    {
        BicepDiagnostic diagnostic = Assert.Single(Recorded("warn.sarif.json"));

        Assert.Equal(3, diagnostic.Line);
        Assert.Equal(7, diagnostic.Column);
    }

    [Fact]
    public void AFileUriBecomesALocalPath()
    {
        Assert.Equal(@"C:\Fixture\warn.bicep", Assert.Single(Recorded("warn.sarif.json")).File);
    }

    [Fact]
    public void ACompilerErrorKeepsItsLevelAndCodeBesideTheLinterWarning()
    {
        IReadOnlyList<BicepDiagnostic> diagnostics = Recorded("broken.sarif.json");

        Assert.Equal(["warning", "error"], diagnostics.Select(d => d.Level));

        BicepDiagnostic error = diagnostics[1];
        Assert.Equal("BCP057", error.Code);
        Assert.Equal(5, error.Line);
        Assert.Equal(23, error.Column);
        Assert.Equal(@"C:\Fixture\broken.bicep", error.File);
        Assert.Contains("\"locaton\" does not exist", error.Message);
    }

    [Fact]
    public void TextBeforeTheDocumentIsSkipped()
    {
        string text = "WARNING: a restore message Bicep printed first\n" + File.ReadAllText(Fixture.Resolve("Fixtures", "bicep", "broken.sarif.json"));

        Assert.Equal(2, Bicep.ParseSarif(text).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: the Bicep CLI crashed")]
    [InlineData("{ \"runs\": [ { \"results\": [")]
    [InlineData("{\"version\":\"2.1.0\"}")]
    public void TextThatIsNotSarifYieldsNoDiagnosticsRatherThanThrowing(string text)
    {
        Assert.Empty(Bicep.ParseSarif(text));
    }

    [Fact]
    public void AResultWithNoLocationStillReportsItsRuleAndMessage()
    {
        BicepDiagnostic diagnostic = Assert.Single(Bicep.ParseSarif("""
            {"runs":[{"results":[{"ruleId":"BCP192","level":"error","message":{"text":"Unable to restore the module."}}]}]}
            """));

        Assert.Equal(new BicepDiagnostic("error", "BCP192", "Unable to restore the module.", null, null, null), diagnostic);
    }
}
