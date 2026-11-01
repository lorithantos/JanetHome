using Janet.Core;
using Xunit;

namespace Janet.Tests;

public class StorageCopySpecTests
{
    private const string Src = "src";

    [Fact]
    public void PatternCapturesOneSegmentPerPlaceholder()
    {
        var p = StoragePattern.Parse("drafts/{id}/{stamp}.json");
        var m = p.Match("drafts/0a1b/20261031T101500Z.json", "c");
        Assert.NotNull(m);
        Assert.Equal("0a1b", m!["id"]);
        Assert.Equal("20261031T101500Z", m["stamp"]);
        Assert.Null(p.Match("drafts/a/b/c.json", "c"));
    }

    [Fact]
    public void ClientPlaceholderMatchesOnlyTheGivenClient()
    {
        var p = StoragePattern.Parse("{client}/{stamp}.json");
        Assert.NotNull(p.Match("test-salon-one/x.json", "test-salon-one"));
        Assert.Null(p.Match("test-salon-one/x.json", "staging-salon"));
    }

    [Fact]
    public void ExpandWritesTheTargetClient()
    {
        var p = StoragePattern.Parse("{client}/{stamp}.json");
        var captures = new Dictionary<string, string> { ["stamp"] = "x" };
        Assert.Equal("staging-salon/x.json", p.Expand(captures, "staging-salon"));
    }

    [Fact]
    public void RowKeyPatternSplitsOnTheLiteralBetweenPlaceholders()
    {
        var p = StoragePattern.Parse("{id}_{stamp}");
        var m = p.Match("0a1b_20261031T101500Z", "c");
        Assert.NotNull(m);
        Assert.Equal("0a1b", m!["id"]);
        Assert.Equal("20261031T101500Z", m["stamp"]);
    }

    [Fact]
    public void TheNeelamSpecParses()
    {
        var spec = StorageCopySpec.Parse(StorageCopyFixtures.Neelam, "neelam.json");
        Assert.Single(spec.Pairs);
        Assert.Equal(2, spec.Containers.Count);
        Assert.Equal(2, spec.Tables.Count);
        Assert.Equal("drafts/{id}/{stamp}.json", spec.Containers[0].Rules[3].KeepIfLive!.Text);
    }

    [Theory]
    [InlineData("""{ "match": "a/{id}.json", "copyy": false }""", null, "copyy")]
    [InlineData("""{ "match": "proofread/{id}/{stamp}.json", "keepIfLive": "drafts/{other}/{stamp}.json" }""", null, "other")]
    [InlineData("""{ "match": "proofread/{id}.json", "keepIfLive": "nowhere/{id}.json" }""", null, "nowhere/{id}.json")]
    [InlineData(null, "dupPair", "p")]
    [InlineData(null, "tableNoRowKey", "id")]
    public void ARefusedSpecThrowsNamingTheProblem(string? rule, string? special, string expected)
    {
        var pair = """{ "name": "p", "source": { "account": "src", "client": "a" }, "target": { "account": "tgt", "client": "b" } }""";
        var pairs = special == "dupPair" ? pair + "," + pair : pair;
        var containers = rule is null
            ? "[]"
            : $$"""[ { "name": "{client}", "rules": [ {{rule}} ] } ]""";
        var tables = special == "tableNoRowKey"
            ? """[ { "name": "t", "partitionKey": "{client}", "keepIfLive": { "container": "{client}", "blob": "drafts/{id}.json" } } ]"""
            : "[]";
        var json = $$"""{ "pairs": [ {{pairs}} ], "containers": {{containers}}, "tables": {{tables}} }""";

        var ex = Assert.Throws<GraphException>(() => StorageCopySpec.Parse(json, "bad.json"));
        Assert.Contains(expected, ex.Message);
        Assert.Contains("bad.json", ex.Message);
    }

    [Fact]
    public void LoadRefusesARelativePath()
    {
        var ex = Assert.Throws<GraphException>(() => StorageCopySpec.Load("storage-copy.json"));
        Assert.Contains("absolute", ex.Message);
    }

    [Fact]
    public void GuardReturnsTheNamedPair()
    {
        var spec = StorageCopySpec.Parse(StorageCopyFixtures.Neelam, "neelam.json");
        Assert.Equal("tgt", spec.Guard("priya-to-staging").Target.Account);
    }

    private static string SpecWithPairs(params string[] pairs) =>
        $$"""{ "pairs": [ {{string.Join(",", pairs)}} ], "containers": [], "tables": [] }""";

    private static string Pair(string name, string sa, string sc, string ta, string tc) =>
        $$"""{ "name": "{{name}}", "source": { "account": "{{sa}}", "client": "{{sc}}" }, "target": { "account": "{{ta}}", "client": "{{tc}}" } }""";

    [Fact]
    public void GuardRefusesAnUnknownPair()
    {
        var spec = StorageCopySpec.Parse(StorageCopyFixtures.Neelam, "neelam.json");
        var ex = Assert.Throws<GraphException>(() => spec.Guard("nope"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void GuardRefusesAnAccountThatIsASourceAndATarget()
    {
        var spec = StorageCopySpec.Parse(SpecWithPairs(
            Pair("fwd", Src, "a", "tgt", "b"),
            Pair("back", "other", "c", Src, "d")), "x.json");
        var ex = Assert.Throws<GraphException>(() => spec.Guard("fwd"));
        Assert.Contains("src", ex.Message);
        // guard 3 checks every pair, not only the named one
        Assert.Throws<GraphException>(() => spec.Guard("back"));
    }

    [Fact]
    public void GuardRefusesACaseDifferingSelfPair()
    {
        var spec = StorageCopySpec.Parse(SpecWithPairs(Pair("self", "Acct", "Salon", "acct", "salon")), "x.json");
        var ex = Assert.Throws<GraphException>(() => spec.Guard("self"));
        Assert.Contains("same", ex.Message);
    }

    [Fact]
    public void GuardRefusesACaseDifferingReversePair()
    {
        var spec = StorageCopySpec.Parse(SpecWithPairs(
            Pair("fwd", "Acct", "a", "tgt", "b"),
            Pair("back", "other", "c", "acct", "d")), "x.json");
        var ex = Assert.Throws<GraphException>(() => spec.Guard("fwd"));
        Assert.Contains("Acct", ex.Message);
    }

    [Fact]
    public void GuardRefusesSourceEqualToTarget()
    {
        var spec = StorageCopySpec.Parse(SpecWithPairs(Pair("self", Src, "a", Src, "a")), "x.json");
        var ex = Assert.Throws<GraphException>(() => spec.Guard("self"));
        Assert.Contains("same", ex.Message);
    }
}
