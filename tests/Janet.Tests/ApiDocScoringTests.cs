using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Asserts the ranking weights directly, because the envelope does not carry them.
/// </summary>
/// <remarks>
/// The golden tests compare the ORDER of a shortlist, which only notices a weight change big
/// enough to reorder something. Changing the name-match weight from 40 to 41 reorders nothing and
/// failed no golden -- the same blind spot the catalog's scoring had, found the same way, by
/// mutating the number and watching the suite stay green.
///
/// So these pin the numbers. They are a contract in their own right: the weights encode that a
/// member's own name beats the type it lives on, which beats prose about either, and a port that
/// quietly reweighted them would rank a real library differently while every ordering test still
/// passed.
/// </remarks>
public class ApiDocScoringTests
{
    private static ApiMember Member(
        string name,
        string declaring = "",
        string summary = "",
        params (string Name, string Doc)[] parameters) =>
        new()
        {
            Id = $"M:{declaring}.{name}",
            Kind = "Method",
            Name = name,
            Declaring = declaring,
            Signature = name,
            Summary = summary,
            Parameters = [.. parameters.Select(p => new ApiParameter(p.Name, p.Doc))],
        };

    [Fact]
    public void AnExactNameMatchScoresOneHundredAndStopsThere()
    {
        // 'continue' after the exact hit is deliberate: the declaring and summary bonuses do not
        // also apply. A member called Draw on a type called Draw must not score 165.
        ApiMember member = Member("Draw", declaring: "Sample.Draw", summary: "Draw the thing.");

        Assert.Equal(100, ApiDoc.Score(member, ["Draw"]));
    }

    [Fact]
    public void AnExactNameMatchIsCaseInsensitive()
    {
        ApiMember member = Member("Draw", summary: "Documented.");

        Assert.Equal(100, ApiDoc.Score(member, ["draw"]));
    }

    [Theory]
    [InlineData("Redrawn", "", "Documented.", 40)]              // substring of the name
    [InlineData("Show", "Sample.Drawing", "Documented.", 25)]   // the declaring type
    [InlineData("Show", "", "Draws it.", 10)]                   // prose only
    public void EachFieldCarriesItsOwnWeight(string name, string declaring, string summary, int expected)
    {
        Assert.Equal(expected, ApiDoc.Score(Member(name, declaring, summary), ["draw"]));
    }

    [Fact]
    public void TheWeightsAddUpAcrossFields()
    {
        ApiMember member = Member("Redrawn", declaring: "Sample.Drawing", summary: "Draws it.");

        Assert.Equal(75, ApiDoc.Score(member, ["draw"]));
    }

    [Fact]
    public void AParameterMatchScoresFiveOnceHoweverManyMatch()
    {
        ApiMember member = Member(
            "Show",
            summary: "Documented.",
            parameters: [("drawTarget", "where"), ("drawStyle", "how")]);

        Assert.Equal(5, ApiDoc.Score(member, ["draw"]));
    }

    [Fact]
    public void AParameterDocMatchesAsWellAsItsName()
    {
        ApiMember member = Member("Show", summary: "Documented.", parameters: [("target", "what to draw on")]);

        Assert.Equal(5, ApiDoc.Score(member, ["draw"]));
    }

    [Fact]
    public void TermsScoreIndependently()
    {
        ApiMember member = Member("Draw", declaring: "Sample.Series", summary: "Documented.");

        // 100 for the exact 'draw', 25 for 'series' matching the declaring type.
        Assert.Equal(125, ApiDoc.Score(member, ["draw", "series"]));
    }

    [Fact]
    public void AnUndocumentedMemberLosesFifteen()
    {
        ApiMember documented = Member("Redrawn", summary: "Documented.");
        ApiMember undocumented = Member("Redrawn");

        Assert.Equal(40, ApiDoc.Score(documented, ["draw"]));
        Assert.Equal(25, ApiDoc.Score(undocumented, ["draw"]));
    }

    [Fact]
    public void ThePenaltyNeverTakesAMatchBelowOne()
    {
        // An undocumented member matching only through a parameter scores 5, and 5 - 15 is
        // negative. It stays a match, because it is still sometimes the right answer -- it just
        // loses every tie.
        ApiMember member = Member("Show", parameters: [("drawTarget", "where")]);

        Assert.Equal(1, ApiDoc.Score(member, ["draw"]));
    }

    [Fact]
    public void SomethingThatMatchesNothingScoresZero()
    {
        Assert.Equal(0, ApiDoc.Score(Member("Show", summary: "Documented."), ["zzzznotathing"]));
    }
}
