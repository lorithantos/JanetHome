using System.Text.Json.Nodes;
using Janet.Core;
using Xunit;

namespace Janet.Tests;

/// <summary>
/// Pins the free-text weights directly, one signal at a time.
/// </summary>
/// <remarks>
/// The goldens cannot do this. A score never reaches the envelope -- only the order it produces
/// does -- so any change to a weight that preserves the ranking of all twenty-seven recorded
/// queries is invisible to them. Measured: moving the tag-exact weight from 40 to 41 fails
/// nothing there. The weights were tuned against this corpus and the comment on Score says so,
/// which makes them exactly the kind of constant that drifts a point at a time until the tuning
/// is gone.
///
/// This is a change-detector test on purpose. Retuning is a decision, and a decision should
/// have to be written down twice.
/// </remarks>
public class ScoringTests
{
    /// <summary>An exact id short-circuits: nothing else on the node is counted.</summary>
    [Fact]
    public void AnExactIdScoresAHundredAndStopsThere() =>
        Assert.Equal(100, CatalogQuery.Score(Node(id: "term", summary: "the term again"), ["term"]));

    /// <summary>Tags are curated, so a tag hit is much stronger evidence than prose.</summary>
    [Fact]
    public void AnExactTagScoresForty() =>
        Assert.Equal(40, CatalogQuery.Score(Node(tags: ["term"]), ["term"]));

    [Fact]
    public void ATagContainingTheTermScoresTwenty() =>
        Assert.Equal(20, CatalogQuery.Score(Node(tags: ["termite"]), ["term"]));

    [Fact]
    public void AnIdContainingTheTermScoresFifteen() =>
        Assert.Equal(15, CatalogQuery.Score(Node(id: "sandbox.term-holder"), ["term"]));

    [Fact]
    public void ASummaryHitScoresTen() =>
        Assert.Equal(10, CatalogQuery.Score(Node(summary: "mentions the term once"), ["term"]));

    /// <summary>An id hit and a summary hit are independent signals and add.</summary>
    [Fact]
    public void SignalsAccumulate() =>
        Assert.Equal(65, CatalogQuery.Score(
            Node(id: "sandbox.term-holder", summary: "the term again", tags: ["term"]),
            ["term"]));

    [Fact]
    public void ACaveatHitDemotesByFive() =>
        Assert.Equal(5, CatalogQuery.Score(
            Node(summary: "mentions the term once", caveats: ["the term is also here"]),
            ["term"]));

    /// <summary>
    /// A caveat alone never selects: a node must not surface for documenting its own breakage.
    /// </summary>
    [Fact]
    public void ACaveatAloneScoresNothing() =>
        Assert.Equal(0, CatalogQuery.Score(Node(caveats: ["the term appears only here"]), ["term"]));

    /// <summary>
    /// The floor keeps a heavily demoted node findable -- a broken tool you can see beats one
    /// you cannot.
    /// </summary>
    [Fact]
    public void ADemotedNodeFloorsAtOneRatherThanDisappearing() =>
        Assert.Equal(1, CatalogQuery.Score(
            Node(summary: "term", caveats: ["term other"]),
            ["term", "other"]));

    /// <summary>Terms score independently, so several weak hits beat one strong one.</summary>
    [Fact]
    public void TermsScoreIndependently() =>
        Assert.Equal(20, CatalogQuery.Score(
            Node(summary: "the first and the second appear here"),
            ["first", "second"]));

    [Fact]
    public void AMissScoresNothing() =>
        Assert.Equal(0, CatalogQuery.Score(Node(summary: "nothing relevant"), ["term"]));

    private static ResearchNode Node(
        string id = "sandbox.node",
        string summary = "an unremarkable summary",
        string[]? tags = null,
        string[]? caveats = null)
    {
        JsonObject json = new()
        {
            ["id"] = id,
            ["kind"] = "note",
            ["path"] = "notes\\nothing.md",
            ["summary"] = summary,
        };

        if (caveats is not null)
        {
            json["caveats"] = new JsonArray([.. caveats.Select(c => (JsonNode)JsonValue.Create(c)!)]);
        }

        if (tags is not null)
        {
            json["tags"] = new JsonArray([.. tags.Select(t => (JsonNode)JsonValue.Create(t)!)]);
        }

        return new ResearchNode(json);
    }
}
